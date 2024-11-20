//
// Copyright (c) 2010-2025 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
// Copyright (c) 2020-2021 Microsoft
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Utilities.Binding;
using Antmicro.Renode.Logging;
using Antmicro.Migrant.Hooks;
using Antmicro.Renode.Exceptions;
using ELFSharp.ELF;
using ELFSharp.UImage;
using Machine = Antmicro.Renode.Core.Machine;

namespace Antmicro.Renode.Peripherals.CPU
{
    public partial class CortexM : Arm
    {
        public CortexM(string cpuType, IMachine machine, NVIC nvic, [NameAlias("id")] uint cpuId = 0, Endianess endianness = Endianess.LittleEndian,
            uint? fpuInterruptNumber = null, uint? numberOfMPURegions = null, uint? numberOfSAURegions = null, bool enableTrustZone = false)
            : base(cpuType, machine, cpuId, endianness, numberOfMPURegions)
        {
            if(nvic == null)
            {
                throw new RecoverableException(new ArgumentNullException("nvic"));
            }

            tlibSetFpuInterruptNumber((int?)fpuInterruptNumber ?? -1);

            if(!numberOfMPURegions.HasValue)
            {
                // FIXME: This is not correct for M7 and v8M cores
                // Setting 8 regions backward-compatibility for now
                this.NumberOfMPURegions = 8;
            }

            TrustZoneEnabled = enableTrustZone;
            if(TrustZoneEnabled)
            {
                // Set CPU to start in Secure State
                // this also enables TrustZone in the translation library
                tlibSetSecurityState(1u);
            }
            if(!numberOfSAURegions.HasValue && TrustZoneEnabled)
            {
                // TODO: Determine default number
                this.NumberOfSAURegions = 8;
                this.Log(LogLevel.Info, "Configuring Security Attribution Unit regions to default: {0}", this.NumberOfSAURegions);
            }
            else if(numberOfSAURegions.HasValue)
            {
                NumberOfSAURegions = numberOfSAURegions.Value;
            }

            this.nvic = nvic;
            try
            {
                nvic.AttachCPU(this);
            }
            catch(RecoverableException e)
            {
                // Rethrow attachment error as ConstructionException, so the CreationDriver doesn't crash
                throw new ConstructionException("Exception occurred when attaching NVIC: ", e);
            }
        }

        public override void Reset()
        {
            pcNotInitialized = true;
            vtorInitialized = false;
            base.Reset();
        }

        public void SetSleepOnExceptionExit(bool value)
        {
            tlibSetSleepOnExceptionExit(value ? 1 : 0);
        }

        protected override void OnResume()
        {
            // Suppress initialization when processor is turned off as binary may not even be loaded yet
            if(!IsHalted)
            {
                InitPCAndSP();
            }
            base.OnResume();
        }

        public override string Architecture { get { return "arm-m"; } }

        public override List<GDBFeatureDescriptor> GDBFeatures
        {
            get
            {
                var features = new List<GDBFeatureDescriptor>();

                var mProfileFeature = new GDBFeatureDescriptor("org.gnu.gdb.arm.m-profile");
                for(var index = 0u; index <= 12; index++)
                {
                    mProfileFeature.Registers.Add(new GDBRegisterDescriptor(index, 32, $"r{index}", "uint32", "general"));
                }
                mProfileFeature.Registers.Add(new GDBRegisterDescriptor(13, 32, "sp", "data_ptr", "general"));
                mProfileFeature.Registers.Add(new GDBRegisterDescriptor(14, 32, "lr", "uint32", "general"));
                mProfileFeature.Registers.Add(new GDBRegisterDescriptor(15, 32, "pc", "code_ptr", "general"));
                mProfileFeature.Registers.Add(new GDBRegisterDescriptor(25, 32, "xpsr", "uint32", "general"));
                features.Add(mProfileFeature);

                var mSystemFeature = new GDBFeatureDescriptor("org.gnu.gdb.arm.m-system");
                mSystemFeature.Registers.Add(new GDBRegisterDescriptor(26, 32, "msp", "uint32", "general"));
                mSystemFeature.Registers.Add(new GDBRegisterDescriptor(27, 32, "psp", "uint32", "general"));
                mSystemFeature.Registers.Add(new GDBRegisterDescriptor(28, 32, "primask", "uint32", "general"));
                mSystemFeature.Registers.Add(new GDBRegisterDescriptor(29, 32, "basepri", "uint32", "general"));
                mSystemFeature.Registers.Add(new GDBRegisterDescriptor(30, 32, "faultmask", "uint32", "general"));
                mSystemFeature.Registers.Add(new GDBRegisterDescriptor(31, 32, "control", "uint32", "general"));
                features.Add(mSystemFeature);

                return features;
            }
        }

