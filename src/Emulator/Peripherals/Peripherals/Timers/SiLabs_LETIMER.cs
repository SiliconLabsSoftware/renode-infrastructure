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
    // EFR32xG2 LETIMER - the low-energy timer, a 24-bit DOWN counter that keeps running in EM2
    // and can therefore wake a sleeping part.
    //
    // Modeled for firmware that arms it as a one-shot wake-up from sleep through emlib's
    // LETIMER_Init / LETIMER_CompareSet / LETIMER_Enable, so this models the registers those
    // touch.
    //
    // Modeled: EN, SWRST, CTRL (REPMODE, CNTTOPEN, prescaler), CMD (START/STOP/CLEAR), STATUS
    // (RUNNING), CNT, COMP0/COMP1, TOP, IF/IEN with COMP0/COMP1/UF and their SET/CLR/TGL aliases,
    // and SYNCBUSY (which always reads idle - nothing here takes time to synchronize).
    //
    // NOT modeled, each stated so its absence is not read as a bug:
    //  * the UNDERFLOW OUTPUT ACTIONS (CTRL.UFOA0/UFOA1) that can toggle or pulse a pad. Nothing
    //    here drives a pin, so firmware using the LETIMER to generate a waveform needs that
    //    added first;
    //  * REP0/REP1 repeat counters, and therefore BUFFERED repeat mode, which is treated as FREE;
    //  * PRS input modes (PRSMODE) - the counter only ever runs from its own clock here;
    //  * DEBUGRUN, and the LOCK register, which accepts writes and protects nothing.
    public class SiLabs_LETIMER : BasicDoubleWordPeripheral, IKnownSize
    {
        public SiLabs_LETIMER(Machine machine, long frequency = DefaultFrequency) : base(machine)
        {
            this.frequency = frequency;
            IRQ = new GPIO();

            innerTimer = new LimitTimer(machine.ClockSource, (ulong)frequency, this, "letimer", limit: MaxCount,
                                        direction: Time.Direction.Descending, enabled: false,
                                        eventEnabled: true, autoUpdate: false);
            innerTimer.LimitReached += OnUnderflow;

            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            innerTimer.Reset();
            innerTimer.Enabled = false;
            interruptFlags = 0;
            interruptEnable = 0;
            top = MaxCount;
            compare0 = 0;
            compare1 = 0;
            running = false;
            UpdateInterrupts();
        }

        public long Size => 0x4000;

        // The SET/CLR/TGL aliases at +0x1000/+0x2000/+0x3000. emlib writes through them - a bare
        // `LETIMER0->EN_SET = LETIMER_EN_EN` is how the module is enabled - so a model that only defines
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

        private void DefineRegisters()
        {
            Registers.IpVersion.Define(this, IpVersion)
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => IpVersion, name: "IPVERSION")
            ;

            Registers.Enable.Define(this)
                .WithFlag(0, out enabled, name: "EN",
                          changeCallback: (_, value) =>
                          {
                              if(!value)
                              {
                                  Stop();
                              }
                          })
                .WithReservedBits(1, 31)
            ;

            Registers.SoftwareReset.Define(this)
                .WithFlag(0, FieldMode.Write, writeCallback: (_, value) => { if(value) Reset(); }, name: "SWRST")
                .WithReservedBits(1, 31)
            ;

            Registers.Control.Define(this)
                .WithEnumField(0, 2, out repeatMode, name: "REPMODE")
                .WithTag("UFOA0", 2, 2)
                .WithTag("UFOA1", 4, 2)
                .WithTaggedFlag("OPOL0", 6)
                .WithTaggedFlag("OPOL1", 7)
                .WithTaggedFlag("BUFTOP", 8)
                .WithFlag(9, out counterTopEnabled, name: "CNTTOPEN")
                .WithReservedBits(10, 2)
                .WithTaggedFlag("DEBUGRUN", 12)
                .WithReservedBits(13, 3)
                .WithValueField(16, 4, out prescaler, name: "CNTPRESC",
                                changeCallback: (_, __) => UpdateFrequency())
                .WithReservedBits(20, 12)
            ;

            Registers.Command.Define(this)
                .WithFlag(0, FieldMode.Write, writeCallback: (_, value) => { if(value) Start(); }, name: "START")
                .WithFlag(1, FieldMode.Write, writeCallback: (_, value) => { if(value) Stop(); }, name: "STOP")
                .WithFlag(2, FieldMode.Write, writeCallback: (_, value) => { if(value) ClearCounter(); }, name: "CLEAR")
                .WithReservedBits(3, 29)
            ;

            Registers.Status.Define(this)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => running, name: "RUNNING")
                .WithTaggedFlag("LETIMERLOCKSTATUS", 1)
                .WithReservedBits(2, 30)
            ;

            Registers.CounterValue.Define(this)
                .WithValueField(0, 24, name: "CNT",
                                valueProviderCallback: _ => (ulong)innerTimer.Value & MaxCount,
                                writeCallback: (_, value) => SetCounter(value))
                .WithReservedBits(24, 8)
            ;

            Registers.CompareValue0.Define(this)
                .WithValueField(0, 24, name: "COMP0",
                                valueProviderCallback: _ => compare0,
                                writeCallback: (_, value) =>
                                {
                                    compare0 = value;
                                    // CNTTOPEN makes COMP0 the counter's top, which is how emlib's
                                    // LETIMER_CompareSet sets a period rather than a match.
                                    if(counterTopEnabled.Value)
                                    {
                                        top = value;
                                    }
                                })
                .WithReservedBits(24, 8)
            ;

            Registers.CompareValue1.Define(this)
                .WithValueField(0, 24, out var compare1Field, name: "COMP1",
                                valueProviderCallback: _ => compare1,
                                writeCallback: (_, value) => compare1 = value)
                .WithReservedBits(24, 8)
            ;

            Registers.TopValue.Define(this, (uint)MaxCount)
                .WithValueField(0, 24, name: "TOP",
                                valueProviderCallback: _ => top,
                                writeCallback: (_, value) => top = value)
                .WithReservedBits(24, 8)
            ;

            Registers.TopBuffer.Define(this, (uint)MaxCount)
                .WithValueField(0, 24, name: "TOPBUFF",
                                valueProviderCallback: _ => top,
                                writeCallback: (_, value) => top = value)
                .WithReservedBits(24, 8)
            ;

            Registers.RepeatCounter0.Define(this)
                .WithTag("REP0", 0, 8)
                .WithReservedBits(8, 24)
            ;

            Registers.RepeatCounter1.Define(this)
                .WithTag("REP1", 0, 8)
                .WithReservedBits(8, 24)
            ;

            Registers.InterruptFlags.Define(this)
                .WithValueField(0, 5, name: "IF",
                                valueProviderCallback: _ => interruptFlags,
                                writeCallback: (_, value) => { interruptFlags = value & FlagMask; UpdateInterrupts(); })
                .WithReservedBits(5, 27)
            ;

            Registers.InterruptEnable.Define(this)
                .WithValueField(0, 5, name: "IEN",
                                valueProviderCallback: _ => interruptEnable,
                                writeCallback: (_, value) => { interruptEnable = value & FlagMask; UpdateInterrupts(); })
                .WithReservedBits(5, 27)
            ;

            Registers.Lock.Define(this)
                .WithTag("LOCKKEY", 0, 16)
                .WithReservedBits(16, 16)
            ;

            Registers.SyncBusy.Define(this)
                // Nothing here takes time to synchronize, so this reads idle forever - which is
                // exactly what emlib's `while(LETIMER->SYNCBUSY)` spins need to exit.
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => 0, name: "SYNCBUSY")
            ;

            Registers.PrsMode.Define(this)
                .WithTag("PRSMODE", 0, 32)
            ;
        }

        private void Start()
        {
            if(!enabled.Value)
            {
                this.Log(LogLevel.Warning, "START written while the module is disabled; ignoring it");
                return;
            }
            UpdateFrequency();
            // A start loads the counter from TOP unless software has already put a value there,
            // which is how a one-shot of N ticks is armed.
            if(innerTimer.Value == 0)
            {
                innerTimer.Value = top;
            }
            innerTimer.Limit = MaxCount;
            innerTimer.Enabled = true;
            running = true;
            this.Log(LogLevel.Debug, "Started at {0} ticks, {1}", innerTimer.Value,
                     repeatMode.Value == RepeatMode.OneShot ? "one-shot" : "free-running");
        }

        private void Stop()
        {
            innerTimer.Enabled = false;
            running = false;
        }

        private void ClearCounter()
        {
            innerTimer.Value = 0;
        }

        private void SetCounter(ulong value)
        {
            innerTimer.Value = value & MaxCount;
        }

        private void UpdateFrequency()
        {
            var divider = 1u << (int)prescaler.Value;
            innerTimer.Divider = divider;
        }

        private void OnUnderflow()
        {
            interruptFlags |= (ulong)Interrupts.Underflow;
            // COMP0/COMP1 matches: with a down-counter reaching zero, a compare of zero coincides
            // with the underflow. Matches at other values are not modeled - nothing in this tree
            // uses them, and pretending otherwise would be inventing edges.
            if(compare0 == 0)
            {
                interruptFlags |= (ulong)Interrupts.Compare0;
            }
            if(compare1 == 0)
            {
                interruptFlags |= (ulong)Interrupts.Compare1;
            }

            if(repeatMode.Value == RepeatMode.OneShot)
            {
                Stop();
            }
            else
            {
                innerTimer.Value = top;
            }
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

        private readonly LimitTimer innerTimer;
        private readonly long frequency;

        private IFlagRegisterField enabled;
        private IFlagRegisterField counterTopEnabled;
        private IEnumRegisterField<RepeatMode> repeatMode;
        private IValueRegisterField prescaler;

        private ulong interruptFlags;
        private ulong interruptEnable;
        private ulong top;
        private ulong compare0;
        private ulong compare1;
        private bool running;

        private const long DefaultFrequency = 1000;     // the ULFRCO
        private const ulong MaxCount = 0xFFFFFF;        // 24-bit counter
        private const ulong FlagMask = 0x1F;
        private const uint IpVersion = 1;

        private enum RepeatMode
        {
            Free = 0,
            OneShot = 1,
            Buffered = 2,
            Double = 3,
        }

        [Flags]
        private enum Interrupts : ulong
        {
            Compare0 = 1 << 0,
            Compare1 = 1 << 1,
            Underflow = 1 << 2,
            Repeat0 = 1 << 3,
            Repeat1 = 1 << 4,
        }

        private enum Registers
        {
            IpVersion = 0x000,
            Enable = 0x004,
            SoftwareReset = 0x008,
            Control = 0x00C,
            Command = 0x010,
            Status = 0x014,
            CounterValue = 0x018,
            CompareValue0 = 0x01C,
            CompareValue1 = 0x020,
            TopValue = 0x024,
            TopBuffer = 0x028,
            RepeatCounter0 = 0x02C,
            RepeatCounter1 = 0x030,
            InterruptFlags = 0x034,
            InterruptEnable = 0x038,
            Lock = 0x03C,
            SyncBusy = 0x040,
            PrsMode = 0x050,
        }
    }
}
