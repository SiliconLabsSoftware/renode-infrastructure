//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Sensor;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities.RESD;

namespace Antmicro.Renode.Peripherals.Analog
{
    // Silicon Labs EFR32 series-2 IADC (incremental ADC), IP version 3 - the revision fitted to
    // EFR32MG24 (_IADC_IPVERSION_RESETVALUE == 3 in efr32mg24_iadc.h).
    //
    // Register offsets, bit positions, masks and reset values are all taken from
    //   simplicity_sdk/2024.12/platform/Device/SiliconLabs/EFR32MG24/Include/efr32mg24_iadc.h
    // and the driver behavior this has to satisfy from
    //   simplicity_sdk/2024.12/platform/emlib/src/em_iadc.c
    //
    // WHAT IS MODELED
    //   * the single (software-triggered) conversion queue, its 8-entry FIFO, and SINGLEDONE
    //   * the scan queue as a single pass over the entries enabled in MASKREQ
    //   * counts computed from a real input voltage through the configured reference and
    //     analog gain, so the number the firmware reads is a function of a voltage a test set
    //   * IF/IEN -> IRQ, and the SET/CLR/TGL register aliases
    //   * the FIFO data-valid levels as DMA requests (ScanFifoRequest, SingleFifoRequest)
    //
    // WHAT IS NOT, AND WHY IT MATTERS
    //   * Conversions complete instantly. STATUS.CONVERTING therefore never reads 1 and
    //     conversion *rate* is not modeled: nothing here can catch a firmware that starts
    //     conversions faster than the ADC can retire them, or that mis-programs CLK_ADC.
    //     Every emlib busy-wait loop waits for a bit to *clear* (em_iadc.c:134,140,802,814,823),
    //     so instant completion satisfies all of them - it cannot hang the firmware.
    //   * Continuous trigger action (TRIGGER.*TRIGACTION = CONTINUOUS) re-runs the scan table
    //     every ContinuousScanPeriodMicroseconds (a knob, since conversion time is not
    //     modeled). The IADC's own TIMER trigger (SCANTRIGSEL = TIMER,
    //     CMD.TIMEREN, the TIMER register) IS modeled: the timer counts CLK_SRC_ADC cycles,
    //     `sourceClockHertz` (a platform fact - the clock tree's EM01GRPACLK, e.g. 39 MHz with
    //     a 39 MHz HFXO), and every TIMER-count period runs one scan pass
    //     while a scan is armed, which is how firmware paces periodic sampling
    //     (1560 cycles of 39 MHz is a 40 us period). PRS triggers are not
    //     modeled and log a warning.
    //   * PORTALLOCERR is never raised. The GPIO ABUSALLOC allocation that a real IADC needs
    //     before it can reach a pin is not checked, so firmware that forgets it still reads
    //     good data here and fails on hardware.
    //   * The digital window comparator (CMPTHR / *.CMP) stores its configuration and never
    //     fires.
    //   * Differential inputs: a non-GND negative input is warned about and treated as GND.
    //
    // FEEDING INPUT VOLTAGES. Implements Sensor.IADC: every channel has a voltage sample source
    // registered in ADCContainer, and a test or a .resc drives the analog side with
    // `SetADCValue <channel> <microvolts>` (or the interface's `SetVoltage <microvolts> <channel>`,
    // which is the same thing). Channel numbering follows the same port*16 + pin scheme Renode's
    // SiLabs GPIO model uses, over the four ports the IADC can reach: A=0, B=1, C=2, D=3. So PD0
    // is 3*16 + 0 = 48:
    //
    //     sysbus.iadc0 SetADCValue 48 1100000     # 1.100 V on PD0
    //
    // Values are microvolts, the RESD VoltageSample unit. Unset channels read 0 V.
    //
    // The default source of a channel is a SiLabs_IADC_3_InputSource, named <adc>-P<port><pin>
    // (iadc0-PD0), which keeps its level across a machine reset: the level describes the board
    // the machine is plugged into, not the state of the peripheral. A .repl may register any
    // other voltage source on a channel instead (ADCChannelSource, Potentiometer, ...), and that
    // source then follows its own reset rules.
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class SiLabs_IADC_3 : BasicDoubleWordPeripheral, IKnownSize, IADC
    {
        // supplyMillivolts backs REFSEL=VDDX; externalReferenceMillivolts backs REFSEL=VREF.
        // Neither is derivable from any register - the reference selection is on-chip, the
        // voltage behind it is board wiring - so they are platform facts and belong in the
        // .repl next to the rest of the board description.
        public SiLabs_IADC_3(IMachine machine, uint supplyMillivolts = 3300,
                               uint externalReferenceMillivolts = 1250,
                               uint continuousScanPeriodMicroseconds = 20,
                               uint sourceClockHertz = 39000000) : base(machine)
        {
            ContinuousScanPeriodMicroseconds = continuousScanPeriodMicroseconds;
            SourceClockHertz = sourceClockHertz;
            scanTimer = new LimitTimer(machine.ClockSource, 1000000, this, "scan", 20, Direction.Ascending,
                                       enabled: false, workMode: WorkMode.Periodic, eventEnabled: true, autoUpdate: true);
            scanTimer.LimitReached += OnScanTimer;
            this.supplyMillivolts = supplyMillivolts;
            this.externalReferenceMillivolts = externalReferenceMillivolts;

            ADCContainer = new SimpleContainerHelper<IRESDSampleSource<VoltageSample>>(machine, this);
            sineAmplitudeMicrovolts = new uint[ChannelCount];
            sineFrequencyMilliHertz = new uint[ChannelCount];
            sinePhaseDegrees = new int[ChannelCount];
            singleFifo = new Queue<uint>();
            scanFifo = new Queue<uint>();

            IRQ = new GPIO();
            ScanFifoRequest = new GPIO();
            SingleFifoRequest = new GPIO();

            DefineRegisters();
            Reset();

            // The default sources are named after this peripheral, so they can only be created
            // once it has its own name; the NameChanged event is the first point where it does.
            machine.PeripheralsChanged += (_, args) =>
            {
                if(args.Peripheral == this && args.Operation == PeripheralsChangedEventArgs.PeripheralChangeType.NameChanged)
                {
                    RegisterDefaultSources();
                }
            };
        }

        public override void Reset()
        {
            base.Reset();
            singleFifo.Clear();
            scanFifo.Clear();
            StopContinuousScan();
            lastSingleData = 0;
            lastScanData = 0;
            timerRunning = false;
            scanArmedForTimer = false;
            // The line is only ever driven from UpdateInterrupts(), which base.Reset() does not
            // call, so an asserted IRQ would otherwise outlive the reset and the NVIC would keep
            // seeing the level.
            IRQ.Unset();
            ScanFifoRequest.Unset();
            SingleFifoRequest.Unset();
            // Deliberately does NOT clear the sine inputs, and the default channel sources keep
            // their level too: they describe the board the machine is plugged into, not the state
            // of the peripheral, and a reset must not silently move the analog world back to 0 V
            // under a running test.
        }

        // The IADC exposes each register three more times as a bit-set, bit-clear and
        // bit-toggle alias 0x1000/0x2000/0x3000 above the base offset. emlib uses them
        // (IADC_clearInt writes IF_CLR, IADC_disable writes EN_CLR), so they are not optional.
        public override uint ReadDoubleWord(long offset)
        {
            if((offset & AliasMask) != 0)
            {
                this.Log(LogLevel.Warning, "Read from the Set/Clr/Tgl alias at offset 0x{0:X}; returning the base register", offset);
            }
            return RegistersCollection.Read(offset & RegisterOffsetMask);
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            var register = offset & RegisterOffsetMask;
            var alias = offset & AliasMask;

            if(alias != 0 && ReadModifiesState.Contains(register))
            {
                // A read-modify-write alias would have to *read* the register first, and reading
                // these pops a FIFO entry. They are read-only on hardware so no driver does this;
                // refuse rather than quietly destroy a sample.
                this.Log(LogLevel.Error, "Set/Clr/Tgl write to offset 0x{0:X}, whose read pops a FIFO entry - ignoring", offset);
                return;
            }

            switch(alias)
            {
                case 0:
                    RegistersCollection.Write(register, value);
                    break;
                case AliasSet:
                    RegistersCollection.Write(register, RegistersCollection.Read(register) | value);
                    break;
                case AliasClear:
                    RegistersCollection.Write(register, RegistersCollection.Read(register) & ~value);
                    break;
                case AliasToggle:
                    RegistersCollection.Write(register, RegistersCollection.Read(register) ^ value);
                    break;
                default:
                    this.Log(LogLevel.Error, "Write to non existing offset 0x{0:X}", offset);
                    break;
            }
        }

        // --- Sensor.IADC -------------------------------------------------------------------
        // `value` is microvolts (a RESD VoltageSample), and `channel` is port*16 + pin with
        // A=0, B=1, C=2, D=3 - see the class comment. Sets the DC level of the source registered
        // on the channel.
        public void SetADCValue(int channel, uint value)
        {
            if(!TryValidateChannel(channel))
            {
                return;
            }
            EnsureSource(channel);
            this.SetVoltage(value, channel);
            this.Log(LogLevel.Debug, "Channel {0} ({1}) set to {2} uV", channel, ChannelName(channel), value);
        }

        public uint GetADCValue(int channel)
        {
            return TryValidateChannel(channel) ? ChannelMicrovolts(channel) : 0;
        }

        // The DC level alone - what SetADCValue set - without the sine GetADCValue adds at the
        // current instant. A load model that caps a current swing at the chain's bias needs the
        // bias, not a sample of the waveform already riding on it.
        public uint GetADCBias(int channel)
        {
            return TryValidateChannel(channel) ? SourceMicrovolts(channel) : 0;
        }

        public string GetDefaultChannelName(int channel)
        {
            return ChannelName(channel);
        }

        // ------------------------------------------------------------------------------------
        // AC inputs. `SetADCValue` alone is a DC level, and a DC level is invisible to any
        // firmware that measures an AC waveform: such firmware typically removes the DC bias
        // from its sample buffer before taking the RMS, so a constant input is subtracted to
        // exactly nothing and the firmware reports 0 V forever. The input has to
        // actually swing.
        //
        // So a channel may carry a sine on top of its DC level, evaluated at the instant of each
        // conversion from the machine's virtual time:
        //
        //     level(t) = SetADCValue(channel) + amplitude * sin(2*pi*f*t + phase)
        //
        // clamped at 0 (a single-ended ADC cannot see below its ground). That is how the real
        // divider works too: it is biased to mid-rail so the negative half cycle is
        // representable, and the firmware measures and subtracts that bias.
        //
        //     sysbus.iadc0 SetADCValue 51 1650000            # PD3 line voltage, mid-rail bias
        //     sysbus.iadc0 SetADCSine  51 547000 60000       # 0.547 V peak at 60.000 Hz
        //     sysbus.iadc0 ClearADCSine 51
        //
        // The amplitude that corresponds to a given mains voltage is a property of the board's
        // divider and the firmware's gain constant, not of this model - see the platform file.
        public void SetADCSine(int channel, uint amplitudeMicrovolts, uint frequencyMilliHertz, int phaseDegrees = 0)
        {
            if(!TryValidateChannel(channel))
            {
                return;
            }
            if(frequencyMilliHertz == 0)
            {
                this.Log(LogLevel.Warning, "A frequency of 0 would be a DC level; use ClearADCSine to stop the swing");
                return;
            }
            sineAmplitudeMicrovolts[channel] = amplitudeMicrovolts;
            sineFrequencyMilliHertz[channel] = frequencyMilliHertz;
            sinePhaseDegrees[channel] = phaseDegrees;
            this.Log(LogLevel.Debug, "Channel {0} ({1}) swings {2} uV peak at {3} mHz, phase {4} deg",
                     channel, ChannelName(channel), amplitudeMicrovolts, frequencyMilliHertz, phaseDegrees);
        }

        public void ClearADCSine(int channel)
        {
            if(!TryValidateChannel(channel))
            {
                return;
            }
            sineAmplitudeMicrovolts[channel] = 0;
            sineFrequencyMilliHertz[channel] = 0;
            sinePhaseDegrees[channel] = 0;
        }

        // What the channel reads right now, DC plus any swing. `GetADCValue` reports this, so a
        // Monitor read of an AC channel shows the instantaneous level, not the bias.
        private uint ChannelMicrovolts(int channel)
        {
            var dc = SourceMicrovolts(channel);
            var amplitude = sineAmplitudeMicrovolts[channel];
            if(amplitude == 0 || sineFrequencyMilliHertz[channel] == 0)
            {
                return dc;
            }
            var seconds = machine.LocalTimeSource.ElapsedVirtualTime.TotalMicroseconds / 1e6;
            var frequency = sineFrequencyMilliHertz[channel] / 1000.0;
            var radians = 2.0 * Math.PI * frequency * seconds + sinePhaseDegrees[channel] * Math.PI / 180.0;
            var level = dc + amplitude * Math.Sin(radians);
            if(level <= 0)
            {
                // The pad cannot go below ground; a divider biased too low clips, and clipping is
                // a real measurement the firmware should be allowed to see rather than a wrap.
                return 0;
            }
            return (uint)Math.Round(level);
        }

        // The level of the source registered on the channel; 0 V when there is none.
        private uint SourceMicrovolts(int channel)
        {
            return ADCContainer.TryGetByAddress(channel, out var source) ? source.Sample.Voltage : 0;
        }

        // Gives the channel its default source if nothing is registered on it.
        private void EnsureSource(int channel)
        {
            if(ADCContainer.TryGetByAddress(channel, out var _))
            {
                return;
            }
            var source = new SiLabs_IADC_3_InputSource();
            ADCContainer.Register(source, new NumberRegistrationPoint<int>(channel));
            if(machine.TryGetLocalName(this, out var name))
            {
                machine.SetLocalName(source, $"{name}-{ChannelName(channel)}");
            }
        }

        private void RegisterDefaultSources()
        {
            for(var channel = 0; channel < ChannelCount; channel++)
            {
                EnsureSource(channel);
            }
        }

        public int ADCChannelCount => ChannelCount;

        public SimpleContainerHelper<IRESDSampleSource<VoltageSample>> ADCContainer { get; }

        public GPIO IRQ { get; }

        // The FIFOs' DMA requests, as the LDMAXBAR sees them: high while the FIFO holds at least
        // DVL+1 entries (the same level that raises the *FIFODVL interrupt), low once the LDMA has
        // drained it. Wire them to the LDMA's request inputs, SOURCESEL_IADC0 (0xA) << 4 | SIGSEL
        // (SCAN 0, SINGLE 1): `iadc0: ScanFifoRequest -> ldma@0x00A0`.
        public GPIO ScanFifoRequest { get; }
        public GPIO SingleFifoRequest { get; }

        public long Size => 0x4000;

        private void DefineRegisters()
        {
            Registers.Ipversion.Define(this, IpVersion)
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => IpVersion, name: "IPVERSION")
            ;

            Registers.En.Define(this)
                .WithFlag(0, out enabled, name: "EN",
                          changeCallback: (_, value) =>
                          {
                              if(!value)
                              {
                                  // A disabled IADC has no results to hand out. Draining here also
                                  // keeps IADC_reset()'s "disable last" ordering honest.
                                  singleFifo.Clear();
                                  scanFifo.Clear();
                              }
                          })
                // DISABLING reports an in-progress shutdown. Nothing here takes time, so it reads
                // 0 forever - which is exactly what em_iadc.c:140's `while(EN & DISABLING)` needs.
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => false, name: "DISABLING")
                .WithReservedBits(2, 30)
            ;