        public override MemorySystemArchitectureType MemorySystemArchitecture => NumberOfMPURegions > 0 ? MemorySystemArchitectureType.Physical_PMSA : MemorySystemArchitectureType.None;

        public override uint ExceptionVectorAddress
        {
            get => VectorTableOffset;
            set => VectorTableOffset = value;
        }

        // Sets VTOR for the current Security State the CPU is in right now
        public uint VectorTableOffset
        {
            get
            {
                var secure = 0u;
                if(TrustZoneEnabled)
                {
                    secure = SecureState ? 1u : 0u;
                }
                return tlibGetInterruptVectorBase(secure);
            }
            set
            {
                var secure = 0u;
                if(TrustZoneEnabled)
                {
                    secure = SecureState ? 1u : 0u;
                }
                vtorInitialized = true;
                if(!machine.SystemBus.IsMemory(value, this))
                {
                    this.Log(LogLevel.Warning, "Tried to set VTOR address at 0x{0:X} which does not lay in memory. Aborted.", value);
                    return;
                }
                this.NoisyLog("VectorTableOffset set to 0x{0:X}.", value);
                tlibSetInterruptVectorBase(value, secure);
            }
        }

        // NS alias for VTOR
        public uint VectorTableOffsetNonSecure
        {
            get
            {
                if(!TrustZoneEnabled)
                {
                    throw new RecoverableException("You need to enable TrustZone to use VTOR_NS");
                }
                return tlibGetInterruptVectorBase(0u);
            }
            set
            {
                if(!TrustZoneEnabled)
                {
                    throw new RecoverableException("You need to enable TrustZone to use VTOR_NS");
                }
                vtorInitialized = true;
                if(machine.SystemBus.FindMemory(value, this) == null)
                {
                    this.Log(LogLevel.Warning, "Tried to set VTOR_NS address at 0x{0:X} which does not lay in memory. Aborted.", value);
                    return;
                }
                this.NoisyLog("VectorTableOffset_NS set to 0x{0:X}.", value);
                tlibSetInterruptVectorBase(value, 0u);
            }
        }

        public uint NumberOfSAURegions
        {
            get => GetTrustZoneRelatedRegister(nameof(NumberOfSAURegions), () => tlibGetNumberOfSauRegions());
            set => SetTrustZoneRelatedRegister(nameof(NumberOfSAURegions), val => tlibSetNumberOfSauRegions(val), value);
        }

        public bool SecureState
        {
            get
            {
                if(!TrustZoneEnabled)
                {
                    throw new RecoverableException("You need to enable TrustZone to use this feature");
                }
                return tlibGetSecurityState() > 0;
            }
            set
            {
                if(!TrustZoneEnabled)
                {
                    throw new RecoverableException("You need to enable TrustZone to use this feature");
                }
                tlibSetSecurityState(value ? 1u : 0u);
            }
        }

        public bool FpuEnabled
        {
            set
            {
                tlibToggleFpu(value ? 1 : 0);
            }
        }

        public bool TrustZoneEnabled { get; }

        public UInt32 FaultStatus
        {
            set
            {
                tlibSetFaultStatus(value);
            }
            get
            {
                return tlibGetFaultStatus();
            }
        }

        public UInt32 MemoryFaultAddress
        {
            get
            {
                return tlibGetMemoryFaultAddress();
            }
        }

        public bool IsV8
        {
            get
            {
                return tlibIsV8() > 0;
            }
        }

        public UInt32 PmsaV8Ctrl
        {
            get
            {
                return tlibGetPmsav8Ctrl();
            }
            set
            {
                tlibSetPmsav8Ctrl(value);
            }
        }

        public UInt32 PmsaV8Rnr
        {
            get
            {
                return tlibGetPmsav8Rnr();
            }
            set
            {
                tlibSetPmsav8Rnr(value);
            }
        }

        public UInt32 PmsaV8Rbar
        {
            get
            {
                return tlibGetPmsav8Rbar();
            }
            set
            {
                tlibSetPmsav8Rbar(value);
            }
        }

        public UInt32 PmsaV8Rlar
        {
            get
            {
                return tlibGetPmsav8Rlar();
            }
            set
            {
                tlibSetPmsav8Rlar(value);
            }
        }

        public UInt32 PmsaV8Mair0
        {
            get
            {
                return tlibGetPmsav8Mair(0);
            }
            set
            {
                tlibSetPmsav8Mair(0, value);
            }
        }

        public UInt32 PmsaV8Mair1
        {
            get
            {
                return tlibGetPmsav8Mair(1);
            }
            set
            {
                tlibSetPmsav8Mair(1, value);
            }
        }

        public bool MPUEnabled
        {
            get
            {
                return tlibIsMpuEnabled() != 0;
            }
            set
            {
                tlibEnableMpu(value ? 1 : 0);
            }
        }

