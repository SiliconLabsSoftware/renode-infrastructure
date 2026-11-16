//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Analog;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // A resistive load on a switched mains output, as the voltage and current sense inputs of
    // the controlling MCU's ADC see it.
    //
    // While the output conducts, line voltage appears across the load and a sense channel
    // behind a biased divider swings with the line; with the output off the channel reads only
    // its bias. Firmware that measures load voltage, current or power needs that swing, and a
    // constant ADC input cannot provide it.
    //
    // The inputs are the drives - any number of them, numbered from 0 - and the load CONDUCTS
    // WHILE ANY INPUT IS HIGH, sample by sample: a line-frequency sine of `AmplitudeMicrovolts`
    // is on the ADC's voltage channel exactly while a drive is high, and the channel reads its
    // bias otherwise. A drive held for part of each half cycle therefore produces a phase-cut
    // waveform, forward or reverse.
    //
    // It also DRAWS current, when told how much. `LoadWatts` is the load's rating at
    // `LineVoltsRms`; while conducting, the current - in phase with the voltage, I = P / V - is
    // placed on every current-sense channel added with `AddCurrentSense`, each scaled by its own
    // sensitivity in microvolts per ampere. LoadWatts 0 (the default) draws no current.
    //
    // AND IT METERS ITSELF. Because the sine and the drives are both functions of virtual time,
    // the power a perfect meter would read for the window actually applied is exact:
    // `DeliveredWatts` = LoadWatts x (2/T) x integral over the last full cycle's conduction of
    // sin^2, accumulated analytically at every drive edge and half-cycle boundary (no sampling).
    // `ConductionDegrees` is the window's average width per half cycle. A test can compare what
    // firmware measured against what the model delivered without predicting the conduction angle.
    //
    // PHASE. The sines are referenced to virtual time zero (ISineInputADC.SetADCSine, phase 0),
    // the same origin MainsZeroCrossSource uses for its edges.
    //
    // `Conducting` has a coarser meaning - on since the last drive rose, held
    // `HoldMilliseconds` past the last drop - so a phase-cut load steady at any level reads as
    // one activation and "on"; `GateOn` is the instantaneous state the ADC channels follow.
    //
    // What it does not model: reactive loads (current is in phase, power factor 1), the load's
    // own dynamics (an incandescent lamp's inrush), and a TRIAC's latching (a real one conducts
    // to the current zero after its gate drops; here conduction ends with the drive).
    //
    // Repl:
    //     load: Miscellaneous.PhaseCutLoad @ sysbus 0x9F001000
    //         adc: iadc0
    //         channel: 19                 // voltage sense channel
    //         amplitudeMicrovolts: 273500 // peak swing on that channel while conducting
    //         init:
    //             AddCurrentSense 18 29000    // channel, microvolts per ampere
    //             AddCurrentSense 53 363000
    //     gpioPort:
    //         17 -> load@0                // one line per drive
    // Monitor: sysbus.load Conducting ; sysbus.load GateOn ; sysbus.load Activations
    //          sysbus.load LoadWatts 60   ; sysbus.load DeliveredWatts ; sysbus.load ConductionDegrees
    //          sysbus.load SetCurrentSenseSensitivity 18 44000
    public class PhaseCutLoad : IDoubleWordPeripheral, IKnownSize, IGPIOReceiver
    {
        public PhaseCutLoad(IMachine machine, ISineInputADC adc, int channel, uint amplitudeMicrovolts,
                            uint frequencyMilliHertz = 60000, uint holdMilliseconds = 25)
        {
            this.machine = machine;
            this.adc = adc;
            this.channel = channel;
            currentSense = new Dictionary<int, uint>();
            LineVoltsRms = DefaultLineVoltsRms;
            AmplitudeMicrovolts = amplitudeMicrovolts;
            FrequencyMilliHertz = frequencyMilliHertz == 0 ? 60000u : frequencyMilliHertz;
            HoldMilliseconds = holdMilliseconds;
            inputs = new Dictionary<int, bool>();
            // One-shot: (re)armed on every falling input edge, fires HoldMilliseconds later.
            holdTimer = new LimitTimer(machine.ClockSource, TickFrequency, this, "hold", 1, Direction.Ascending,
                                       enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            holdTimer.LimitReached += OnHoldExpired;
            // One-shot, re-armed from absolute virtual time at every half-cycle boundary of the
            // sine (k * T/2), so the meter's cycle windows are the line's and never drift.
            halfCycleTimer = new LimitTimer(machine.ClockSource, TickFrequency, this, "halfcycle", 1, Direction.Ascending,
                                            enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            halfCycleTimer.LimitReached += OnHalfCycle;
            registers = new DoubleWordRegisterCollection(this, new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.Status, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => Conducting, name: "CONDUCTING")
                    .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => GateOn, name: "GATE_ON")
                    .WithReservedBits(2, 30)},
                {(long)Registers.Amplitude, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, v) => AmplitudeMicrovolts = (uint)v,
                                    valueProviderCallback: _ => AmplitudeMicrovolts, name: "AMPLITUDE_UV")},
                {(long)Registers.LoadWatts, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, v) => LoadWatts = (uint)v,
                                    valueProviderCallback: _ => LoadWatts, name: "LOAD_WATTS")},
                {(long)Registers.DeliveredMilliwatts, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read,
                                    valueProviderCallback: _ => (uint)Math.Round(DeliveredWatts * 1000.0), name: "DELIVERED_MW")},
            });
            ArmHalfCycle(NowSeconds);
        }

        public void Reset()
        {
            // The load does not reboot with the MCU, but every drive does drop at reset: the
            // pins go back to inputs, so the load stops conducting.
            inputs.Clear();
            holdTimer.Enabled = false;
            SetGate(false);
            Conducting = false;
            Activations = 0;
        }

        public void OnGPIO(int number, bool value)
        {
            inputs[number] = value;
            SetGate(AnyInputHigh());
            if(value)
            {
                holdTimer.Enabled = false;
                if(!Conducting)
                {
                    Activations++;
                    Conducting = true;
                    this.Log(LogLevel.Debug, "Load conducting: channel {0} swings {1} uV at {2} mHz, {3} mA peak",
                             channel, amplitudeMicrovolts, FrequencyMilliHertz, PeakCurrentMilliamps);
                }
                return;
            }
            if(Conducting && !GateOn)
            {
                // Hold through the gap between half cycles of a phase-cut drive.
                holdTimer.Value = 0;
                holdTimer.Limit = HoldMilliseconds == 0 ? 1UL : (ulong)HoldMilliseconds * (ulong)(TickFrequency / 1000);
                holdTimer.Enabled = true;
            }
        }

        public uint ReadDoubleWord(long offset) => registers.Read(offset);
        public void WriteDoubleWord(long offset, uint value) => registers.Write(offset, value);

        public long Size => 0x1000;

        // True from the first drive rising until HoldMilliseconds after the last drive dropped:
        // "the load is on", as a test reads it, steady across a phase-cut drive's half cycles.
        public bool Conducting { get; private set; }

        // The instantaneous conduction state: true exactly while a drive input is high, which is
        // when the ADC channels carry the line. This is the phase-cut window.
        public bool GateOn { get; private set; }

        // Off-to-on transitions of Conducting since the last reset.
        public ulong Activations { get; private set; }

        // Peak of the sine placed on the channel while conducting, in microvolts at the ADC
        // input. Takes effect at the next gate edge; while the gate is on, immediately.
        public uint AmplitudeMicrovolts
        {
            get => amplitudeMicrovolts;
            set
            {
                amplitudeMicrovolts = value;
                if(GateOn)
                {
                    adc.SetADCSine(channel, amplitudeMicrovolts, FrequencyMilliHertz);
                }
            }
        }

        // The load's rating at LineVoltsRms, in watts. 0 draws no current (the current channels
        // stay at their bias). Takes effect at the next gate edge; while the gate is on, immediately.
        public uint LoadWatts
        {
            get => loadWatts;
            set
            {
                loadWatts = value;
                if(GateOn)
                {
                    DriveCurrent();
                }
            }
        }

        // The RMS line voltage the rating is quoted at; the current follows I = P / V.
        public uint LineVoltsRms { get; set; }

        // Adds an ADC channel that senses the load current, with its sensitivity at the ADC input
        // in microvolts per ampere of load current (the sensor's output times any gain after it).
        // Any number of channels may be added; a sensitivity of 0 leaves the channel alone.
        public void AddCurrentSense(int channel, uint microvoltsPerAmp)
        {
            currentSense[channel] = microvoltsPerAmp;
            if(GateOn)
            {
                DriveCurrent();
            }
        }

        // Changes the sensitivity of a channel added with AddCurrentSense.
        public void SetCurrentSenseSensitivity(int channel, uint microvoltsPerAmp)
        {
            if(!currentSense.ContainsKey(channel))
            {
                this.Log(LogLevel.Warning, "Channel {0} is not a current-sense channel; use AddCurrentSense first", channel);
                return;
            }
            AddCurrentSense(channel, microvoltsPerAmp);
        }

        public uint GetCurrentSenseSensitivity(int channel)
        {
            return currentSense.TryGetValue(channel, out var microvoltsPerAmp) ? microvoltsPerAmp : 0;
        }

        // Peak load current while conducting, in milliamps: the value the sense channels carry.
        public uint PeakCurrentMilliamps => LoadWatts == 0 || LineVoltsRms == 0
            ? 0 : (uint)Math.Round(LoadWatts * 1000.0 / LineVoltsRms * Math.Sqrt(2.0));

        // Line frequency, in millihertz. The sines are phase 0 at virtual time zero, like every
        // other ADC sine and the zero-cross source's edges. Set it before the line is enabled.
        public uint FrequencyMilliHertz { get; set; }

        // How long after the last drive drops `Conducting` stays true. Must cover one half cycle
        // of the slowest line (10 ms at 50 Hz) with margin.
        public uint HoldMilliseconds { get; set; }

        // --- the meter ------------------------------------------------------------------------

        // The average power a resistive load of LoadWatts actually received over the last
        // completed line cycle, given the conduction window applied to it: LoadWatts times the
        // fraction of the cycle's sin^2 that fell inside the window. LoadWatts at full
        // conduction, 0 with the gate off, and in between exactly what the phase cut leaves.
        public double DeliveredWatts => LoadWatts * DeliveredFraction;

        // That fraction alone: integral of sin^2 over the window, over the last full cycle,
        // divided by the whole cycle's integral (T/2).
        public double DeliveredFraction
        {
            get
            {
                var whole = HalfPeriodSeconds;   // integral of sin^2 over one full cycle = T/2
                return whole <= 0 ? 0 : (completedIntegral[0] + completedIntegral[1]) / whole;
            }
        }

        // The conduction window's width, averaged over the two half cycles of the last completed
        // line cycle, in degrees of the half cycle: 180 is full conduction, 0 is off.
        public double ConductionDegrees
        {
            get
            {
                var half = HalfPeriodSeconds;
                return half <= 0 ? 0 : (completedOnSeconds[0] + completedOnSeconds[1]) / (2.0 * half) * 180.0;
            }
        }

        // Half cycles the meter has completed since construction (two per line cycle).
        public ulong MeteredHalfCycles { get; private set; }

        private double HalfPeriodSeconds => FrequencyMilliHertz == 0 ? 0 : 500.0 / FrequencyMilliHertz;

        private double NowSeconds => machine.LocalTimeSource.ElapsedVirtualTime.TotalMicroseconds / 1e6;

        // Integral of sin^2(2 pi f t) dt from 0 to t: t/2 - sin(4 pi f t) / (8 pi f).
        private double SineSquaredIntegral(double seconds)
        {
            var omega = 2.0 * Math.PI * (FrequencyMilliHertz / 1000.0);
            return seconds / 2.0 - Math.Sin(2.0 * omega * seconds) / (4.0 * omega);
        }

        // Book the gate-on time between the last mark and now into the current half cycle.
        private void Accumulate(double now)
        {
            if(GateOn && now > mark)
            {
                halfIntegral += SineSquaredIntegral(now) - SineSquaredIntegral(mark);
                halfOnSeconds += now - mark;
            }
            mark = now;
        }

        private void OnHalfCycle()
        {
            // This is boundary `armedBoundary`'s event. Book up to the boundary itself, not to
            // `now`: the 1 MHz one-shot lands within a microsecond either side of it, and booking
            // to the exact boundary keeps every half cycle's window exactly one half period.
            var half = HalfPeriodSeconds;
            var boundary = armedBoundary * half;
            Accumulate(boundary);
            completedIntegral[1] = completedIntegral[0];
            completedOnSeconds[1] = completedOnSeconds[0];
            completedIntegral[0] = halfIntegral;
            completedOnSeconds[0] = halfOnSeconds;
            halfIntegral = 0;
            halfOnSeconds = 0;
            MeteredHalfCycles++;
            ArmBoundary(armedBoundary + 1, NowSeconds);
        }

        // First arm: the next boundary after now. Later arms come from OnHalfCycle with the
        // boundary index, never from `now`, because an event a tick early would otherwise floor
        // to the boundary it is standing on and fire again a microsecond later - an empty half
        // cycle filed between every real one, and a meter reading exactly half.
        private void ArmHalfCycle(double now)
        {
            var half = HalfPeriodSeconds;
            if(half <= 0)
            {
                return;
            }
            ArmBoundary(Math.Floor(now / half) + 1, now);
        }

        private void ArmBoundary(double boundary, double now)
        {
            armedBoundary = boundary;
            var ticks = (long)Math.Round((boundary * HalfPeriodSeconds - now) * TickFrequency);
            if(ticks < 1)
            {
                ticks = 1;
            }
            halfCycleTimer.Enabled = false;
            halfCycleTimer.Value = 0;
            halfCycleTimer.Limit = (ulong)ticks;
            halfCycleTimer.Enabled = true;
        }

        // --- the waveforms --------------------------------------------------------------------

        private bool AnyInputHigh()
        {
            foreach(var level in inputs.Values)
            {
                if(level)
                {
                    return true;
                }
            }
            return false;
        }

        private void OnHoldExpired()
        {
            if(!AnyInputHigh())
            {
                Conducting = false;
                this.Log(LogLevel.Debug, "Load off: channel {0} at its bias", channel);
            }
        }

        // The instantaneous state: the sines are on the channels exactly while the gate is.
        private void SetGate(bool value)
        {
            if(GateOn == value)
            {
                return;
            }
            Accumulate(NowSeconds);
            GateOn = value;
            if(value)
            {
                adc.SetADCSine(channel, amplitudeMicrovolts, FrequencyMilliHertz);
                DriveCurrent();
            }
            else
            {
                adc.ClearADCSine(channel);
                ClearCurrent();
            }
        }

        // A resistive load: current in phase with the voltage on every sense channel, each at
        // its own sensitivity. The swing is capped at the channel's bias so it never asks the
        // ADC for a level below ground; a real sense amplifier saturates there too.
        private void DriveCurrent()
        {
            var peakMilliamps = PeakCurrentMilliamps;
            foreach(var sense in currentSense)
            {
                DriveCurrentChannel(sense.Key, sense.Value, peakMilliamps);
            }
        }

        private void DriveCurrentChannel(int currentChannel, uint microvoltsPerAmp, uint peakMilliamps)
        {
            if(microvoltsPerAmp == 0)
            {
                return;
            }
            if(peakMilliamps == 0)
            {
                adc.ClearADCSine(currentChannel);
                return;
            }
            var swing = (ulong)peakMilliamps * microvoltsPerAmp / 1000;
            var bias = adc.GetADCBias(currentChannel);
            if(bias == 0)
            {
                // Nothing has biased this channel; a swing about 0 uV would be half clipped. Say
                // so rather than silently feeding the firmware a rectified current.
                this.Log(LogLevel.Warning, "Current channel {0} has no bias set; the load current on it will be clipped at ground",
                         currentChannel);
            }
            else if(swing > bias)
            {
                swing = bias;
            }
            adc.SetADCSine(currentChannel, (uint)swing, FrequencyMilliHertz);
        }

        private void ClearCurrent()
        {
            foreach(var senseChannel in currentSense.Keys)
            {
                adc.ClearADCSine(senseChannel);
            }
        }

        private readonly IMachine machine;
        private readonly ISineInputADC adc;
        private readonly int channel;
        private readonly Dictionary<int, uint> currentSense;
        private uint loadWatts;
        private readonly Dictionary<int, bool> inputs;
        private readonly LimitTimer holdTimer;
        private readonly LimitTimer halfCycleTimer;
        private readonly DoubleWordRegisterCollection registers;
        private uint amplitudeMicrovolts;

        // The meter: the current half cycle's booked conduction, and the last two completed.
        private double armedBoundary;   // index k of the half-cycle boundary (k * T/2) the timer is armed for
        private double mark;
        private double halfIntegral;
        private double halfOnSeconds;
        private readonly double[] completedIntegral = new double[2];
        private readonly double[] completedOnSeconds = new double[2];

        private const long TickFrequency = 1_000_000;
        private const uint DefaultLineVoltsRms = 120;

        private enum Registers : long
        {
            Status = 0x0,
            Amplitude = 0x4,
            LoadWatts = 0x8,
            DeliveredMilliwatts = 0xC,
        }
    }
}