            Registers.Ctrl.Define(this)
                .WithFlag(0, name: "EM23WUCONVERT")
                .WithFlag(1, name: "ADCCLKSUSPEND0")
                .WithFlag(2, name: "ADCCLKSUSPEND1")
                .WithFlag(3, name: "DBGHALT")
                .WithValueField(4, 2, name: "WARMUPMODE")
                .WithReservedBits(6, 10)
                .WithValueField(16, 7, name: "TIMEBASE")
                .WithReservedBits(23, 5)
                .WithValueField(28, 3, name: "HSCLKRATE")
                .WithReservedBits(31, 1)
            ;

            Registers.Cmd.Define(this)
                .WithFlag(0, FieldMode.Write, name: "SINGLESTART",
                          writeCallback: (_, value) => { if(value) { StartSingleConversion(); } })
                .WithFlag(1, FieldMode.Write, name: "SINGLESTOP",
                          // Nothing is ever in flight, so there is nothing to abort. The FIFO
                          // deliberately survives: emlib stops the queues *before* draining.
                          writeCallback: (_, value) => { if(value) { this.Log(LogLevel.Debug, "SINGLESTOP with no conversion in flight"); } })
                .WithReservedBits(2, 1)
                .WithFlag(3, FieldMode.Write, name: "SCANSTART",
                          writeCallback: (_, value) => { if(value) { StartScanConversion(); } })
                .WithFlag(4, FieldMode.Write, name: "SCANSTOP",
                          writeCallback: (_, value) => { if(value) { StopContinuousScan(); } })
                .WithReservedBits(5, 11)
                .WithFlag(16, FieldMode.Write, name: "TIMEREN",
                          writeCallback: (_, value) =>
                          {
                              if(!value)
                              {
                                  return;
                              }
                              timerRunning = true;
                              if(scanArmedForTimer)
                              {
                                  StartTimerPacedScan();
                              }
                          })
                .WithFlag(17, FieldMode.Write, name: "TIMERDIS",
                          writeCallback: (_, value) =>
                          {
                              if(value)
                              {
                                  timerRunning = false;
                                  if(scanArmedForTimer)
                                  {
                                      scanTimer.Enabled = false;
                                      ContinuousScanRunning = false;
                                  }
                              }
                          })
                .WithReservedBits(18, 6)
                .WithFlag(24, FieldMode.Write, name: "SINGLEFIFOFLUSH",
                          writeCallback: (_, value) => { if(value) { singleFifo.Clear(); UpdateDmaRequests(); } })
                .WithFlag(25, FieldMode.Write, name: "SCANFIFOFLUSH",
                          writeCallback: (_, value) => { if(value) { scanFifo.Clear(); UpdateDmaRequests(); } })
                .WithReservedBits(26, 6)
            ;

