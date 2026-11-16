//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Timers
{
    // EFR32xG2 PCNT - the pulse counter, in external-clock mode: counting EXTERNAL
    // events rather than an internal clock.
    //
    // A typical use is counting the cycles of a carrier that a TIMER generates:
    //
    //     TIMERn CCx (the carrier)  ->  PRS async channel  ->  PCNT0.IN0
    //     PCNT0 overflow  ->  its interrupt
    //
    // so that a burst's length is counted in carrier cycles and the overflow interrupt ends it.
    //
    // Events arrive as GPIO transitions on input 0, which is how a .repl wires a producer to it:
    //
    //     prs: 5 -> pcnt0@0
    //
    // Counting follows the firmware's configuration rather than the wire: an edge is counted only
    // while the module is enabled and a counter is running, and the direction comes from
    // CTRL.CNTDIR. Which EDGE counts follows CTRL.EDGE - a model that counted both would double
    // every burst length.
    //
    // NOT modeled, each listed so its absence is not mistaken for a bug:
    //  * the AUXILIARY counter (AUXCNT) and quadrature/S1 direction control - only a
    //    single input is modeled;
    //  * the input FILTER and hysteresis (CTRL.FILT, OVSCTRL) - edges are counted as they arrive;
    //  * OVSSINGLE mode, where the counter is clocked by an internal oversampling clock rather
    //    than the external event. EXTCLKSINGLE is the mode modeled;
    //  * TOPB buffering: a write to TOPB takes effect immediately rather than at the next
    //    overflow.
    public class SiLabs_PCNT : BasicDoubleWordPeripheral, IKnownSize, IGPIOReceiver
    {
        public SiLabs_PCNT(Machine machine) : base(machine)
        {
            IRQ = new GPIO();
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            counter = 0;
            top = MaxCount;
            interruptFlags = 0;
            interruptEnable = 0;
            counterRunning = false;
            lastInputLevel = false;
            UpdateInterrupts();
        }

        // The counting input. Input 0 is IN0/S0, the one a PRS channel feeds.
        public void OnGPIO(int number, bool value)
        {
            if(number != 0)
            {
                this.Log(LogLevel.Warning, "Event on input {0}, which is not modeled; only IN0 counts", number);
                return;
            }

            var rising = value && !lastInputLevel;
            var falling = !value && lastInputLevel;
            lastInputLevel = value;

            if(!enabled.Value || !counterRunning)
            {
                return;
            }
            // CTRL.EDGE picks which transition is an event. Counting both would make every
            // interval half as long as the firmware intends.
            if(countOnFallingEdge.Value ? !falling : !rising)
            {
                return;
            }
            Count();
        }

        public long Size => 0x4000;

        // The SET/CLR/TGL aliases at +0x1000/+0x2000/+0x3000. emlib writes through them - a bare
        // `PCNT0->EN_SET = PCNT_EN_EN` is how the module is enabled - so a model that only defines
        // the base offsets silently drops half the driver's writes.
        public override void WriteDoubleWord(long offset, uint value)
        {
            var alias = offset & AliasMask;
            var register = offset & ~AliasMask;
            switch(alias)
            {
                case 0:
                    base.WriteDoubleWord(register, value);
                    return;
                case AliasSet:
                    base.WriteDoubleWord(register, base.ReadDoubleWord(register) | value);
                    return;
                case AliasClear:
                    base.WriteDoubleWord(register, base.ReadDoubleWord(register) & ~value);
                    return;
                case AliasToggle:
                    base.WriteDoubleWord(register, base.ReadDoubleWord(register) ^ value);
                    return;
            }
        }

        public override uint ReadDoubleWord(long offset)
        {
            return base.ReadDoubleWord(offset & ~AliasMask);
        }

        private const long AliasMask = 0x3000;
        private const long AliasSet = 0x1000;
        private const long AliasClear = 0x2000;
        private const long AliasToggle = 0x3000;

        public GPIO IRQ { get; }

        private void Count()
        {
            if(countDown.Value)
            {
                if(counter == 0)
                {
                    counter = top;
                    SetFlag(Interrupts.Underflow);
                }
                else
                {
                    counter--;
                }
                return;
            }

            // Counting up: the overflow lands when the counter passes TOP, and the counter
            // reloads from zero. TOP of 0 therefore overflows on every single event, which is
            // what a firmware asking for "interrupt me each carrier cycle" writes.
            if(counter >= top)
            {
                counter = 0;
                SetFlag(Interrupts.Overflow);
            }
            else
            {
                counter++;
            }
        }

        private void SetFlag(Interrupts flag)
        {
            interruptFlags |= (ulong)flag;
            UpdateInterrupts();
        }

        private void DefineRegisters()
        {
            Registers.IpVersion.Define(this, IpVersion)
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => IpVersion, name: "IPVERSION")
            ;

            Registers.Enable.Define(this)
                .WithFlag(0, out enabled, name: "EN",
                          changeCallback: (_, value) => { if(!value) counterRunning = false; })
                .WithReservedBits(1, 31)
            ;

            Registers.SoftwareReset.Define(this)
                .WithFlag(0, FieldMode.Write, writeCallback: (_, value) => { if(value) Reset(); }, name: "SWRST")
                .WithReservedBits(1, 31)
            ;

            Registers.Configuration.Define(this)
                .WithEnumField(0, 2, out mode, name: "MODE")
                .WithTaggedFlag("FILTEN", 2)
                .WithTaggedFlag("HYST", 3)
                .WithTaggedFlag("S1CDIR", 4)
                .WithTag("CNTEV", 5, 2)
                .WithTag("AUXCNTEV", 7, 2)
                .WithTaggedFlag("DEBUGHALT", 9)
                .WithReservedBits(10, 22)
            ;

            Registers.Control.Define(this)
                .WithTaggedFlag("CNTRSTEN", 0)
                .WithFlag(1, out countDown, name: "CNTDIR")
                .WithTaggedFlag("AUXCNTRSTEN", 2)
                .WithFlag(3, out countOnFallingEdge, name: "EDGE")
                .WithTaggedFlag("TCCPRSPOL", 4)
                .WithReservedBits(5, 27)
            ;

            Registers.Command.Define(this)
                .WithFlag(0, FieldMode.Write, writeCallback: (_, value) => { if(value) CoreReset(); }, name: "CORERST")
                .WithFlag(1, FieldMode.Write, writeCallback: (_, value) => { if(value) counter = 0; }, name: "CNTRST")
                .WithFlag(2, FieldMode.Write, name: "AUXCNTRST")
                .WithReservedBits(3, 1)
                .WithFlag(4, FieldMode.Write, name: "LCNTIM")
                .WithReservedBits(5, 3)
                .WithFlag(8, FieldMode.Write, writeCallback: (_, value) => { if(value) counterRunning = true; }, name: "STARTCNT")
                .WithFlag(9, FieldMode.Write, writeCallback: (_, value) => { if(value) counterRunning = false; }, name: "STOPCNT")
                .WithReservedBits(10, 22)
            ;

            Registers.Status.Define(this)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => countDown.Value, name: "DIR")
                .WithReservedBits(1, 31)
            ;

            Registers.InterruptFlags.Define(this)
                .WithValueField(0, 8, name: "IF",
                                valueProviderCallback: _ => interruptFlags,
                                writeCallback: (_, value) => { interruptFlags = value & FlagMask; UpdateInterrupts(); })
                .WithReservedBits(8, 24)
            ;

            Registers.InterruptEnable.Define(this)
                .WithValueField(0, 8, name: "IEN",
                                valueProviderCallback: _ => interruptEnable,
                                writeCallback: (_, value) => { interruptEnable = value & FlagMask; UpdateInterrupts(); })
                .WithReservedBits(8, 24)
            ;

            Registers.CounterValue.Define(this)
                .WithValueField(0, 16, FieldMode.Read, valueProviderCallback: _ => counter, name: "CNT")
                .WithReservedBits(16, 16)
            ;

            Registers.AuxCounterValue.Define(this)
                .WithValueField(0, 16, FieldMode.Read, valueProviderCallback: _ => 0, name: "AUXCNT")
                .WithReservedBits(16, 16)
            ;

            Registers.TopValue.Define(this, (uint)MaxCount)
                .WithValueField(0, 16, name: "TOP",
                                valueProviderCallback: _ => top,
                                writeCallback: (_, value) => top = value)
                .WithReservedBits(16, 16)
            ;

            Registers.TopBuffer.Define(this, (uint)MaxCount)
                .WithValueField(0, 16, name: "TOPB",
                                valueProviderCallback: _ => top,
                                writeCallback: (_, value) => top = value)
                .WithReservedBits(16, 16)
            ;

            Registers.OversamplingControl.Define(this)
                .WithTag("OVSCTRL", 0, 32)
            ;

            Registers.SyncBusy.Define(this)
                // Nothing here takes time to synchronize, so emlib's `while(PCNT->SYNCBUSY)` spins
                // exit immediately.
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => 0, name: "SYNCBUSY")
            ;

            Registers.Lock.Define(this)
                .WithTag("LOCKKEY", 0, 16)
                .WithReservedBits(16, 16)
            ;
        }

        private void CoreReset()
        {
            counter = 0;
            counterRunning = false;
            interruptFlags = 0;
            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            var active = (interruptFlags & interruptEnable) != 0;
            machine.ClockSource.ExecuteInLock(delegate
            {
                IRQ.Set(active);
            });
        }

        private IFlagRegisterField enabled;
        private IFlagRegisterField countDown;
        private IFlagRegisterField countOnFallingEdge;
        private IEnumRegisterField<Mode> mode;

        private ulong counter;
        private ulong top;
        private ulong interruptFlags;
        private ulong interruptEnable;
        private bool counterRunning;
        private bool lastInputLevel;

        private const ulong MaxCount = 0xFFFF;      // 16-bit counter
        private const ulong FlagMask = 0xFF;
        private const uint IpVersion = 1;

        private enum Mode
        {
            OversamplingSingle = 0,
            ExternalClockSingle = 1,
            ExternalClockQuadrature = 2,
            OversamplingQuadrature = 3,
        }

        [Flags]
        private enum Interrupts : ulong
        {
            Underflow = 1 << 0,
            Overflow = 1 << 1,
            DirectionChange = 1 << 2,
        }

        private enum Registers
        {
            IpVersion = 0x000,
            Enable = 0x004,
            SoftwareReset = 0x008,
            Configuration = 0x00C,
            Control = 0x010,
            Command = 0x014,
            Status = 0x018,
            InterruptFlags = 0x01C,
            InterruptEnable = 0x020,
            CounterValue = 0x024,
            AuxCounterValue = 0x028,
            TopValue = 0x02C,
            TopBuffer = 0x030,
            OversamplingControl = 0x034,
            SyncBusy = 0x038,
            Lock = 0x03C,
        }
    }
}
