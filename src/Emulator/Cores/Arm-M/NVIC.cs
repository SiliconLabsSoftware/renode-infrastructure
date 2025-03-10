//
// Copyright (c) 2010-2024 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
// Copyright (c) 2020-2021 Microsoft
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class NVIC : IDoubleWordPeripheral, IHasDivisibleFrequency, IKnownSize, IIRQController
    {
        public NVIC(IMachine machine, long systickFrequency = 50 * 0x800000, byte priorityMask = 0xFF, bool haltSystickOnDeepSleep = true)
        {
            priorities = new byte[IRQCount];
            activeIRQs = new Stack<int>();
            pendingIRQs = new SortedSet<int>();
            // We set initial limit to the maximum 24-bit value, and don't modify it afterwards.
            // Instead we reload counter with RELOAD value when counter reaches 0.
            systick = new LimitTimer(machine.ClockSource, systickFrequency, this, nameof(systick), SysTickMaxValue, Direction.Descending, false, eventEnabled: true);
            this.machine = machine;
            this.priorityMask = priorityMask;
            defaultHaltSystickOnDeepSleep = haltSystickOnDeepSleep;
            irqs = new IRQState[IRQCount];
            IRQ = new GPIO();
            resetMachine = machine.RequestReset;
            systick.LimitReached += () =>
            {
                countFlag.Value = true;
                if(eventEnabled.Value)
                {
                    SetPendingIRQ((int)SystemException.SysTick);
                }

                // If the systick timer is running and the reload value is 0, this has the effect of disabling the counter on the expiration.
                if(systickReload.Value == 0)
                {
                    systick.Enabled = false;
                }
                else
                {
                    systick.Value = systickReload.Value;
                }
            };
            RegisterCollection = new DoubleWordRegisterCollection(this);
            DefineRegisters();
            Reset();
        }

        public void AttachCPU(CortexM cpu)
        {
            if(this.cpu != null)
            {
                throw new RecoverableException("The NVIC has already attached CPU.");
            }
            this.cpu = cpu;
            this.cpuId = cpu.ModelID;
            mpuVersion = cpu.IsV8 ? MPUVersion.PMSAv8 : MPUVersion.PMSAv7;

            if(cpu.Model == "cortex-m7")
            {
                DefineTightlyCoupledMemoryControlRegisters();
            }

            cpu.AddHookAtWfiStateChange(HandleWfiStateChange);
        }

        public bool MaskedInterruptPresent { get { return maskedInterruptPresent; } }

        public IEnumerable<int> GetEnabledExternalInterrupts()
        {
            return irqs.Skip(16).Select((x,i) => new {x,i}).Where(y => (y.x & IRQState.Enabled) != 0).Select(y => y.i).OrderBy(x => x);
        }

        public IEnumerable<int> GetEnabledInternalInterrupts()
        {
            return irqs.Take(16).Select((x,i) => new {x,i}).Where(y => (y.x & IRQState.Enabled) != 0).Select(y => y.i).OrderBy(x => x);
        }

        public long Frequency
        {
            get
            {
                return systick.Frequency;
            }
            set
            {
                systick.Frequency = value;
            }
        }

        public int Divider
        {
            get => systick.Divider;
            set
            {
                systick.Divider = value;
            }
        }

        public bool HaltSystickOnDeepSleep { get; set; }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= PriorityStart && offset < PriorityEnd)
            {
                return HandlePriorityRead(offset - PriorityStart, true);
            }
            if(offset >= SetEnableStart && offset < SetEnableEnd)
            {
                return HandleEnableRead(offset - SetEnableStart);
            }
            if(offset >= ClearEnableStart && offset < ClearEnableEnd)
            {
                return HandleEnableRead(offset - ClearEnableStart);
            }
            if(offset >= SetPendingStart && offset < SetPendingEnd)
            {
                return GetPending((int)(offset - SetPendingStart));
            }
            if(offset >= ClearPendingStart && offset < ClearPendingEnd)
            {
                return GetPending((int)(offset - ClearPendingStart));
            }
            if(offset >= ActiveBitStart && offset < ActiveBitEnd)
            {
                return GetActive((int)(offset - ActiveBitStart));
            }
            if(offset >= MPUStart && offset < MPUEnd)
            {
                return HandleMPURead(offset - MPUStart);
            }
            switch((Registers)offset)
            {
            case Registers.VectorTableOffset:
                return cpu.VectorTableOffset;
            case Registers.CPUID:
                return cpuId;
            case Registers.CoprocessorAccessControl:
                return cpu.CPACR;
            case Registers.FPContextControl:
                if(!IsPrivilegedMode())
                {
                    this.Log(LogLevel.Warning, "Tried to read FPContextControl from an unprivileged context. Returning 0.");
                    return 0;
                }
                return cpu.FPCCR;
            case Registers.FPContextAddress:
                if(!IsPrivilegedMode())
                {
                    this.Log(LogLevel.Warning, "Tried to read FPContextAddress from an unprivileged context. Returning 0.");
                    return 0;
                }
                return cpu.FPCAR;
            case Registers.FPDefaultStatusControl:
                if(!IsPrivilegedMode())
                {
                    this.Log(LogLevel.Warning, "Tried to read FPDefaultStatusControl from an unprivileged context. Returning 0.");
                    return 0;
                }
                return cpu.FPDSCR;
            case Registers.ConfigurationAndControl:
                return ccr;
            case Registers.SystemHandlerPriority1:
            case Registers.SystemHandlerPriority2:
            case Registers.SystemHandlerPriority3:
                return HandlePriorityRead(offset - 0xD14, false);
            case Registers.ApplicationInterruptAndReset:
                return HandleApplicationInterruptAndResetRead();
            case Registers.ConfigurableFaultStatus:
                return cpu.FaultStatus;
            case Registers.InterruptControllerType:
                return 0b0111;
            case Registers.MemoryFaultAddress:
                return cpu.MemoryFaultAddress;
            default:
                return RegisterCollection.Read(offset);
            }
        }

        public GPIO IRQ { get; private set; }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= SetEnableStart && offset < SetEnableEnd)
            {
                EnableOrDisableInterrupt((int)offset - SetEnableStart, value, true);
                return;
            }
            if(offset >= PriorityStart && offset < PriorityEnd)
            {
                HandlePriorityWrite(offset - PriorityStart, true, value);
                return;
            }
            if(offset >= ClearEnableStart && offset < ClearEnableEnd)
            {
                EnableOrDisableInterrupt((int)offset - ClearEnableStart, value, false);
                return;
            }
            if(offset >= ClearPendingStart && offset < ClearPendingEnd)
            {
                SetOrClearPendingInterrupt((int)offset - ClearPendingStart, value, false);
                return;
            }
            if(offset >= SetPendingStart && offset < SetPendingEnd)
            {
                SetOrClearPendingInterrupt((int)offset - SetPendingStart, value, true);
                return;
            }
            if(offset >= MPUStart && offset < MPUEnd)
            {
                HandleMPUWrite(offset - MPUStart, value);
                return;
            }
            switch((Registers)offset)
            {
            case Registers.VectorTableOffset:
                cpu.VectorTableOffset = value & 0xFFFFFF80;
                break;
            case Registers.ApplicationInterruptAndReset:
                var key = value >> 16;
                if(key != VectKey)
                {
                    this.DebugLog("Wrong key while accessing Application Interrupt and Reset Control register 0x{0:X}.", key);
                    break;
                }
                binaryPointPosition = (int)(value >> 8) & 7;
                if(BitHelper.IsBitSet(value, 2))
                {
                    resetMachine();
                }
                break;
            case Registers.ConfigurableFaultStatus:
                cpu.FaultStatus &= ~value;
                break;
            case Registers.SystemHandlerPriority1:
                // 7th interrupt is ignored
                priorities[4] = (byte)value;
                priorities[5] = (byte)(value >> 8);
                priorities[6] = (byte)(value >> 16);
                this.DebugLog("Priority of IRQs 4, 5, 6 set to 0x{0:X}, 0x{1:X}, 0x{2:X} respectively.", (byte)value, (byte)(value >> 8), (byte)(value >> 16));
                break;
            case Registers.SystemHandlerPriority2:
                // only 11th is not ignored
                priorities[11] = (byte)(value >> 24);
                this.DebugLog("Priority of IRQ 11 set to 0x{0:X}.", (byte)(value >> 24));
                break;
            case Registers.SystemHandlerPriority3:
                priorities[(int)SystemException.PendSV] = (byte)(value >> 16);
                priorities[(int)SystemException.SysTick] = (byte)(value >> 24);
                this.DebugLog("Priority of IRQs 14, 15 set to 0x{0:X}, 0x{1:X} respectively.", (byte)(value >> 16), (byte)(value >> 24));
                break;
            case Registers.CoprocessorAccessControl:
                // for ARM v8 and CP10 values:
                //      0b11 Full access to the FP Extension and MVE
                //      0b01 Privileged access only to the FP Extension and MVE
                //      0b00 No access to the FP Extension and MVE
                //      0b10 Reserved
                // Any attempted use without access generates a NOCP UsageFault.
                // same for ARM v7, but if values of CP11 and CP10 differ then effects are unpredictable
                cpu.CPACR = value;
                // Enable FPU if any access is permitted, privilege checks in tlib use CPACR register.
                if((value & 0x100000) == 0x100000)
                {
                    this.DebugLog("Enabling FPU.");
                    cpu.FpuEnabled = true;
                }
                else
                {
                    this.DebugLog("Disabling FPU.");
                    cpu.FpuEnabled = false;
                }
                break;
            case Registers.SoftwareTriggerInterrupt:
                // This register is implemented only in ARMv7m and ARMv8m
                if(cpu.Model == "cortex-m3" || cpu.Model == "cortex-m4" || cpu.Model == "cortex-m4f" || cpu.Model == "cortex-m7")
                {
                    SetPendingIRQ((int)(16 + value));
                }
                else
                {
                    this.Log(LogLevel.Error, "Software Trigger Interrupt Register not implemented for {0}", cpu.Model);
                }
                break;
            case Registers.FPContextControl:
                if(!IsPrivilegedMode())
                {
                    this.Log(LogLevel.Warning, "Writing to FPContextControl requires privileged access.");
                    break;
                }
                cpu.FPCCR = value;
                break;
            case Registers.FPContextAddress:
                if(!IsPrivilegedMode())
                {
                    this.Log(LogLevel.Warning, "Writing to FPContextAddress requires privileged access.");
                    break;
                }
                // address must be 8-byte aligned
                cpu.FPCAR = value & ~0x3u;
                break;
            case Registers.FPDefaultStatusControl:
                if(!IsPrivilegedMode())
                {
                    this.Log(LogLevel.Warning, "Writing to FPDefaultStatusControl requires privileged access.");
                    break;
                }
                // set only not reserved values
                cpu.FPDSCR = value & 0x07c00000;
                break;
            case Registers.ConfigurationAndControl:
                ccr = value;
                break;
            default:
                RegisterCollection.Write(offset, value);
                break;
            }
        }

        public void Reset()
        {
            RegisterCollection.Reset();
            InitInterrupts();
            for(var i = 0; i < priorities.Length; i++)
            {
                priorities[i] = 0x00;
            }
            activeIRQs.Clear();
            systick.Reset();
            IRQ.Unset();
            mpuControlRegister = 0;
            HaltSystickOnDeepSleep = defaultHaltSystickOnDeepSleep;

            // bit [16] DC / Cache enable. This is a global enable bit for data and unified caches.
            ccr = 0x10000;
        }

        public long Size
        {
            get
            {
                return 0x1000;
            }
        }

        public int AcknowledgeIRQ()
        {
            lock(irqs)
            {
                var result = FindPendingInterrupt();
                if(result != SpuriousInterrupt)
                {
                    irqs[result] |= IRQState.Active;
                    irqs[result] &= ~IRQState.Pending;
                    pendingIRQs.Remove(result);
                    this.NoisyLog("Acknowledged IRQ {0}.", result);
                    activeIRQs.Push(result);
                }
                // at this point we can surely deactivate interrupt, because the best was chosen
                IRQ.Set(false);
                return result;
            }
        }

        public void CompleteIRQ(int number)
        {
            lock(irqs)
            {
                var currentIRQ = irqs[number];
                if((currentIRQ & IRQState.Active) == 0)
                {
                    this.Log(LogLevel.Error, "Trying to complete not active IRQ {0}.", number);
                    return;
                }
                irqs[number] &= ~IRQState.Active;
                var activeIRQ = activeIRQs.Pop();
                if(activeIRQ != number)
                {
                    this.Log(LogLevel.Error, "Trying to complete IRQ {0} that was not the last active. Last active was {1}.", number, activeIRQ);
                    return;
                }
                if((currentIRQ & IRQState.Running) > 0)
                {
                    this.NoisyLog("Completed IRQ {0} active -> pending.", number);
                    irqs[number] |= IRQState.Pending;
                    pendingIRQs.Add(number);
                }
                else if((currentIRQ & IRQState.Pending) != 0)
                {
                    this.NoisyLog("Completed IRQ {0} active -> pending.", number);
                }
                else
                {
                    this.NoisyLog("Completed IRQ {0} active -> inactive.", number);
                }
                FindPendingInterrupt();
            }
        }

        public void SetPendingIRQ(int number)
        {
            lock(irqs)
            {
                this.NoisyLog("Internal IRQ {0}.", number);
                SetPending(number);
                FindPendingInterrupt();
            }
        }

        public void OnGPIO(int number, bool value)
        {
            number += 16; // because this is HW interrupt
            this.NoisyLog("External IRQ {0}: {1}", number, value);
            var pendingInterrupt = SpuriousInterrupt;
            lock(irqs)
            {
                if(value)
                {
                    irqs[number] |= IRQState.Running;
                    SetPending(number);
                }
                else
                {
                    irqs[number] &= ~IRQState.Running;
                }
                pendingInterrupt = FindPendingInterrupt();
            }
            if(pendingInterrupt != SpuriousInterrupt && value)
            {
                if(systickEnabled.Value && systick.Enabled == false)
                {
                    this.NoisyLog("Waking up from deep sleep");
                }
                systick.Enabled |= value && systickEnabled.Value && systickReload.Value != 0;
            }
        }

        public void SetSevOnPendingOnAllCPUs(bool value)
        {
            foreach(var cpu in machine.SystemBus.GetCPUs().OfType<Arm>())
            {
                cpu.SetSevOnPending(value);
            }
        }

        public void SetSleepOnExceptionExitOnAllCPUs(bool value)
        {
            foreach(var cpu in machine.SystemBus.GetCPUs().OfType<CortexM>())
            {
                cpu.SetSleepOnExceptionExit(value);
            }
        }

        public DoubleWordRegisterCollection RegisterCollection { get; }

        public bool DeepSleepEnabled => deepSleepEnabled.Value;

        private void DefineRegisters()
        {
            Registers.SysTickControl.Define(RegisterCollection)
                .WithFlag(0, out systickEnabled, changeCallback: (_, value) =>
                {
                    if(value && systickReload.Value == 0)
                    {
                        this.DebugLog("Systick enabled but it won't be started as long as reload value is zero");
                        return;
                    }
                    this.NoisyLog("Systick {0}", value ? "enabled" : "disabled");
                    systick.Enabled = value;
                }, name: "ENABLE")
                .WithFlag(1, out eventEnabled, changeCallback: (_, newValue) =>
                {
                    this.NoisyLog("Systick interrupt {0}.", newValue ? "enabled" : "disabled");
                }, name: "TICKINT")
                // If no external clock is provided, this bit reads as 1 and ignores writes.
                .WithFlag(2, FieldMode.Read, valueProviderCallback: _ => true, name: "CLKSOURCE") // SysTick uses the processor clock
                .WithReservedBits(3, 13)
                .WithFlag(16, out countFlag, FieldMode.ReadToClear, name: "COUNTFLAG")
                .WithReservedBits(17, 15);

            Registers.SysTickReloadValue.Define(RegisterCollection)
                .WithValueField(0, 24, out systickReload, changeCallback: (oldValue, newValue) =>
                {
                    if(systickEnabled.Value && oldValue == 0 && !systick.Enabled)
                    {
                        // We explicitly enable underlying SysTick counter only in the case it was blocked by RELOAD=0.
                        // We ignore other cases, as we don't want to accidentally enable counter in DEEPSLEEP just by writing to this register.
                        this.DebugLog("Resuming systick counter due to reload value change from 0x0 to 0x{0:X}", newValue);
                        systick.Value = newValue;
                        systick.Enabled = true;
                    }
                }, name: "RELOAD")
                .WithReservedBits(24, 8);

            Registers.SysTickValue.Define(RegisterCollection)
                .WithValueField(0, 24, writeCallback: (_, __) =>
                {
                    if(systickReload.Value != 0)
                    {
                        // Write to this register does not trigger the SysTick exception logic - we can't write zero to timer value as it would trigger an event.
                        systick.Value = systickReload.Value;
                    }
                    countFlag.Value = false;
                },
                valueProviderCallback: _ =>
                {
                    cpu?.SyncTime();
                    return (uint)systick.Value;
                }, name: "CURRENT");

            Registers.SysTickCalibrationValue.Define(RegisterCollection)
                // Note that some reference manuals state that this value is for 1ms interval and not for 10ms
                .WithValueField(0, 24, FieldMode.Read, valueProviderCallback: _ => SysTickMaxValue & (uint)(systick.Frequency / SysTickCalibration100Hz), name: "TENMS")
                .WithReservedBits(24, 6)
                .WithFlag(30, FieldMode.Read, valueProviderCallback: _ => systick.Frequency % SysTickCalibration100Hz != 0, name: "SKEW")
                .WithFlag(31, FieldMode.Read, valueProviderCallback: _ => true, name: "NOREF");

            Registers.InterruptControlState.Define(RegisterCollection)
                .WithValueField(0, 9, FieldMode.Read, valueProviderCallback: _ => (uint)(activeIRQs.Count == 0 ? 0 : activeIRQs.Peek()), name: "VECTACTIVE")
                .WithReservedBits(9, 2)
                .WithTaggedFlag("RETTOBASE", 11)
                .WithValueField(12, 9, FieldMode.Read, valueProviderCallback: _ => (uint)FindPendingInterrupt(), name: "VECTPENDING")
                .WithReservedBits(21, 1)
                .WithTaggedFlag("ISRPENDING", 22)
                .WithTaggedFlag("ISRPREEMPT", 23)
                .WithTaggedFlag("STTNS", 24)
                .WithFlag(25, FieldMode.WriteOneToClear, writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        ClearPending((int)SystemException.SysTick);
                    }
                }, name: "PENDSTCLR")
                .WithFlag(26, writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        SetPendingIRQ((int)SystemException.SysTick);
                    }
                }, valueProviderCallback: _ => irqs[(int)SystemException.SysTick].HasFlag(IRQState.Pending), name: "PENDSTSET")
                .WithFlag(27, FieldMode.WriteOneToClear, writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        ClearPending((int)SystemException.PendSV);
                    }
                }, name: "PENDSVCLR")
                .WithFlag(28, writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        SetPendingIRQ((int)SystemException.PendSV);
                    }
                }, valueProviderCallback: _ => irqs[(int)SystemException.PendSV].HasFlag(IRQState.Pending), name: "PENDSVSET")
                .WithReservedBits(29, 1)
                .WithFlag(30, FieldMode.WriteOneToClear, writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        ClearPending((int)SystemException.NMI);
                    }
                }, name: "PENDNMICLR")
                .WithFlag(31, writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        SetPendingIRQ((int)SystemException.NMI);
                    }
                }, valueProviderCallback: _ => irqs[(int)SystemException.NMI].HasFlag(IRQState.Pending), name: "PENDNMISET");

            Registers.SystemControlRegister.Define(RegisterCollection)
                .WithReservedBits(0, 1)
                .WithFlag(1, out sleepOnExitEnabled, name: "SLEEPONEXIT",
                    changeCallback: (_, value) => SetSleepOnExceptionExitOnAllCPUs(value))
                .WithFlag(2, out deepSleepEnabled, name: "SLEEPDEEP")
                .WithReservedBits(3, 1)
                .WithFlag(4, out currentSevOnPending, name: "SEVONPEND",
                    changeCallback: (_, value) => SetSevOnPendingOnAllCPUs(value))
                .WithReservedBits(5, 27);

            Registers.SystemHandlerControlAndState.Define(RegisterCollection)
                .WithTaggedFlag("MEMFAULTACT (Memory Manage Active)", 0)
                .WithTaggedFlag("BUSFAULTACT (Bus Fault Active)", 1)
                .WithReservedBits(2, 1)
                .WithTaggedFlag("USGFAULTACT (Usage Fault Active)", 3)
                .WithReservedBits(4, 3)
                .WithTaggedFlag("SVCALLACT (SV Call Active)", 7)
                .WithTaggedFlag("MONITORACT (Monitor Active)", 8)
                .WithReservedBits(9, 1)
                .WithTaggedFlag("PENDSVACT (Pend SV Active)", 10)
                .WithTaggedFlag("SYSTICKACT (Sys Tick Active)", 11)
                .WithTaggedFlag("USGFAULTPENDED (Usage Fault Pending)", 12)
                .WithTaggedFlag("MEMFAULTPENDED (Mem Manage Pending)", 13)
                .WithTaggedFlag("BUSFAULTPENDED (Bus Fault Pending)", 14)
                .WithTaggedFlag("SVCALLPENDED (SV Call Pending)", 15)
                // The enable flags only store written data.
                // Changing them doesn't change a behavior of the model.
                .WithFlag(16, name: "MEMFAULTENA (Memory Manage Fault Enable)")
                .WithFlag(17, name: "BUSFAULTENA (Bus Fault Enable)")
                .WithFlag(18, name: "USGFAULTENA (Usage Fault Enable)")
                .WithReservedBits(19, 13)
                .WithChangeCallback((_, val) =>
                    this.Log(LogLevel.Warning, "Changing value of the SHCSR register to 0x{0:X}, the register isn't supported by Renode", val)
                );

            Registers.CacheSizeSelection.Define(RegisterCollection)
                .WithTaggedFlag("InD (Instruction or Data Selection)", 0)
                .WithTag("Level", 1, 3)
                .WithReservedBits(4, 28);

            Registers.CacheSizeID.Define(RegisterCollection)
                .WithTag("LineSize", 0, 3)
                .WithTag("Associativity", 3, 10)
                .WithTag("NumSets", 13, 15)
                .WithTaggedFlag("WA (Write Allocation Support)", 28)
                .WithTaggedFlag("RA (Read Allocation Support)", 29)
                .WithTaggedFlag("WB (Write Back Support)", 30)
                .WithTaggedFlag("WT (Write Through Support)", 31);

            Registers.DebugExceptionAndMonitorControlRegister.Define(RegisterCollection)
                .WithTaggedFlag("VC_CORERESET (Reset Vector Catch)", 0)
                .WithReservedBits(1, 3)
                .WithTaggedFlag("VC_MMERR (Debug trap on Memory Management faults)", 4)
                .WithTaggedFlag("VC_NOCPERR (Debug trap on Usage Fault access to Coprocessor which is not present)", 5)
                .WithTaggedFlag("VC_CHKERR (Debug trap on Usage Fault enabled checking errors)", 6)
                .WithTaggedFlag("VC_STATERR (Debug trap on Usage Fault state error)", 7)
                .WithTaggedFlag("VC_BUSERR (Debug trap on normal Bus error)", 8)
                .WithTaggedFlag("VC_INTERR (Debug trap on interrupt/exception service errors)", 9)
                .WithTaggedFlag("VC_HARDERR (Debug trap on Hard Fault)", 10)
                .WithReservedBits(11, 5)
                .WithTaggedFlag("MON_EN (Monitor Enable)", 16)
                .WithTaggedFlag("MON_PEND (Monitor Pend)", 17)
                .WithTaggedFlag("MON_STEP (Monitor Step)", 18)
                .WithTaggedFlag("MON_REQ (Monitor Request)", 19)
                .WithReservedBits(20, 4)
                // The trace flag only store written data.
                // Changing it doesn't change the behavior of the model.
                .WithFlag(24, name: "TRCENA (Trace Enable)")
                .WithReservedBits(25, 7);
        }

        private void DefineTightlyCoupledMemoryControlRegisters()
        {
            // The ITCMC and DTCMC registers have same fields.
            Registers.InstructionTightlyCoupledMemoryControl.DefineMany(RegisterCollection, 2, setup: (reg, index) => reg
                .WithTaggedFlag("EN (TCM Enable)", 0)
                .WithTaggedFlag("RMW (Read Modify Write Enable)", 1)
                .WithTaggedFlag("RETEN (Retry Phase Enable)", 2)
                .WithTag("SZ (TCM Size)", 3, 4)
                .WithReservedBits(7, 25)
            );
        }

        private void InitInterrupts()
        {
            Array.Clear(irqs, 0, irqs.Length);
            for(var i = 0; i < 16; i++)
            {
                irqs[i] = IRQState.Enabled;
            }
            maskedInterruptPresent = false;
            pendingIRQs.Clear();
        }

        private static int GetStartingInterrupt(long offset, bool externalInterrupt)
        {
            return (int)(offset + (externalInterrupt ? 16 : 0));
        }

        private void HandlePriorityWrite(long offset, bool externalInterrupt, uint value)
        {
            lock(irqs)
            {
                var startingInterrupt = GetStartingInterrupt(offset, externalInterrupt);
                for(var i = startingInterrupt; i < startingInterrupt + 4; i++)
                {

                    if((((byte)value) & ~priorityMask) != 0)
                    {
                        this.Log(LogLevel.Warning, "Trying to set the priority for interrupt {0} to 0x{1:X}, but it should be maskable with 0x{2:X}", i, value, priorityMask);
                    }

                    priorities[i] = (byte)(value & priorityMask);

                    this.DebugLog("Priority 0x{0:X} set for interrupt {1}.", priorities[i], i);
                    value >>= 8;
                }
            }
        }

        private uint HandlePriorityRead(long offset, bool externalInterrupt)
        {
            lock(irqs)
            {
                var returnValue = 0u;
                var startingInterrupt = GetStartingInterrupt(offset, externalInterrupt);
                for(var i = startingInterrupt + 3; i > startingInterrupt; i--)
                {
                    returnValue |= priorities[i];
                    returnValue <<= 8;
                }
                returnValue |= priorities[startingInterrupt];
                return returnValue;
            }
        }

        private uint HandleMPURead(long offset)
        {
            switch(mpuVersion)
            {
                case MPUVersion.PMSAv7:
                    return HandleMPUReadV7(offset);
                case MPUVersion.PMSAv8:
                    return HandleMPUReadV8(offset);
                default:
                    throw new Exception("Attempted MPU read, but the MPU version is unknown");
            }
        }

        private void HandleMPUWrite(long offset, uint value)
        {
            switch(mpuVersion)
            {
                case MPUVersion.PMSAv7:
                    HandleMPUWriteV7(offset, value);
                    break;
                case MPUVersion.PMSAv8:
                    HandleMPUWriteV8(offset, value);
                    break;
                default:
                    throw new Exception("Attempted MPU write, but the mpuVersion is unknown");
            }
        }

        private void HandleMPUWriteV7(long offset, uint value)
        {
            this.Log(LogLevel.Debug, "MPU: Trying to write to {0} (value: 0x{1:X08})", Enum.GetName(typeof(RegistersV7), offset), value);
            switch((RegistersV7)offset)
            {
                case RegistersV7.Type:
                    this.Log(LogLevel.Warning, "MPU: Trying to write to a read-only register (MPU_TYPE)");
                    break;
                case RegistersV7.Control:
                    if((mpuControlRegister & 0x1) != (value & 0x1))
                    {
                        this.cpu.MPUEnabled = (value & 0x1) != 0x0;
                    }
                    mpuControlRegister = value;
                    break;
                case RegistersV7.RegionNumber: // MPU_RNR
                    cpu.MPURegionNumber = value;
                    break;
                case RegistersV7.RegionBaseAddress:
                case RegistersV7.RegionBaseAddressAlias1:
                case RegistersV7.RegionBaseAddressAlias2:
                case RegistersV7.RegionBaseAddressAlias3:
                    cpu.MPURegionBaseAddress = value;
                    break;
                case RegistersV7.RegionAttributeAndSize:
                case RegistersV7.RegionAttributeAndSizeAlias1:
                case RegistersV7.RegionAttributeAndSizeAlias2:
                case RegistersV7.RegionAttributeAndSizeAlias3:
                    cpu.MPURegionAttributeAndSize = value;
                    break;
            }
        }

        private void HandleMPUWriteV8(long offset, uint value)
        {
            this.Log(LogLevel.Debug, "MPU: Trying to write to {0} (value: 0x{1:X08})", Enum.GetName(typeof(RegistersV8), offset), value);

            if (cpu.NumberOfMPURegions == 0)
            {
                this.Log(LogLevel.Error, $"CPU abort [PC={cpu.PC:x}]. Attempted a write to an MPU register, but the CPU doesn't support MPU. Set 'numberOfMPURegions' in CPU configuration to enable it.");
                throw new CpuAbortException();
            }
            switch((RegistersV8)offset)
            {
                case RegistersV8.Type:
                    this.Log(LogLevel.Warning, "MPU: Trying to write to a read-only register (MPU_TYPE)");
                    break;
                case RegistersV8.Control:
                    cpu.PmsaV8Ctrl = value;
                    break;
                case RegistersV8.RegionNumberRegister:
                    cpu.PmsaV8Rnr = value;
                    break;
                case RegistersV8.RegionBaseAddressRegister:
                case RegistersV8.RegionBaseAddressRegisterAlias1:
                case RegistersV8.RegionBaseAddressRegisterAlias2:
                case RegistersV8.RegionBaseAddressRegisterAlias3:
                    cpu.PmsaV8Rbar = value;
                    break;
                case RegistersV8.RegionLimitAddressRegister:
                case RegistersV8.RegionLimitAddressRegisterAlias1:
                case RegistersV8.RegionLimitAddressRegisterAlias2:
                case RegistersV8.RegionLimitAddressRegisterAlias3:
                    cpu.PmsaV8Rlar = value;
                    break;
                case RegistersV8.MemoryAttributeIndirectionRegister0:
                    cpu.PmsaV8Mair0 = value;
                    break;
                case RegistersV8.MemoryAttributeIndirectionRegister1:
                    cpu.PmsaV8Mair1 = value;
                    break;
            }
        }

        private uint HandleMPUReadV7(long offset)
        {
            uint value;
            switch((RegistersV7)offset)
            {
                case RegistersV7.Type:
                    value = (cpu.NumberOfMPURegions & 0xFF) << 8;
                    break;
                case RegistersV7.Control:
                    value = mpuControlRegister;
                    break;
                case RegistersV7.RegionNumber:
                    value = cpu.MPURegionNumber;
                    break;
                case RegistersV7.RegionBaseAddress:
                case RegistersV7.RegionBaseAddressAlias1:
                case RegistersV7.RegionBaseAddressAlias2:
                case RegistersV7.RegionBaseAddressAlias3:
                    value = cpu.MPURegionBaseAddress;
                    break;
                case RegistersV7.RegionAttributeAndSize:
                case RegistersV7.RegionAttributeAndSizeAlias1:
                case RegistersV7.RegionAttributeAndSizeAlias2:
                case RegistersV7.RegionAttributeAndSizeAlias3:
                    value = cpu.MPURegionAttributeAndSize;
                    break;
                default:
                    value = 0x0;
                    break;
            }
            this.Log(LogLevel.Debug, "MPU: Trying to read {0} (value: 0x{1:X08})", Enum.GetName(typeof(RegistersV7), offset), value);
            return value;
        }

        private uint HandleMPUReadV8(long offset)
        {
            if (cpu.NumberOfMPURegions == 0)
            {
                this.Log(LogLevel.Debug, $"Attempted a read from an MPU register, but the CPU doesn't support MPU. Set 'numberOfMPURegions' in CPU configuration to enable it.");
                return 0;
            }
            uint value;
            switch((RegistersV8)offset)
            {
                case RegistersV8.Type:
                    value = (cpu.NumberOfMPURegions & 0xFF) << 8;
                    break;
                case RegistersV8.Control:
                    value = cpu.PmsaV8Ctrl;
                    break;
                case RegistersV8.RegionNumberRegister:
                    value = cpu.PmsaV8Rnr;
                    break;
                case RegistersV8.RegionBaseAddressRegister:
                case RegistersV8.RegionBaseAddressRegisterAlias1:
                case RegistersV8.RegionBaseAddressRegisterAlias2:
                case RegistersV8.RegionBaseAddressRegisterAlias3:
                    value = cpu.PmsaV8Rbar;
                    break;
                case RegistersV8.RegionLimitAddressRegister:
                case RegistersV8.RegionLimitAddressRegisterAlias1:
                case RegistersV8.RegionLimitAddressRegisterAlias2:
                case RegistersV8.RegionLimitAddressRegisterAlias3:
                    value = cpu.PmsaV8Rlar;
                    break;
                case RegistersV8.MemoryAttributeIndirectionRegister0:
                    value = cpu.PmsaV8Mair0;
                    break;
                case RegistersV8.MemoryAttributeIndirectionRegister1:
                    value = cpu.PmsaV8Mair1;
                    break;
                default:
                    value = 0x0;
                    break;
            }
            this.Log(LogLevel.Debug, "MPU: Trying to read {0} (value: 0x{1:X08})", Enum.GetName(typeof(RegistersV7), offset), value);
            return value;
        }

        private uint HandleApplicationInterruptAndResetRead()
        {
            var returnValue = (uint)VectKeyStat << 16;
            returnValue |= ((uint)binaryPointPosition << 8);
            return returnValue;
        }

        private void HandleWfiStateChange(bool enteredWfi)
        {
            if(enteredWfi && deepSleepEnabled.Value && HaltSystickOnDeepSleep)
            {
                systick.Enabled = false;
                this.NoisyLog("Entering deep sleep");
            }
        }

        private void EnableOrDisableInterrupt(int offset, uint value, bool enable)
        {
            lock(irqs)
            {
                var firstIRQNo = 8 * offset + 16;  // 16 is added because this is HW interrupt
                {
                    var lastIRQNo = firstIRQNo + 31;
                    var mask = 1u;
                    for(var i = firstIRQNo; i <= lastIRQNo; i++)
                    {
                        if((value & mask) > 0)
                        {
                            if(enable)
                            {
                                this.NoisyLog("Enabled IRQ {0}.", i);
                                irqs[i] |= IRQState.Enabled;
                            }
                            else
                            {
                                this.NoisyLog("Disabled IRQ {0}.", i);
                                irqs[i] &= ~IRQState.Enabled;
                            }
                        }
                        mask <<= 1;
                    }
                    FindPendingInterrupt();
                }
            }
        }

        private uint HandleEnableRead(long offset)
        {
            lock(irqs)
            {
                var firstIRQNo = 8 * offset + 16;
                var lastIRQNo = firstIRQNo + 31;
                var result = 0u;
                if(firstIRQNo < 0 || lastIRQNo > irqs.Length)
                {
                    this.Log(LogLevel.Error, "Trying to access IRQs from range {0}-{1}, but only {2} are defined (offset 0x{3:X})", firstIRQNo, lastIRQNo, irqs.Length, offset);
                    return result;
                }
                for(var i = lastIRQNo; i > firstIRQNo; i--)
                {
                    result |= ((irqs[i] & IRQState.Enabled) != 0) ? 1u : 0u;
                    result <<= 1;
                }
                result |= ((irqs[firstIRQNo] & IRQState.Enabled) != 0) ? 1u : 0u;
                return result;
            }
        }

        private void SetPending(int i)
        {
            this.DebugLog("Set pending IRQ {0}.", i);
            var before = irqs[i];
            irqs[i] |= IRQState.Pending;
            pendingIRQs.Add(i);

            // when SEVONPEND is set all interrupts (even those masked)
            // generate an event when entering the pending state
            if(before != irqs[i] && currentSevOnPending.Value)
            {
                foreach(var cpu in machine.SystemBus.GetCPUs().OfType<CortexM>())
                {
                    cpu.SetEventFlag(true);
                }
            }
        }

        private void ClearPending(int i)
        {
            lock(irqs)
            {
                if((irqs[i] & IRQState.Running) == 0)
                {
                    this.DebugLog("Cleared pending IRQ {0}.", i);
                    irqs[i] &= ~IRQState.Pending;
                    pendingIRQs.Remove(i);
                }
                else
                {
                    this.DebugLog("Not clearing pending IRQ {0} as it is currently running.", i);
                }
            }
        }

        private void SetOrClearPendingInterrupt(int offset, uint value, bool set)
        {
            lock(irqs)
            {
                var firstIRQNo = 8 * offset + 16;  // 16 is added because this is HW interrupt
                {
                    var lastIRQNo = firstIRQNo + 31;
                    var mask = 1u;
                    for(var i = firstIRQNo; i <= lastIRQNo; i++)
                    {
                        if((value & mask) > 0)
                        {
                            if (set)
                            {
                                SetPending(i);
                            }
                            else
                            {
                                ClearPending(i);
                            }
                        }
                        mask <<= 1;
                    }
                    FindPendingInterrupt();
                }
            }
        }

        public int FindPendingInterrupt()
        {
            lock(irqs)
            {
                var bestPriority = 0xFF + 1;
                var preemptNeeded = activeIRQs.Count != 0;
                var result = SpuriousInterrupt; // TODO (and some log?)

                foreach(int i in pendingIRQs)
                {
                    var currentIRQ = irqs[i];
                    if(IsCandidate(currentIRQ, i) && priorities[i] < bestPriority)
                    {
                        result = i;
                        bestPriority = priorities[i];
                    }
                }
                if(preemptNeeded)
                {
                    var activePriority = (int)priorities[activeIRQs.Peek()];
                    if(!DoesAPreemptB(bestPriority, activePriority))
                    {
                        result = SpuriousInterrupt;
                    }
                    else
                    {
                        this.NoisyLog("IRQ {0} preempts {1}.", result, activeIRQs.Peek());
                    }
                }

                if(result != SpuriousInterrupt)
                {
                    if(result == NonMaskableInterruptIRQ || (cpu.PRIMASK == 0 && cpu.FAULTMASK == 0))
                    {
                        IRQ.Set(true);
                    }
                    // This field has side-effects, and can cause Cortex-M CPU running in another thread to exit WFI immediately.
                    // Make absolutely sure to execute last, after signaling IRQ handler to run with `IRQ.Set`.
                    // Only this way the CPU will enter an exception handler immediately upon waking from WFI.
                    // This doesn't matter for async (HW) interrupts, arriving when the core is executing normally.
                    maskedInterruptPresent = true;
                }
                else
                {
                    maskedInterruptPresent = false;
                }

                return result;
            }
        }

        private bool IsCandidate(IRQState state, int index)
        {
            const IRQState mask = IRQState.Pending | IRQState.Enabled | IRQState.Active;
            const IRQState candidate = IRQState.Pending | IRQState.Enabled;

            return ((state & mask) == candidate) &&
                   (basepri == 0 || priorities[index] < basepri);
        }

        private bool DoesAPreemptB(int priorityA, int priorityB)
        {
            var binaryPointMask = ~((1 << binaryPointPosition + 1) - 1);
            return (priorityA & binaryPointMask) < (priorityB & binaryPointMask);
        }

        private uint GetPending(int offset)
        {
            return BitHelper.GetValueFromBitsArray(irqs.Skip(16 + offset * 8).Take(32).Select(irq => (irq & IRQState.Pending) != 0));
        }

        private uint GetActive(int offset)
        {
            return BitHelper.GetValueFromBitsArray(irqs.Skip(16 + offset * 8).Take(32).Select(irq => (irq & IRQState.Active) != 0));
        }

        private bool IsPrivilegedMode()
        {
            // Is in handler mode or is privileged
            return (cpu.XProgramStatusRegister & InterruptProgramStatusRegisterMask) != 0 || (cpu.Control & 1) == 0;
        }

        private byte basepri;
        public byte BASEPRI
        {
            get { return basepri; }
            set
            {
                if(value == basepri)
                {
                    return;
                }
                basepri = value;
                FindPendingInterrupt();
            }
        }

        [Flags]
        private enum IRQState : byte
        {
            Running = 1,
            Pending = 2,
            Active = 4,
            Enabled = 32
        }

        private enum Registers
        {
            InterruptControllerType = 0x4,
            SysTickControl = 0x10,
            SysTickReloadValue = 0x14,
            SysTickValue = 0x18,
            SysTickCalibrationValue = 0x1C,
            SetEnable = 0x100,
            ClearEnable = 0x180,
            SetPending = 0x200,
            ClearPending = 0x280,
            ActiveBit = 0x300,
            InterruptPriority = 0x400,
            CPUID = 0xD00,
            InterruptControlState = 0xD04,
            VectorTableOffset = 0xD08,
            ApplicationInterruptAndReset = 0xD0C,
            SystemControlRegister = 0xD10, // SCR
            ConfigurationAndControl = 0xD14, // CCR
            SystemHandlerPriority1 = 0xD18, // SHPR1
            SystemHandlerPriority2 = 0xD1C, // SHPR2
            SystemHandlerPriority3 = 0xD20, // SHPR3
            SystemHandlerControlAndState = 0xD24, // SHCSR
            ConfigurableFaultStatus = 0xD28, // CFSR
            HardFaultStatus = 0xD2C, // HFSR
            DebugFaultStatus = 0xD30, // DFSR
            // FPU registers 0xD88 .. F3C
            MemoryFaultAddress = 0xD34, // MMFAR
            BusFaultAddress = 0xD38, // BFAR
            AuxiliaryFaultStatus = 0xD3C, // AFSR
            ProcessorFeature0 = 0xD40, // ID_PFR0
            ProcessorFeature1 = 0xD44, // ID_PFR1
            DebugFeature0 = 0xD48, // ID_DFR0
            AuxiliaryFeature0 = 0xD4C, // ID_AFR0
            MemoryModelFeature0 = 0xD50, // ID_MMFR0
            MemoryModelFeature1 = 0xD54, // ID_MMFR1
            MemoryModelFeature2 = 0xD58, // ID_MMFR2
            MemoryModelFeature3 = 0xD5C, // ID_MMFR3
            InstructionSetAttribute0 = 0xD60, // ID_ISAR0
            InstructionSetAttribute1 = 0xD64, // ID_ISAR1
            InstructionSetAttribute2 = 0xD68, // ID_ISAR2
            InstructionSetAttribute3 = 0xD6C, // ID_ISAR3
            InstructionSetAttribute4 = 0xD70, // ID_ISAR4
            ID_ISAR5 = 0xD74, // ID_ISAR5
            CacheLevelID = 0xD78, // CLIDR
            CacheType = 0xD7C, // CTR
            CacheSizeID = 0xD80, // CCSIDR
            CacheSizeSelection = 0xD84, // CSSELR
            CoprocessorAccessControl = 0xD88, // CPACR
            MPUType = 0xD90, // MPU_TYPE
            MPUControl = 0xD94, // MPU_CTRL
            MPURegionNumber = 0xD98, // MPU_RNR
            MPURegionBaseAddress = 0xD9C, // MPU_RBAR
            MPURegionAttributeAndSize = 0xDA0, // MPU_RASR
            Alias1OfMPURegionBaseAddress = 0xDA4, // MPU_RBAR_A1
            Alias1OfMPURegionAttributeAndSize = 0xDA8, // MPU_RASR_A1
            Alias2OfMPURegionBaseAddress = 0xDAC, // MPU_RBAR_A2
            Alias2OfMPURegionAttributeAndSize = 0xDB0, // MPU_RASR_A2
            Alias3OfMPURegionBaseAddress = 0xDB4, // MPU_RBAR_A3
            Alias3OfMPURegionAttributeAndSize = 0xDB8, // MPU_RASR_A3
            DebugExceptionAndMonitorControlRegister = 0xDFC, // DEMCR
            SoftwareTriggerInterrupt = 0xF00, // STIR
            FPContextControl = 0xF34, // FPCCR
            FPContextAddress = 0xF38, // FPCAR
            FPDefaultStatusControl = 0xF3C, // FPDSCR
            FloatingPointDefaultStatusControl = 0xF3C, // FPDSCR
            MediaAndFPFeature0 = 0xF40, // MVFR0
            MediaAndFPFeature1 = 0xF44, // MVFR1
            MediaAndFPFeature2 = 0xF48, // MVFR2
            ICacheInvalidateAllToPoUaIgnored = 0xF50, // ICIALLU
            ICacheInvalidateByMVAToPoUaAddress = 0xF58, // ICIMVAU
            DCacheInvalidateByMVAToPoCAddress = 0xF5C, // DCIMVAC
            DCacheInvalidateBySetWay= 0xF60, // DCISW
            DCacheCleanByMVAToPoUAddress = 0xF64, // DCCMVAU
            DCacheCleanByMVAToPoCAddress = 0xF68, // DCCMVAC
            DCacheCleanBySetWay= 0xF6C, // DCCSW
            DCacheCleanAndInvalidateByMVAToPoCAddress = 0xF70, // DCCIMVAC
            DCacheCleanAndInvalidateBySetWay= 0xF74, // DCCISW
            BranchPredictorInvalidateAllIgnored = 0xF78, // BPIALL

            // Registers with addresses from 0xF90 to 0xFCF are implementation defined.
            // The following ones are valid for Cortex-M7.
            InstructionTightlyCoupledMemoryControl = 0xF90, // ITCMCR
            DataTightlyCoupledMemoryControl = 0xF94, // DTCMCR
            AHBPControl = 0xF98, // AHBPCR
            L1CacheControl = 0xF9C, // CACR
            AHBSlaveControl = 0xFA0, // AHBSCR
            AuxiliaryBusFaultStatus = 0xFA8, // ABFSR
        }

        private enum RegistersV7
        {
            Type = 0x00,
            Control = 0x04,
            RegionNumber = 0x08,
            RegionBaseAddress = 0x0C,
            RegionAttributeAndSize = 0x10,
            RegionBaseAddressAlias1 = 0x14,
            RegionAttributeAndSizeAlias1 = 0x18,
            RegionBaseAddressAlias2 = 0x1C,
            RegionAttributeAndSizeAlias2 = 0x20,
            RegionBaseAddressAlias3 = 0x24,
            RegionAttributeAndSizeAlias3 = 0x28,
        }

        private enum RegistersV8
        {
            Type = 0x00,
            Control = 0x04,
            RegionNumberRegister = 0x08,
            RegionBaseAddressRegister = 0x0C,
            RegionLimitAddressRegister = 0x10,
            RegionBaseAddressRegisterAlias1 = 0x14,
            RegionLimitAddressRegisterAlias1 = 0x18,
            RegionBaseAddressRegisterAlias2 = 0x1C,
            RegionLimitAddressRegisterAlias2 = 0x20,
            RegionBaseAddressRegisterAlias3 = 0x24,
            RegionLimitAddressRegisterAlias3 = 0x28,
            MemoryAttributeIndirectionRegister0 = 0x30,
            MemoryAttributeIndirectionRegister1 = 0x34
        }

        private enum MPUVersion
        {
            PMSAv7,
            PMSAv8
        }

        private enum SystemException
        {
            NMI = 2,
            PendSV = 14,
            SysTick = 15
        }

        // bit [16] DC / Cache enable. This is a global enable bit for data and unified caches.
        private uint ccr = 0x10000;

        private readonly bool defaultHaltSystickOnDeepSleep;
        private byte priorityMask;
        private Stack<int> activeIRQs;
        private ISet<int> pendingIRQs;
        private int binaryPointPosition; // from the right
        private uint mpuControlRegister;
        private MPUVersion mpuVersion;

        private bool maskedInterruptPresent;
        private IFlagRegisterField systickEnabled;
        private IFlagRegisterField eventEnabled;
        private IFlagRegisterField countFlag;
        private IValueRegisterField systickReload;

        private readonly IRQState[] irqs;
        private readonly byte[] priorities;
        private readonly Action resetMachine;
        private CortexM cpu;
        private readonly LimitTimer systick;
        private readonly IMachine machine;
        private uint cpuId;

        private IFlagRegisterField sleepOnExitEnabled;
        private IFlagRegisterField deepSleepEnabled;
        private IFlagRegisterField currentSevOnPending;

        private const int MPUStart             = 0xD90;
        private const int MPUEnd               = 0xDC4;    // resized for compat. with V8 MPU
        private const int SpuriousInterrupt    = 256;
        private const int SetEnableStart       = 0x100;
        private const int SetEnableEnd         = 0x140;
        private const int ClearEnableStart     = 0x180;
        private const int ClearEnableEnd       = 0x1C0;
        private const int SetPendingStart      = 0x200;
        private const int SetPendingEnd        = 0x240;
        private const int ClearPendingStart    = 0x280;
        private const int ClearPendingEnd      = 0x2C0;
        private const int ActiveBitStart       = 0x300;
        private const int ActiveBitEnd         = 0x320;
        private const int PriorityStart        = 0x400;
        private const int PriorityEnd          = 0x7F0;
        private const int IRQCount             = 512 + 16;
        private const uint DefaultCpuId        = 0x412FC231;
        private const int VectKey              = 0x5FA;
        private const int VectKeyStat          = 0xFA05;
        private const uint SysTickMaximumValue = 0x00FFFFFF;

        private const uint InterruptProgramStatusRegisterMask = 0x1FF;
        private const uint NonMaskableInterruptIRQ = 2;
        private const int SysTickCalibration100Hz = 100;
        private const int SysTickMaxValue = (1 << 24) - 1;
    }
}