            // Compare value for the IADC's own trigger timer, in CLK_SRC_ADC cycles.
            Registers.Timer.Define(this)
                .WithValueField(0, 16, out timerCompare, name: "TIMER")
                .WithReservedBits(16, 16)
            ;

            // Every bit here is read-only. The ones hardwired to false are states this model has
            // no way to be in (nothing is ever mid-conversion or mid-flush, and no write takes
            // time to propagate), and emlib only ever waits for them to be clear.
            Registers.Status.Define(this)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => false, name: "SINGLEQEN")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => false, name: "SINGLEQUEUEPENDING")
                .WithReservedBits(2, 1)
                .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => false, name: "SCANQEN")
                .WithFlag(4, FieldMode.Read, valueProviderCallback: _ => false, name: "SCANQUEUEPENDING")
                .WithReservedBits(5, 1)
                .WithFlag(6, FieldMode.Read, valueProviderCallback: _ => false, name: "CONVERTING")
                .WithReservedBits(7, 1)
                .WithFlag(8, FieldMode.Read, valueProviderCallback: _ => singleFifo.Count > 0, name: "SINGLEFIFODV")
                .WithFlag(9, FieldMode.Read, valueProviderCallback: _ => scanFifo.Count > 0, name: "SCANFIFODV")
                .WithReservedBits(10, 4)
                .WithFlag(14, FieldMode.Read, valueProviderCallback: _ => false, name: "SINGLEFIFOFLUSHING")
                .WithFlag(15, FieldMode.Read, valueProviderCallback: _ => false, name: "SCANFIFOFLUSHING")
                .WithFlag(16, FieldMode.Read, valueProviderCallback: _ => timerRunning, name: "TIMERACTIVE")
                .WithReservedBits(17, 3)
                .WithFlag(20, FieldMode.Read, valueProviderCallback: _ => false, name: "SINGLEWRITEPENDING")
                .WithFlag(21, FieldMode.Read, valueProviderCallback: _ => false, name: "MASKREQWRITEPENDING")
                .WithReservedBits(22, 2)
                .WithFlag(24, FieldMode.Read, valueProviderCallback: _ => false, name: "SYNCBUSY")
                .WithReservedBits(25, 5)
                // The analog front end is warm whenever the peripheral is on; warm-up takes no
                // time here, and WARMUPMODE only decides whether it stays warm between
                // conversions, which is invisible without conversion timing.
                .WithFlag(30, FieldMode.Read, valueProviderCallback: _ => enabled.Value, name: "ADCWARM")
                .WithReservedBits(31, 1)
            ;

            Registers.Maskreq.Define(this)
                .WithValueField(0, 16, out scanMask, name: "MASKREQ")
                .WithReservedBits(16, 16)
            ;

            // STMASK is the working copy of MASKREQ. Writes propagate instantly here, so the two
            // are always identical.
            Registers.Stmask.Define(this)
                .WithValueField(0, 16, FieldMode.Read, valueProviderCallback: _ => scanMask.Value, name: "STMASK")
                .WithReservedBits(16, 16)
            ;

            Registers.Cmpthr.Define(this)
                .WithValueField(0, 16, name: "ADLT")
                .WithValueField(16, 16, name: "ADGT")
            ;

            var interruptFlagRegister = Registers.If.Define(this);
            var interruptEnableRegister = Registers.Ien.Define(this);
            foreach(var interrupt in InterruptBits)
            {
                interruptFlagRegister.WithFlag(interrupt.Key, out interruptFlags[interrupt.Key], name: interrupt.Value);
                interruptEnableRegister.WithFlag(interrupt.Key, out interruptEnable[interrupt.Key], name: interrupt.Value);
            }
            interruptFlagRegister.WithWriteCallback((_, __) => UpdateInterrupts());
            interruptEnableRegister.WithWriteCallback((_, __) => UpdateInterrupts());

            Registers.Trigger.Define(this)
                .WithValueField(0, 3, out scanTriggerSelect, name: "SCANTRIGSEL")
                .WithReservedBits(3, 1)
                .WithFlag(4, out scanTriggerContinuous, name: "SCANTRIGACTION")
                .WithReservedBits(5, 3)
                .WithValueField(8, 3, out singleTriggerSelect, name: "SINGLETRIGSEL")
                .WithReservedBits(11, 1)
                .WithFlag(12, out singleTriggerContinuous, name: "SINGLETRIGACTION")
                .WithReservedBits(13, 3)
                .WithFlag(16, name: "SINGLETAILGATE")
                .WithReservedBits(17, 15)
            ;

            for(var index = 0; index < ConfigCount; index++)
            {
                var config = index;
                (index == 0 ? Registers.Cfg0 : Registers.Cfg1).Define(this, CfgResetValue)
                    .WithValueField(0, 2, out adcMode[config], name: "ADCMODE")
                    .WithValueField(2, 3, out osrHighSpeed[config], name: "OSRHS")
                    .WithValueField(5, 3, name: "OSRHA")
                    .WithReservedBits(8, 4)
                    .WithValueField(12, 3, out analogGain[config], name: "ANALOGGAIN")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 3, out referenceSelect[config], name: "REFSEL")
                    .WithReservedBits(19, 2)
                    .WithValueField(21, 3, name: "DIGAVG")
                    .WithReservedBits(24, 4)
                    .WithValueField(28, 2, name: "TWOSCOMPL")
                    .WithReservedBits(30, 2)
                ;

                // Offset/gain calibration trim. Stored so a read-back matches, and deliberately
                // not applied: modelling the calibration would mean inventing a gain error to
                // correct.
                (index == 0 ? Registers.Scale0 : Registers.Scale1).Define(this, ScaleResetValue)
                    .WithValueField(0, 18, name: "OFFSET")
                    .WithValueField(18, 13, name: "GAIN13LSB")
                    .WithFlag(31, name: "GAIN3MSB")
                ;

                (index == 0 ? Registers.Sched0 : Registers.Sched1).Define(this)
                    .WithValueField(0, 10, name: "PRESCALE")
                    .WithReservedBits(10, 22)
                ;
            }

            Registers.Singlefifocfg.Define(this, FifoCfgResetValue)
                .WithValueField(0, 3, out singleAlignment, name: "ALIGNMENT")
                .WithFlag(3, out singleShowId, name: "SHOWID")
                .WithValueField(4, 3, out singleDataValidLevel, name: "DVL")
                .WithReservedBits(7, 1)
                .WithFlag(8, name: "DMAWUFIFOSINGLE")
                .WithReservedBits(9, 23)
            ;

            Registers.Singlefifodata.Define(this)
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => PopFifo(singleFifo, ref lastSingleData, IrqSingleFifoUf, "single"),
                                name: "DATA")
            ;

            Registers.Singlefifostat.Define(this)
                .WithValueField(0, 4, FieldMode.Read, valueProviderCallback: _ => (ulong)singleFifo.Count, name: "FIFOREADCNT")
                .WithReservedBits(4, 28)
            ;

            // Peeks at the head of the FIFO without popping it - emlib documents
            // IADC_readSingleData as leaving the FIFO alone, unlike the popping read.
            Registers.Singledata.Define(this)
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => PeekFifo(singleFifo, lastSingleData), name: "DATA")
            ;

            Registers.Scanfifocfg.Define(this, FifoCfgResetValue)
                .WithValueField(0, 3, out scanAlignment, name: "ALIGNMENT")
                .WithFlag(3, out scanShowId, name: "SHOWID")
                .WithValueField(4, 3, out scanDataValidLevel, name: "DVL")
                .WithReservedBits(7, 1)
                .WithFlag(8, name: "DMAWUFIFOSCAN")
                .WithReservedBits(9, 23)
            ;

            Registers.Scanfifodata.Define(this)
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => PopFifo(scanFifo, ref lastScanData, IrqScanFifoUf, "scan"),
                                name: "DATA")
            ;

            Registers.Scanfifostat.Define(this)
                .WithValueField(0, 4, FieldMode.Read, valueProviderCallback: _ => (ulong)scanFifo.Count, name: "FIFOREADCNT")
                .WithReservedBits(4, 28)
            ;

            Registers.Scandata.Define(this)
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => PeekFifo(scanFifo, lastScanData), name: "DATA")
            ;

            Registers.Single.Define(this)
                .WithValueField(0, 4, out singlePinNegative, name: "PINNEG")
                .WithValueField(4, 4, out singlePortNegative, name: "PORTNEG")
                .WithValueField(8, 4, out singlePinPositive, name: "PINPOS")
                .WithValueField(12, 4, out singlePortPositive, name: "PORTPOS")
                .WithFlag(16, out singleConfigSelect, name: "CFG")
                .WithFlag(17, out singleCompare, name: "CMP")
                .WithReservedBits(18, 14)
            ;

            Registers.Scantable.DefineMany(this, ScanTableEntries, (register, index) =>
            {
                register
                    .WithValueField(0, 4, out scanPinNegative[index], name: "PINNEG")
                    .WithValueField(4, 4, out scanPortNegative[index], name: "PORTNEG")
                    .WithValueField(8, 4, out scanPinPositive[index], name: "PINPOS")
                    .WithValueField(12, 4, out scanPortPositive[index], name: "PORTPOS")
                    .WithFlag(16, out scanConfigSelect[index], name: "CFG")
                    .WithFlag(17, out scanCompare[index], name: "CMP")
                    .WithReservedBits(18, 14)
                ;
            });
        }

        private void StartSingleConversion()
        {
            if(!enabled.Value)
            {
                this.Log(LogLevel.Warning, "CMD.SINGLESTART while the IADC is disabled - ignoring");
                return;
            }

            if(singleTriggerSelect.Value != (ulong)TriggerSource.Immediate)
            {
                this.Log(LogLevel.Warning, "SINGLETRIGSEL is {0}, which is not modeled; converting immediately",
                         (TriggerSource)singleTriggerSelect.Value);
            }
            if(singleTriggerContinuous.Value)
            {
                this.Log(LogLevel.Warning, "SINGLETRIGACTION is CONTINUOUS; this model converts once per CMD.SINGLESTART");
            }

            var config = singleConfigSelect.Value ? 1 : 0;
            var counts = Convert(singlePortPositive.Value, singlePinPositive.Value,
                                 singlePortNegative.Value, singlePinNegative.Value, config, out var bits);
            WarnIfCompareEnabled(singleCompare.Value);

            PushFifo(singleFifo, Pack(counts, singleShowId.Value ? SingleQueueId : 0u, (Alignment)singleAlignment.Value, bits),
                     singleDataValidLevel.Value, IrqSingleFifoDvl, IrqSingleFifoOf, "single");
            SetInterrupt(IrqSingleDone);
            UpdateInterrupts();
        }

        private void StartScanConversion()
        {
            if(!enabled.Value)
            {
                this.Log(LogLevel.Warning, "CMD.SCANSTART while the IADC is disabled - ignoring");
                return;
            }

            if(scanTriggerSelect.Value == (ulong)TriggerSource.Timer)
            {
                // SCANSTART arms the queue; the TIMER's events run it, one pass per event, for
                // as long as the trigger action is CONTINUOUS (once, otherwise).
                scanArmedForTimer = true;
                if(timerRunning)
                {
                    StartTimerPacedScan();
                }
                return;
            }
            if(scanTriggerSelect.Value != (ulong)TriggerSource.Immediate)
            {
                this.Log(LogLevel.Warning, "SCANTRIGSEL is {0}, which is not modeled; converting immediately",
                         (TriggerSource)scanTriggerSelect.Value);
            }
            if(!RunScanPass())
            {
                return;
            }
            if(scanTriggerContinuous.Value)
            {
                StartContinuousScan();
            }
        }

        // The TIMER trigger: TIMER cycles of CLK_SRC_ADC between scan passes. The scan timer runs
        // at 1 MHz, so the period is rounded to whole microseconds (1560 cycles at 39 MHz is
        // exactly 40 us, the only value any firmware here programs).
        private void StartTimerPacedScan()
        {
            var period = (ulong)Math.Max(1L, (long)Math.Round(timerCompare.Value * 1e6 / SourceClockHertz));
            scanTimer.Limit = period;
            scanTimer.Value = 0;
            scanTimer.Enabled = true;
            ContinuousScanRunning = true;
            this.Log(LogLevel.Debug, "TIMER-triggered scan: {0} cycles of {1} Hz = {2} us per pass, {3}",
                     timerCompare.Value, SourceClockHertz, period, scanTriggerContinuous.Value ? "continuous" : "once");
        }

        // Continuous trigger action: silicon restarts the table as soon as its last entry has
        // converted (iadcTriggerActionContinuous in emlib), and firmware that samples the latest
        // result from a periodic interrupt relies on that. Conversion time is not
        // modeled, so the re-trigger period is a parameter, ContinuousScanPeriodMicroseconds
        // (default 20 us: eight entries per pass is 400 ksps, the order of what CLK_ADC ~10 MHz at
        // 2x oversampling delivers). A fidelity knob, not
        // a measurement.
        private void StartContinuousScan()
        {
            scanTimer.Limit = ContinuousScanPeriodMicroseconds == 0 ? 1UL : ContinuousScanPeriodMicroseconds;
            scanTimer.Value = 0;
            scanTimer.Enabled = true;
            ContinuousScanRunning = true;
        }

        private void StopContinuousScan()
        {
            if(ContinuousScanRunning)
            {
                this.Log(LogLevel.Debug, "Continuous scan stopped after {0} passes", ContinuousScanPasses);
            }
            ContinuousScanRunning = false;
            scanArmedForTimer = false;
            scanTimer.Enabled = false;
        }

        private void OnScanTimer()
        {
            if(!ContinuousScanRunning || !enabled.Value)
            {
                scanTimer.Enabled = false;
                ContinuousScanRunning = false;
                return;
            }
            ContinuousScanPasses++;
            RunScanPass();
            // With the TIMER as the trigger, ONCE means one table pass per timer EVENT, and the
            // timer keeps producing events: the queue stays enabled until SCANSTOP. (CONTINUOUS
            // would re-run the table back to back within one event; not distinguished here.)
        }

        public uint ContinuousScanPeriodMicroseconds { get; set; }

        // CLK_SRC_ADC, the clock the TIMER trigger counts. A platform fact from the clock tree.
        public uint SourceClockHertz { get; set; }
        public bool ContinuousScanRunning { get; private set; }
        public ulong ContinuousScanPasses { get; private set; }

        // One pass over the enabled scan table entries; false if nothing was converted.
        private bool RunScanPass()
        {
            var mask = scanMask.Value;
            if(mask == 0)
            {
                this.Log(LogLevel.Warning, "CMD.SCANSTART with MASKREQ == 0 - no scan table entry is enabled");
                return false;
            }

            for(var entry = 0; entry < ScanTableEntries; entry++)
            {
                if((mask & (1UL << entry)) == 0)
                {
                    continue;
                }

                var config = scanConfigSelect[entry].Value ? 1 : 0;
                var counts = Convert(scanPortPositive[entry].Value, scanPinPositive[entry].Value,
                                     scanPortNegative[entry].Value, scanPinNegative[entry].Value, config, out var bits);
                WarnIfCompareEnabled(scanCompare[entry].Value);

                // The ID a scan entry tags its result with is the entry's index in the table,
                // which is what a scan interrupt handler switches on to tell its inputs apart.
                PushFifo(scanFifo, Pack(counts, scanShowId.Value ? (uint)entry : 0u, (Alignment)scanAlignment.Value, bits),
                         scanDataValidLevel.Value, IrqScanFifoDvl, IrqScanFifoOf, "scan");
                SetInterrupt(IrqScanEntryDone);
            }

            SetInterrupt(IrqScanTableDone);
            UpdateInterrupts();
            return true;
        }

        // Turns an input voltage into ADC counts through the reference and analog gain selected
        // by CFG[config]. `bits` comes back because the result's width decides how a
        // left-aligned FIFO entry is packed.
        private uint Convert(ulong portPositive, ulong pinPositive, ulong portNegative, ulong pinNegative,
                             int config, out int bits)
        {
            bits = ResultBits(config);

            if((PortSelect)portNegative != PortSelect.Gnd)
            {
                this.Log(LogLevel.Warning, "Differential input (PORTNEG = {0}, PINNEG = {1}) is not modeled; measuring against ground",
                         (PortSelect)portNegative, pinNegative);
            }

            var microvolts = InputMicrovolts(portPositive, pinPositive);
            var fullScaleMicrovolts = FullScaleMicrovolts(config);
            if(fullScaleMicrovolts == 0)
            {
                return 0;
            }

            var maximum = (1UL << bits) - 1;
            var counts = (ulong)Math.Round((double)microvolts * maximum / fullScaleMicrovolts);
            if(counts > maximum)
            {
                this.Log(LogLevel.Debug, "Input {0} uV is above the {1} uV full scale; clamping the result to 0x{2:X}",
                         microvolts, fullScaleMicrovolts, maximum);
                counts = maximum;
            }
            return (uint)counts;
        }

        private uint InputMicrovolts(ulong port, ulong pin)
        {
            switch((PortSelect)port)
            {
                case PortSelect.Gnd:
                    return 0;
                case PortSelect.PortA:
                case PortSelect.PortB:
                case PortSelect.PortC:
                case PortSelect.PortD:
                    return ChannelMicrovolts(((int)port - (int)PortSelect.PortA) * PinsPerPort + (int)pin);
                default:
                    // SUPPLY, the DACs and the PADANA taps all need a model of something else on
                    // the die or the board. Feeding them a value would be inventing a measurement.
                    this.Log(LogLevel.Warning, "Positive input {0} is not modeled; reading 0 V", (PortSelect)port);
                    return 0;
            }
        }

        // Full scale = vRef / analog gain (em_iadc.h:
        // vRef 1210 mV at 0.5x gain gives 0xFFF == 2.42 V).
        private ulong FullScaleMicrovolts(int config)
        {
            var referenceMicrovolts = 1000UL * ReferenceMillivolts(config);
            switch((AnalogGain)analogGain[config].Value)
            {
                case AnalogGain.Gain0P5:
                    return referenceMicrovolts * 2;
                case AnalogGain.Gain1:
                    return referenceMicrovolts;
                case AnalogGain.Gain2:
                    return referenceMicrovolts / 2;
                case AnalogGain.Gain3:
                    return referenceMicrovolts / 3;
                case AnalogGain.Gain4:
                    return referenceMicrovolts / 4;
                default:
                    this.Log(LogLevel.Warning, "CFG{0}.ANALOGGAIN is the reserved value {1}; assuming unity gain",
                             config, analogGain[config].Value);
                    return referenceMicrovolts;
            }
        }

        private uint ReferenceMillivolts(int config)
        {
            switch((ReferenceSelect)referenceSelect[config].Value)
            {
                case ReferenceSelect.Vbgr:
                    return InternalBandgapMillivolts;
                case ReferenceSelect.Vref:
                    return externalReferenceMillivolts;
                case ReferenceSelect.Vref2P5:
                    return 2500;
                case ReferenceSelect.Vddx:
                    return supplyMillivolts;
                case ReferenceSelect.Vddx0P8Buf:
                    return supplyMillivolts * 8 / 10;
                default:
                    this.Log(LogLevel.Warning, "CFG{0}.REFSEL is the reserved value {1}; assuming the internal bandgap",
                             config, referenceSelect[config].Value);
                    return InternalBandgapMillivolts;
            }
        }

        // Normal and high-speed mode produce a 12-bit result at the minimum 2x oversampling
        // ratio, which is what every firmware in this tree configures (iadcCfgOsrHighSpeed2x).
        // Higher ratios widen the result - that is why the RIGHT16/RIGHT20 alignments exist - but
        // by how much is not something to guess at, so it is refused loudly instead.
        private int ResultBits(int config)
        {
            switch((AdcMode)adcMode[config].Value)
            {
                case AdcMode.Normal:
                case AdcMode.HighSpeed:
                    if((OsrHighSpeed)osrHighSpeed[config].Value != OsrHighSpeed.Ratio2x)
                    {
                        this.Log(LogLevel.Warning, "CFG{0}.OSRHS is {1}; the extra resolution oversampling buys is not modeled, "
                                 + "so the result stays 12-bit", config, (OsrHighSpeed)osrHighSpeed[config].Value);
                    }
                    return 12;
                case AdcMode.HighAccuracy:
                    return 16;
                default:
                    this.Log(LogLevel.Warning, "CFG{0}.ADCMODE is the reserved value {1}; assuming normal mode",
                             config, adcMode[config].Value);
                    return 12;
            }
        }

        // FIFO entry layout, matching IADC_ConvertRawDataToResult (em_iadc.c:150): right-aligned
        // entries carry the data in bits 23:0 and the ID in 31:24, left-aligned entries put the
        // data at the top and the ID in bits 7:0.
        private uint Pack(uint counts, uint id, Alignment alignment, int bits)
        {
            switch(alignment)
            {
                case Alignment.Right12:
                case Alignment.Right16:
                case Alignment.Right20:
                    return ((id & 0xFF) << 24) | (counts & 0x00FFFFFF);
                case Alignment.Left12:
                case Alignment.Left16:
                case Alignment.Left20:
                    var width = AlignmentWidth(alignment);
                    if(width != bits)
                    {
                        this.Log(LogLevel.Debug, "ALIGNMENT is {0} but the configured resolution is {1}-bit; "
                                 + "aligning to the field width the driver asked for", alignment, bits);
                    }
                    return ((counts << (32 - width)) & 0xFFFFFF00) | (id & 0xFF);
                default:
                    this.Log(LogLevel.Warning, "ALIGNMENT is the reserved value {0}; packing as RIGHT12", (uint)alignment);
                    return ((id & 0xFF) << 24) | (counts & 0x00FFFFFF);
            }
        }

        private static int AlignmentWidth(Alignment alignment)
        {
            switch(alignment)
            {
                case Alignment.Left16:
                    return 16;
                case Alignment.Left20:
                    return 20;
                default:
                    return 12;
            }
        }

        private void PushFifo(Queue<uint> fifo, uint entry, ulong dataValidLevel, int dataValidLevelInterrupt,
                              int overflowInterrupt, string name)
        {
            if(fifo.Count >= FifoDepth)
            {
                // Hardware drops the new sample and flags the overflow rather than overwriting
                // data the CPU has not read yet.
                this.Log(LogLevel.Warning, "The {0} FIFO is full ({1} entries); dropping 0x{2:X}", name, FifoDepth, entry);
                SetInterrupt(overflowInterrupt);
                return;
            }

            fifo.Enqueue(entry);
            // DVL encodes the level minus one: DVL == 0 is "valid at 1 entry" (VALID1).
            if(fifo.Count >= (int)dataValidLevel + 1)
            {
                SetInterrupt(dataValidLevelInterrupt);
            }
            UpdateDmaRequests();
        }

        private void UpdateDmaRequests()
        {
            if(scanDataValidLevel == null || singleDataValidLevel == null)
            {
                return; // registers not defined yet (construction)
            }
            ScanFifoRequest.Set(scanFifo.Count >= (int)scanDataValidLevel.Value + 1);
            SingleFifoRequest.Set(singleFifo.Count >= (int)singleDataValidLevel.Value + 1);
        }

        private ulong PopFifo(Queue<uint> fifo, ref uint last, int underflowInterrupt, string name)
        {
            if(!fifo.TryDequeue(out var entry))
            {
                this.Log(LogLevel.Warning, "Read from an empty {0} FIFO", name);
                SetInterrupt(underflowInterrupt);
                UpdateInterrupts();
                return 0;
            }
            last = entry;
            UpdateDmaRequests();
            return entry;
        }

        private ulong PeekFifo(Queue<uint> fifo, uint last)
        {
            return fifo.Count > 0 ? fifo.Peek() : last;
        }

        private void WarnIfCompareEnabled(bool compareEnabled)
        {
            if(compareEnabled)
            {
                this.Log(LogLevel.Warning, "The digital window comparator (CMP) is enabled but not modeled; "
                         + "SINGLECMP/SCANCMP will never be raised");
            }
        }

        private bool TryValidateChannel(int channel)
        {
            if(channel < 0 || channel >= ChannelCount)
            {
                this.Log(LogLevel.Error, "Channel {0} is out of range; the IADC reaches ports A-D pins 0-15, i.e. channels 0-{1}",
                         channel, ChannelCount - 1);
                return false;
            }
            return true;
        }

        private static string ChannelName(int channel)
        {
            return $"P{(char)('A' + channel / PinsPerPort)}{channel % PinsPerPort}";
        }

        private void SetInterrupt(int bit)
        {
            interruptFlags[bit].Value = true;
        }

        private void UpdateInterrupts()
        {
            var pending = InterruptBits.Keys.Any(bit => interruptFlags[bit].Value && interruptEnable[bit].Value);
            IRQ.Set(pending);
        }

        private readonly uint supplyMillivolts;
        private readonly uint externalReferenceMillivolts;

        // The analog side is a property of the board, not of the MCU, so it deliberately survives
        // a machine reset - the mains does not stop because the firmware rebooted.
        private readonly uint[] sineAmplitudeMicrovolts;
        private readonly uint[] sineFrequencyMilliHertz;
        private readonly int[] sinePhaseDegrees;
        private readonly Queue<uint> singleFifo;
        private readonly Queue<uint> scanFifo;
        private readonly LimitTimer scanTimer;
        private IValueRegisterField timerCompare;
        private bool scanArmedForTimer;

        private uint lastSingleData;
        private uint lastScanData;
        private bool timerRunning;

        private IFlagRegisterField enabled;
        private IValueRegisterField scanMask;

        private readonly IFlagRegisterField[] interruptFlags = new IFlagRegisterField[32];
        private readonly IFlagRegisterField[] interruptEnable = new IFlagRegisterField[32];

        private IValueRegisterField scanTriggerSelect;
        private IFlagRegisterField scanTriggerContinuous;
        private IValueRegisterField singleTriggerSelect;
        private IFlagRegisterField singleTriggerContinuous;

        private readonly IValueRegisterField[] adcMode = new IValueRegisterField[ConfigCount];
        private readonly IValueRegisterField[] osrHighSpeed = new IValueRegisterField[ConfigCount];
        private readonly IValueRegisterField[] analogGain = new IValueRegisterField[ConfigCount];
        private readonly IValueRegisterField[] referenceSelect = new IValueRegisterField[ConfigCount];

        private IValueRegisterField singleAlignment;
        private IFlagRegisterField singleShowId;
        private IValueRegisterField singleDataValidLevel;
        private IValueRegisterField scanAlignment;
        private IFlagRegisterField scanShowId;
        private IValueRegisterField scanDataValidLevel;

        private IValueRegisterField singlePinNegative;
        private IValueRegisterField singlePortNegative;
        private IValueRegisterField singlePinPositive;
        private IValueRegisterField singlePortPositive;
        private IFlagRegisterField singleConfigSelect;
        private IFlagRegisterField singleCompare;

        private readonly IValueRegisterField[] scanPinNegative = new IValueRegisterField[ScanTableEntries];
        private readonly IValueRegisterField[] scanPortNegative = new IValueRegisterField[ScanTableEntries];
        private readonly IValueRegisterField[] scanPinPositive = new IValueRegisterField[ScanTableEntries];
        private readonly IValueRegisterField[] scanPortPositive = new IValueRegisterField[ScanTableEntries];
        private readonly IFlagRegisterField[] scanConfigSelect = new IFlagRegisterField[ScanTableEntries];
        private readonly IFlagRegisterField[] scanCompare = new IFlagRegisterField[ScanTableEntries];

        private const uint IpVersion = 0x3;
        private const uint CfgResetValue = 0x00002060;
        private const uint ScaleResetValue = 0x8002C000;
        private const uint FifoCfgResetValue = 0x00000030;

        // DVL runs up to VALID8 on series-2 config 4 (EFR32MG24), so the FIFO holds 8 entries.
        private const int FifoDepth = 8;
        private const int ConfigCount = 2;
        private const uint ScanTableEntries = 16;
        private const int PinsPerPort = 16;
        private const int PortCount = 4;
        private const int ChannelCount = PortCount * PinsPerPort;

        // The ID a single conversion tags its result with when SHOWID is set.
        private const uint SingleQueueId = 0x20;
        private const uint InternalBandgapMillivolts = 1210;

        private const long RegisterOffsetMask = 0x0FFF;
        private const long AliasMask = 0x3000;
        private const long AliasSet = 0x1000;
        private const long AliasClear = 0x2000;
        private const long AliasToggle = 0x3000;

        private const int IrqSingleFifoDvl = 0;
        private const int IrqScanFifoDvl = 1;
        private const int IrqScanEntryDone = 7;
        private const int IrqScanTableDone = 8;
        private const int IrqSingleDone = 9;
        private const int IrqSingleFifoOf = 16;
        private const int IrqScanFifoOf = 17;
        private const int IrqSingleFifoUf = 18;
        private const int IrqScanFifoUf = 19;

        // Exactly the bits _IADC_IF_MASK (0x800F338F) covers. Anything not listed stays reserved,
        // so a stray write to one is reported rather than silently stored.
        private static readonly Dictionary<int, string> InterruptBits = new Dictionary<int, string>
        {
            { 0, "SINGLEFIFODVL" },
            { 1, "SCANFIFODVL" },
            { 2, "SINGLECMP" },
            { 3, "SCANCMP" },
            { 7, "SCANENTRYDONE" },
            { 8, "SCANTABLEDONE" },
            { 9, "SINGLEDONE" },
            { 12, "POLARITYERR" },
            { 13, "PORTALLOCERR" },
            { 16, "SINGLEFIFOOF" },
            { 17, "SCANFIFOOF" },
            { 18, "SINGLEFIFOUF" },
            { 19, "SCANFIFOUF" },
            { 31, "EM23ABORTERROR" },
        };

        // Reading either of these pops a FIFO entry, so a Set/Clr/Tgl alias write - which would
        // have to read first - must not be attempted against them.
        private static readonly HashSet<long> ReadModifiesState = new HashSet<long>
        {
            (long)Registers.Singlefifodata,
            (long)Registers.Scanfifodata,
        };

        private enum PortSelect
        {
            Gnd = 0,
            Supply = 1,
            Dac = 2,
            PadAna0 = 4,
            PadAna2 = 5,
            PortA = 8,
            PortB = 9,
            PortC = 10,
            PortD = 11,
        }

        private enum ReferenceSelect
        {
            Vbgr = 0,
            Vref = 1,
            Vref2P5 = 2,
            Vddx = 3,
            Vddx0P8Buf = 4,
        }

        private enum AnalogGain
        {
            Gain0P5 = 1,
            Gain1 = 2,
            Gain2 = 3,
            Gain3 = 4,
            Gain4 = 5,
        }

        private enum AdcMode
        {
            Normal = 0,
            HighSpeed = 1,
            HighAccuracy = 2,
        }

        private enum OsrHighSpeed
        {
            Ratio2x = 0,
            Ratio4x = 1,
            Ratio8x = 2,
            Ratio16x = 3,
            Ratio32x = 4,
            Ratio64x = 5,
        }

        private enum Alignment
        {
            Right12 = 0,
            Right16 = 1,
            Right20 = 2,
            Left12 = 3,
            Left16 = 4,
            Left20 = 5,
        }

        private enum TriggerSource
        {
            Immediate = 0,
            Timer = 1,
            PrsClkGrp = 2,
            PrsPos = 3,
            PrsNeg = 4,
        }

        private enum Registers
        {
            Ipversion = 0x000,
            En = 0x004,
            Ctrl = 0x008,
            Cmd = 0x00C,
            Timer = 0x010,
            Status = 0x014,
            Maskreq = 0x018,
            Stmask = 0x01C,
            Cmpthr = 0x020,
            If = 0x024,
            Ien = 0x028,
            Trigger = 0x02C,
            Cfg0 = 0x048,
            Scale0 = 0x050,
            Sched0 = 0x054,
            Cfg1 = 0x058,
            Scale1 = 0x060,
            Sched1 = 0x064,
            Singlefifocfg = 0x070,
            Singlefifodata = 0x074,
            Singlefifostat = 0x078,
            Singledata = 0x07C,
            Scanfifocfg = 0x080,
            Scanfifodata = 0x084,
            Scanfifostat = 0x088,
            Scandata = 0x08C,
            Single = 0x098,
            Scantable = 0x0A0,
        }
    }

    // The default voltage source of a SiLabs_IADC_3 channel: a level set from the Monitor or a
    // test (optionally a RESD stream), standing for the board's analog wiring. Unlike a plain
    // ADCChannelSource it keeps its level across a machine reset - the mains does not stop
    // because the firmware rebooted.
    public class SiLabs_IADC_3_InputSource : ADCDefaultChannelSource, IPeripheral
    {
        public new void Reset()
        {
            // Intentionally empty: the level is board state, not peripheral state.
        }
    }
}
