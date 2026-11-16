//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // The output of a mains zero-cross detector: a logic-level square wave at line frequency, high
    // during the positive half cycle. Connect it to a GPIO pin, a timer capture input, or both.
    //
    // PHASE. The edges are anchored to virtual time zero: the wave is high on [k*T/2, (k+1)*T/2)
    // for even k, which is exactly where sin(2*pi*f*t) is positive. A sine that an ADC model
    // generates with phase 0 from the same origin (ISineInputADC.SetADCSine) is therefore in phase
    // with this square wave by construction, whenever `Enabled` was set and however often the
    // frequency changed.
    //
    // `DelayMicroseconds` is the detector's lag: a real zero-cross circuit reports a crossing some
    // time after the line actually crossed. 0 (the default) is an ideal detector.
    //
    // Each edge is scheduled from absolute virtual time, not by a free-running period, so there
    // is no cumulative drift against the sines (a 10 MHz tick rounds a 60 Hz half cycle by
    // 33 ns; free-running, that is 9 degrees after 100 s).
    //
    // Repl:
    //     zx: Miscellaneous.MainsZeroCrossSource @ sysbus 0x9F000000
    //         frequencyMilliHertz: 60000
    //         0 -> gpioPort@52 | timer1@0
    // Monitor: sysbus.zx FrequencyMilliHertz 60000 ; sysbus.zx DelayMicroseconds 1000 ;
    //          sysbus.zx Enabled true ; sysbus.zx Level ; sysbus.zx Edges
    public class MainsZeroCrossSource : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public MainsZeroCrossSource(IMachine machine, uint frequencyMilliHertz = 60000, bool enabled = false)
        {
            this.machine = machine;
            output = new GPIO();
            Connections = new ReadOnlyDictionary<int, IGPIO>(new Dictionary<int, IGPIO> { { 0, output } });
            // 10 MHz tick: a 60 Hz half cycle is 83333.3 ticks, so an edge lands within 50 ns of
            // where it belongs - finer than a typical MCU timer capturing the edge resolves.
            // One-shot, re-armed at every edge from absolute time.
            timer = new LimitTimer(machine.ClockSource, TickFrequency, this, "zx", 1, Direction.Ascending,
                                   enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            timer.LimitReached += OnHalfCycle;
            registers = new DoubleWordRegisterCollection(this, new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.Control, new DoubleWordRegister(this)
                    .WithFlag(0, writeCallback: (_, v) => Enabled = v, valueProviderCallback: _ => Enabled, name: "EN")
                    .WithReservedBits(1, 31)},
                {(long)Registers.Frequency, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, v) => FrequencyMilliHertz = (uint)v,
                                    valueProviderCallback: _ => FrequencyMilliHertz, name: "FREQ_MHZ")},
                {(long)Registers.Delay, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, v) => DelayMicroseconds = (uint)v,
                                    valueProviderCallback: _ => DelayMicroseconds, name: "DELAY_US")},
            });
            this.frequencyMilliHertz = frequencyMilliHertz == 0 ? 60000u : frequencyMilliHertz;
            Enabled = enabled;
        }

        public void Reset()
        {
            // The line does not reboot with the MCU: keep frequency, enable and phase. Only the
            // counters restart so a test can count edges since the last reset.
            Edges = 0;
        }

        public uint ReadDoubleWord(long offset) => registers.Read(offset);
        public void WriteDoubleWord(long offset, uint value) => registers.Write(offset, value);

        public long Size => 0x1000;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // Line frequency in millihertz (60000 = 60.000 Hz). The edges re-anchor to the new
        // period at once.
        public uint FrequencyMilliHertz
        {
            get => frequencyMilliHertz;
            set
            {
                if(value == 0)
                {
                    this.Log(LogLevel.Warning, "Frequency of 0 ignored; use Enabled false to stop the line");
                    return;
                }
                frequencyMilliHertz = value;
                if(enabled)
                {
                    Align();
                }
            }
        }

        // The detector's lag behind the true zero crossing, in microseconds; the edges move by
        // this much after the sine's crossings. Takes effect at once.
        public uint DelayMicroseconds
        {
            get => delayMicroseconds;
            set
            {
                delayMicroseconds = value;
                if(enabled)
                {
                    Align();
                }
            }
        }

        public bool Enabled
        {
            get => enabled;
            set
            {
                if(value == enabled)
                {
                    return;
                }
                enabled = value;
                if(value)
                {
                    Align();
                }
                else
                {
                    timer.Enabled = false;
                }
            }
        }

        // Current level of the line: true during the positive half cycle.
        public bool Level => level;

        // Edges emitted since the last reset (two per cycle).
        public ulong Edges { get; private set; }

        // Half period in microseconds: 1e6 / (2 f) with f in Hz = f_mHz / 1000.
        private double HalfPeriodMicroseconds => 5e8 / frequencyMilliHertz;

        private double NowMicroseconds => machine.LocalTimeSource.ElapsedVirtualTime.TotalMicroseconds;

        // Put the output at the level the line has NOW (relative to virtual time zero and the
        // delay) and schedule the next edge at the next half-period boundary.
        private void Align()
        {
            var half = HalfPeriodMicroseconds;
            var now = NowMicroseconds;
            var k = Math.Floor((now - delayMicroseconds) / half);
            SetLevel(IsPositiveHalf(k));
            ArmEdge(now, (k + 1) * half + delayMicroseconds);
        }

        private void OnHalfCycle()
        {
            var half = HalfPeriodMicroseconds;
            var now = NowMicroseconds;
            // We are on (within a tick of) boundary k; the level for the half cycle that starts
            // here comes from k's parity, so a tick of rounding can never invert the wave.
            var k = Math.Round((now - delayMicroseconds) / half);
            var next = SetLevel(IsPositiveHalf(k));
            if(next)
            {
                Edges++;
            }
            ArmEdge(now, (k + 1) * half + delayMicroseconds);
        }

        private static bool IsPositiveHalf(double k)
        {
            // Even k is the positive half; k may be negative while now < delay.
            return ((long)k % 2 + 2) % 2 == 0;
        }

        // Returns true when the level changed.
        private bool SetLevel(bool value)
        {
            var changed = value != level;
            level = value;
            output.Set(level);
            return changed;
        }

        private void ArmEdge(double nowMicroseconds, double edgeMicroseconds)
        {
            var ticks = (long)Math.Round((edgeMicroseconds - nowMicroseconds) * TicksPerMicrosecond);
            if(ticks < 1)
            {
                // Rounding put the boundary behind us: take the one after.
                ticks += (long)Math.Round(HalfPeriodMicroseconds * TicksPerMicrosecond);
                if(ticks < 1)
                {
                    ticks = 1;
                }
            }
            timer.Enabled = false;
            timer.Value = 0;
            timer.Limit = (ulong)ticks;
            timer.Enabled = true;
        }

        private readonly IMachine machine;
        private readonly GPIO output;
        private readonly LimitTimer timer;
        private readonly DoubleWordRegisterCollection registers;
        private uint frequencyMilliHertz;
        private uint delayMicroseconds;
        private bool enabled;
        private bool level = true;

        private const long TickFrequency = 10_000_000;
        private const double TicksPerMicrosecond = TickFrequency / 1e6;

        private enum Registers : long
        {
            Control = 0x0,
            Frequency = 0x4,
            Delay = 0x8,
        }
    }
}
