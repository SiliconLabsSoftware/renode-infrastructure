//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous.SiLabs
{
    // Series-2 buck DC-DC converter with the EFM32PG23 register layout (IF 0x28, STATUS 0x30,
    // SYNCBUSY 0x34, LOCK 0x40, LOCKSTATUS 0x44).
    //
    // CTRL.MODE selects bypass (0) or regulation (1). STATUS reflects it at once - BYPSW in
    // bypass, RUNNING|WARM in regulation - and the matching IF bit is raised, so code that polls
    // either register (emlib's EMU_DCDCModeSet(), the Gecko bootloader's bypass entry) completes.
    // LOCK takes the 0xABCD unlock key and LOCKSTATUS reports it; SYNCBUSY reads 0.
    // EM01CTRL0/EM23CTRL0/PFMXCTRL/IEN are stored. There is no electrical behavior: VREGIN,
    // current limits, EM4 and the PFMX/PP mode flags stay clear.
    public class SiLabs_DCDC_1 : IDoubleWordPeripheral, IKnownSize
    {
        public SiLabs_DCDC_1(IMachine machine)
        {
            IRQ = new GPIO();
            registers = BuildRegisters();
            Reset();
        }

        public void Reset()
        {
            registers.Reset();
            regulating = false;
            locked = false;
            interruptFlags = InterruptBypassSwitch;   // out of reset the bypass switch is closed
            interruptEnable = 0;
            IRQ.Unset();
        }

        public uint ReadDoubleWord(long offset)
        {
            var (baseOffset, _) = Alias(offset);
            return registers.Read(baseOffset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            var (baseOffset, mode) = Alias(offset);
            switch(mode)
            {
            case AliasMode.Set:
                value = registers.Read(baseOffset) | value;
                break;
            case AliasMode.Clear:
                value = registers.Read(baseOffset) & ~value;
                break;
            case AliasMode.Toggle:
                value = registers.Read(baseOffset) ^ value;
                break;
            }
            registers.Write(baseOffset, value);
        }

        public long Size => 0x4000;

        public GPIO IRQ { get; }

        public bool Regulating => regulating;

        public bool Locked => locked;

        private void SetMode(bool regulation)
        {
            if(regulation == regulating)
            {
                return;
            }
            regulating = regulation;
            interruptFlags |= regulation ? (InterruptRegulation | InterruptRunning | InterruptWarm) : InterruptBypassSwitch;
            UpdateInterrupts();
        }

        private uint Status()
        {
            return regulating ? (StatusRunning | StatusWarm) : StatusBypassSwitch;
        }

        private void UpdateInterrupts()
        {
            IRQ.Set((interruptFlags & interruptEnable) != 0);
        }

        private DoubleWordRegisterCollection BuildRegisters()
        {
            return new DoubleWordRegisterCollection(this, new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.IpVersion, new DoubleWordRegister(this, 0x2)
                    .WithValueField(0, 32, FieldMode.Read, name: "IPVERSION")},
                {(long)Registers.Control, new DoubleWordRegister(this, 0x00000100)
                    .WithFlag(0, writeCallback: (_, v) => SetMode(v), valueProviderCallback: _ => regulating, name: "MODE")
                    .WithReservedBits(1, 3)
                    .WithValueField(4, 2, name: "IPKTMAXCTRL")
                    .WithReservedBits(6, 2)
                    .WithFlag(8, name: "DCMONLYEN")
                    .WithReservedBits(9, 23)},
                {(long)Registers.Em01Control0, new DoubleWordRegister(this, 0x00000109)
                    .WithValueField(0, 32, name: "EM01CTRL0")},
                {(long)Registers.Em23Control0, new DoubleWordRegister(this, 0x00000109)
                    .WithValueField(0, 32, name: "EM23CTRL0")},
                {(long)Registers.PfmxControl, new DoubleWordRegister(this, 0x0000000B)
                    .WithValueField(0, 32, name: "PFMXCTRL")},
                {(long)Registers.InterruptFlags, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, v) => { interruptFlags = (uint)v; UpdateInterrupts(); },
                                    valueProviderCallback: _ => interruptFlags, name: "IF")},
                {(long)Registers.InterruptEnable, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, v) => { interruptEnable = (uint)v; UpdateInterrupts(); },
                                    valueProviderCallback: _ => interruptEnable, name: "IEN")},
                {(long)Registers.Status, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => Status(), name: "STATUS")},
                {(long)Registers.SyncBusy, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => 0, name: "SYNCBUSY")},
                {(long)Registers.Lock, new DoubleWordRegister(this)
                    .WithValueField(0, 16, FieldMode.Write, writeCallback: (_, v) => locked = (v != UnlockKey), name: "LOCKKEY")
                    .WithReservedBits(16, 16)},
                {(long)Registers.LockStatus, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => locked, name: "LOCK")
                    .WithReservedBits(1, 31)},
            });
        }

        private static (long, AliasMode) Alias(long offset)
        {
            if(offset >= 0x3000)
            {
                return (offset - 0x3000, AliasMode.Toggle);
            }
            if(offset >= 0x2000)
            {
                return (offset - 0x2000, AliasMode.Clear);
            }
            if(offset >= 0x1000)
            {
                return (offset - 0x1000, AliasMode.Set);
            }
            return (offset, AliasMode.None);
        }

        private readonly DoubleWordRegisterCollection registers;
        private bool regulating;
        private bool locked;
        private uint interruptFlags;
        private uint interruptEnable;

        private const uint UnlockKey = 0xABCD;

        private const uint StatusBypassSwitch = 1u << 0;
        private const uint StatusWarm = 1u << 1;
        private const uint StatusRunning = 1u << 2;

        private const uint InterruptBypassSwitch = 1u << 0;
        private const uint InterruptWarm = 1u << 1;
        private const uint InterruptRunning = 1u << 2;
        private const uint InterruptRegulation = 1u << 5;

        private enum AliasMode
        {
            None,
            Set,
            Clear,
            Toggle,
        }

        private enum Registers : long
        {
            IpVersion = 0x00,
            Control = 0x04,
            Em01Control0 = 0x08,
            Em23Control0 = 0x10,
            PfmxControl = 0x18,
            InterruptFlags = 0x28,
            InterruptEnable = 0x2C,
            Status = 0x30,
            SyncBusy = 0x34,
            Lock = 0x40,
            LockStatus = 0x44,
        }
    }
}