        public UInt32 MPURegionBaseAddress
        {
            set
            {
                tlibSetMpuRegionBaseAddress(value);
            }
            get
            {
                return tlibGetMpuRegionBaseAddress();
            }
        }

        public UInt32 MPURegionAttributeAndSize
        {
            set
            {
                tlibSetMpuRegionSizeAndEnable(value);
            }
            get
            {
                return tlibGetMpuRegionSizeAndEnable();
            }
        }

        public UInt32 MPURegionNumber
        {
            set
            {
                tlibSetMpuRegionNumber(value);
            }
            get
            {
                return tlibGetMpuRegionNumber();
            }
        }

        public uint SAUControl
        {
            get => GetTrustZoneRelatedRegister(nameof(SAUControl), () => tlibGetSauControl());
            set => SetTrustZoneRelatedRegister(nameof(SAUControl), val => tlibSetSauControl(val), value);
        }

        public uint SAURegionNumber
        {
            get => GetTrustZoneRelatedRegister(nameof(SAURegionNumber), () => tlibGetSauRegionNumber());
            set => SetTrustZoneRelatedRegister(nameof(SAURegionNumber), val => tlibSetSauRegionNumber(val), value);
        }

        public uint SAURegionBaseAddress
        {
            get => GetTrustZoneRelatedRegister(nameof(SAURegionBaseAddress), () => tlibGetSauRegionBaseAddress());
            set => SetTrustZoneRelatedRegister(nameof(SAURegionBaseAddress), val => tlibSetSauRegionBaseAddress(val), value);
        }

        public uint SAURegionLimitAddress
        {
            get => GetTrustZoneRelatedRegister(nameof(SAURegionLimitAddress), () => tlibGetSauRegionLimitAddress());
            set => SetTrustZoneRelatedRegister(nameof(SAURegionLimitAddress), val => tlibSetSauRegionLimitAddress(val), value);
        }

        public uint XProgramStatusRegister
        {
            get
            {
                return tlibGetXpsr();
            }
        }

        public override void InitFromElf(IELF elf)
        {
            // do nothing
        }

        public override void InitFromUImage(UImage uImage)
        {
            // do nothing
        }

        protected override UInt32 BeforePCWrite(UInt32 value)
        {
            if(value % 2 == 0)
            {
                this.Log(LogLevel.Warning, "Patching PC 0x{0:X} for Thumb mode.", value);
                value += 1;
            }
            pcNotInitialized = false;
            return base.BeforePCWrite(value);
        }

        protected override void OnLeavingResetState()
        {
            if(State == CPUState.Running)
            {
                InitPCAndSP();
            }
            base.OnLeavingResetState();
        }

        private uint GetTrustZoneRelatedRegister(string registerName, Func<uint> getter)
        {
            if(!TrustZoneEnabled)
            {
                this.Log(LogLevel.Warning, "Tried to read from {0} in CPU without TrustZone implemented, returning 0x0", registerName);
                return 0x0;
            }
            return getter();
        }

        private void InitPCAndSP()
        {
            var firstNotNullSection = machine.SystemBus.GetLookup(this).FirstNotNullSectionAddress;
            if(!vtorInitialized && firstNotNullSection.HasValue)
            {
                if((firstNotNullSection.Value & (2 << 6 - 1)) > 0)
                {
                    this.Log(LogLevel.Warning, "Alignment of VectorTableOffset register is not correct.");
                }
                else
                {
                    var value = firstNotNullSection.Value;
                    this.Log(LogLevel.Info, "Guessing VectorTableOffset value to be 0x{0:X}.", value);
                    if(value > uint.MaxValue)
                    {
                        this.Log(LogLevel.Error, "Guessed VectorTableOffset doesn't fit in 32-bit address space: 0x{0:X}.", value);
                        return; // Keep VectorTableOffset uninitialized in the case of error condition
                    }
                    VectorTableOffset = checked((uint)value);
                }
            }
            if(pcNotInitialized)
            {
                // stack pointer and program counter are being sent according
                // to VTOR (vector table offset register)
                var sysbus = machine.SystemBus;
                var pc = sysbus.ReadDoubleWord(VectorTableOffset + 4, this);
                var sp = sysbus.ReadDoubleWord(VectorTableOffset, this);
                if(!sysbus.IsMemory(pc, this) || (pc == 0 && sp == 0))
                {
                    this.Log(LogLevel.Error, "PC does not lay in memory or PC and SP are equal to zero. CPU was halted.");
                    IsHalted = true;
                    return; // Keep PC and SP uninitialized in the case of error condition
                }
                this.Log(LogLevel.Info, "Setting initial values: PC = 0x{0:X}, SP = 0x{1:X}.", pc, sp);
                PC = pc;
                SP = sp;
            }
        }

        private void SetTrustZoneRelatedRegister(string registerName, Action<uint> setter, uint value)
        {
            if(!TrustZoneEnabled)
            {
                this.Log(LogLevel.Warning, "Tried to write to {0} (value: 0x{1:X}) in CPU without TrustZone implemented, write ignored", registerName, value);
                return;
            }
            setter(value);
        }

        [Export]
        private uint HasEnabledTrustZone()
        {
            return TrustZoneEnabled ? 1u : 0u;
        }

        [Export]
        private void SetPendingIRQ(int number)
        {
            nvic.SetPendingIRQ(number);
        }

        [Export]
        private int AcknowledgeIRQ()
        {
            var result = nvic.AcknowledgeIRQ();
            return result;
        }

        [Export]
        private void CompleteIRQ(int number)
        {
            nvic.CompleteIRQ(number);
        }

        [Export]
        private void OnBASEPRIWrite(int value)
        {
            nvic.BASEPRI = (byte)value;
        }

        [Export]
        private int FindPendingIRQ()
        {
            return nvic != null ? nvic.FindPendingInterrupt() : -1;
        }

        [Export]
        private int PendingMaskedIRQ()
        {
            return nvic.MaskedInterruptPresent ? 1 : 0;
        }

        [Export]
        private uint InterruptTargetsSecure(int interruptNumber)
        {
            return nvic.GetTargetInterruptSecurityState(interruptNumber) == NVIC.InterruptTargetSecurityState.Secure ? 1u : 0u;
        }

        private NVIC nvic;
        private bool pcNotInitialized = true;
        private bool vtorInitialized;

        // 649:  Field '...' is never assigned to, and will always have its default value null
        #pragma warning disable 649

        [Import]
        private Action<int> tlibToggleFpu;

        [Import]
        private Func<uint> tlibGetFaultStatus;

        [Import]
        private Action<uint> tlibSetFaultStatus;

        [Import]
        private Func<uint> tlibGetMemoryFaultAddress;

        [Import]
        private Action<int> tlibEnableMpu;

        [Import]
        private Func<int> tlibIsMpuEnabled;

        [Import]
        private Action<uint> tlibSetMpuRegionBaseAddress;

        [Import]
        private Func<uint> tlibGetMpuRegionBaseAddress;

        [Import]
        private Action<uint> tlibSetMpuRegionSizeAndEnable;

        [Import]
        private Func<uint> tlibGetMpuRegionSizeAndEnable;

        [Import]
        private Action<uint> tlibSetMpuRegionNumber;

        [Import]
        private Func<uint> tlibGetMpuRegionNumber;

        [Import]
        private Action<int> tlibSetFpuInterruptNumber;

        [Import]
        private Func<uint, uint> tlibGetInterruptVectorBase;

        [Import]
        private Action<uint, uint> tlibSetInterruptVectorBase;

        [Import]
        private Func<uint> tlibGetXpsr;

        [Import]
        private Func<uint> tlibIsV8;

        [Import]
        private Action<int> tlibSetSleepOnExceptionExit;

        /* TrustZone */
        [Import]
        private Action<uint> tlibSetSecurityState;

        [Import]
        private Func<uint> tlibGetSecurityState;

        /* TrustZone SAU */
        [Import]
        private Action<uint> tlibSetNumberOfSauRegions;

        [Import]
        private Func<uint> tlibGetNumberOfSauRegions;

        [Import]
        private Action<uint> tlibSetSauControl;

        [Import]
        private Func<uint> tlibGetSauControl;

        [Import]
        private Action<uint> tlibSetSauRegionNumber;

        [Import]
        private Func<uint> tlibGetSauRegionNumber;

        [Import]
        private Action<uint> tlibSetSauRegionBaseAddress;

        [Import]
        private Func<uint> tlibGetSauRegionBaseAddress;

        [Import]
        private Action<uint> tlibSetSauRegionLimitAddress;

        [Import]
        private Func<uint> tlibGetSauRegionLimitAddress;

        /* PMSAv8 MPU */
        [Import]
        private Action<uint> tlibSetPmsav8Ctrl;

        [Import]
        private Action<uint> tlibSetPmsav8Rnr;

        [Import]
        private Action<uint> tlibSetPmsav8Rbar;

        [Import]
        private Action<uint> tlibSetPmsav8Rlar;

        [Import]
        private Action<uint, uint> tlibSetPmsav8Mair;

        [Import]
        private Func<uint> tlibGetPmsav8Ctrl;

        [Import]
        private Func<uint> tlibGetPmsav8Rnr;

        [Import]
        private Func<uint> tlibGetPmsav8Rbar;

        [Import]
        private Func<uint> tlibGetPmsav8Rlar;

        [Import]
        private Func<uint, uint> tlibGetPmsav8Mair;

        #pragma warning restore 649
    }
}

