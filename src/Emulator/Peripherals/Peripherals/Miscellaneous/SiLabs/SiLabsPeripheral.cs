//
// Copyright (c) 2010-2025 Silicon Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous.SiLabs
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public abstract class SiLabsPeripheral : IDoubleWordPeripheral, IKnownSize, IAbsoluteAddressAware
    {
        public SiLabsPeripheral(Machine machine, bool buildRegisters = true)
        {
            this.machine = machine;
            if(buildRegisters)
            {
                registersCollection = BuildRegistersCollection();
            }
        }

        public void SetAbsoluteAddress(ulong address)
        {
            currentAbsoluteAddress = address;
        }

        public byte ReadByte(long offset)
        {
            int byteOffset = (int)(offset & 0x3);
            uint registerValue = ReadDoubleWord(offset - byteOffset);
            byte result = (byte)((registerValue >> byteOffset*8) & 0xFF);
            return result;
        }

        public uint ReadDoubleWord(long offset)
        {
            if(!VerifyPeripheralAccess(isWrite: false, offset))
            {
                return 0;
            }

            long internal_offset = offset & 0x0FFF;
            switch(offset & 0x3000)
            {
            case 0x0000:
                return registersCollection.Read(internal_offset);
            default:
                this.Log(LogLevel.Warning, "Reading from Set/Clr/Tgl is not supported");
                return registersCollection.Read(internal_offset);
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            machine.ClockSource.ExecuteInLock(delegate
            {
                if(!VerifyPeripheralAccess(isWrite: true, offset))
                {
                    return;
                }

                long internal_offset = offset & 0x0FFF;
                switch(offset & 0x3000)
                {
                case 0x0000:
                    registersCollection.Write(internal_offset, value);
                    break;
                case 0x1000:
                    registersCollection.Write(internal_offset, registersCollection.Read(internal_offset) | value);
                    break;
                case 0x2000:
                    registersCollection.Write(internal_offset, registersCollection.Read(internal_offset) & ~value);
                    break;
                case 0x3000:
                    registersCollection.Write(internal_offset, registersCollection.Read(internal_offset) ^ value);
                    break;
                default:
                    this.Log(LogLevel.Error, "writing doubleWord to non existing offset 0x{0:X}", offset);
                    break;
                }
            });
        }

        public virtual void Reset()
        {
            registersCollection.Reset();
            UpdateInterrupts();
        }

        /// <summary>
        /// Indicates whether the peripheral's enable register is set.
        /// Derived classes should define this when the peripheral has an EN register.
        /// </summary>
        public virtual bool Enabled { get; set; }

        public bool isSecureAccess => (currentAbsoluteAddress & 0x10000000) != 0;

        public virtual long Size => 0x4000;

        protected void WriteWSTATIC()
        {
            if(Enabled)
            {
                this.Log(LogLevel.Error, "Write to a WSTATIC register ({0}) while peripheral is enabled; ignored on hardware", Enum.ToObject(RegistersType, currentAbsoluteAddress & 0x3FFF));
            }
        }

        protected void WriteWSYNC()
        {
            if(!Enabled)
            {
                this.Log(LogLevel.Error, "Trying to write to a WSYNC register ({0}) while peripheral is disabled", Enum.ToObject(RegistersType, currentAbsoluteAddress & 0x3FFF));
                TriggerBusFault(currentAbsoluteAddress);
            }
        }

        protected void WriteRWSYNC()
        {
            if(!Enabled)
            {
                this.Log(LogLevel.Error, "Trying to write to a RWSYNC register ({0}) while peripheral is disabled", Enum.ToObject(RegistersType, currentAbsoluteAddress & 0x3FFF));
                TriggerBusFault(currentAbsoluteAddress);
            }
        }

        protected uint ReadWFIFO()
        {
            this.Log(LogLevel.Warning, "Reading from a WFIFO Field, value returned will always be 0");
            return 0x0;
        }

        protected uint ReadLFWSYNC()
        {
            this.Log(LogLevel.Warning, "Reading from a LFWSYNC/HVLFWSYNC Field, value returned will always be 0");
            return 0x0;
        }

        protected uint ReadRFIFO()
        {
            this.Log(LogLevel.Warning, "Reading from a RFIFO Field, value returned will always be 0");
            return 0x0;
        }

        internal bool VerifyPeripheralAccess(bool isWrite, long offset)
        {
            if(cmu != null && cmu.GetPeripheralClockEnable(this) == false)
            {
                this.Log(LogLevel.Error, "Accessing a peripheral while peripheral clock is disabled");
                TriggerBusFault(currentAbsoluteAddress);
                return false;
            }
            if(currentCpu is CortexM cortexM && cortexM.TrustZoneEnabled && smu != null && smu.GetPeripheralSecurityLevel(this) != isSecureAccess)
            {
                this.Log(LogLevel.Error, "Accessing a {0} peripheral while peripheral security level is {1}",
                    isSecureAccess ? "secure" : "non-secure",
                    isSecureAccess ? "non-secure" : "secure");
                TriggerBusFault(currentAbsoluteAddress);
                return false;
            }
            if(!VerifyPapAccess(isWrite, offset))
            {
                return false;
            }
            smu?.UpdateDirtyState(this);
            return true;
        }

        /// <summary>
        /// Optional PAP filter. Return false to reject the access:
        /// reads RAZ to 0, writes are dropped. Subclasses that reject a write
        /// should report the IMU VIOCON consequence themselves.
        /// </summary>
        protected virtual bool VerifyPapAccess(bool isWrite, long offset)
        {
            return true;
        }

        protected TimeInterval GetTime() => machine.LocalTimeSource.ElapsedVirtualTime;

        protected bool TrySyncTime()
        {
            if(currentCpu != null)
            {
                currentCpu.SyncTime();
                return true;
            }
            return false;
        }

        protected virtual void UpdateInterrupts()
        {
        }

        /// <summary>
        /// Triggers a precise bus fault on the currently-executing CortexM CPU.
        /// </summary>
        protected void TriggerBusFault(ulong address)
        {
            if(currentCpu is CortexM cortexM)
            {
                cortexM.RaisePreciseBusFault(checked((uint)address));
                return;
            }
            this.Log(LogLevel.Error, "TriggerBusFault: no active CortexM CPU; precise bus fault not delivered");
        }

        protected abstract DoubleWordRegisterCollection BuildRegistersCollection();

        protected ulong HfxoFrequency => cmu.HfxoFrequency;

        protected SiLabs_ICMU cmu
        {
            get
            {
                if(Object.ReferenceEquals(_cmu, null))
                {
                    foreach(var peripheral in machine.GetPeripheralsOfType<SiLabs_ICMU>())
                    {
                        _cmu = peripheral;
                        break;
                    }
                }
                return _cmu;
            }

            set
            {
                _cmu = value;
            }
        }

        protected SiLabs_IEmu emu
        {
            get
            {
                if(Object.ReferenceEquals(_emu, null))
                {
                    foreach(var peripheral in machine.GetPeripheralsOfType<SiLabs_IEmu>())
                    {
                        _emu = peripheral;
                        break;
                    }
                }
                return _emu;
            }

            set
            {
                _emu = value;
            }
        }

        protected SiLabs_ISMU smu
        {
            get
            {
                if(Object.ReferenceEquals(_smu, null))
                {
                    foreach(var peripheral in machine.GetPeripheralsOfType<SiLabs_ISMU>())
                    {
                        _smu = peripheral;
                        break;
                    }
                }
                return _smu;
            }

            set
            {
                _smu = value;
            }
        }

        protected ICPU currentCpu
        {
            get
            {
                // Monitor and test writes have no executing CPU; GetCurrentCPU() throws in that case.
                if(machine.SystemBus.TryGetCurrentCPU(out var cpu))
                {
                    return cpu;
                }
                return null;
            }
        }

        protected abstract Type RegistersType { get; }

        protected DoubleWordRegisterCollection registersCollection;

        protected readonly Machine machine;

        private SiLabs_ICMU _cmu;

        private SiLabs_IEmu _emu;

        private SiLabs_ISMU _smu;

        private ulong currentAbsoluteAddress;
        private const uint SetRegisterOffset = 0x1000;
        private const uint ClearRegisterOffset = 0x2000;
        private const uint ToggleRegisterOffset = 0x3000;
    }

    /// <summary>
    /// Optional: implement this to report a custom retention state. If not implemented, the entire peripheral is treated as not retained.
    /// </summary>
    public interface SiLabs_IRetention
    {
        bool Enabled { get; }

        void PowerDown();
    }

    /// <summary>
    /// Optional: implement this to report registers to power down. If not implemented, the entire peripheral is treated as not retained.
    /// </summary>
    public interface SiLabs_IPartialRetention
    {
        List<DoubleWordRegister> GetPowerDownRegisters();
    }

    /// <summary>
    /// Optional, opt-in hook for partial-retention peripherals that carry C# state outside their
    /// DoubleWordRegister collection (e.g. per-channel objects). EMU invokes OnPartialPowerDown()
    /// once per non-retained peripheral, just before resetting the registers returned by
    /// GetPowerDownRegisters(). Peripherals whose entire non-retained state already lives in
    /// DoubleWordRegister instances should NOT implement this interface.
    /// </summary>
    public interface SiLabs_IPartialPowerDownHook
    {
        void OnPartialPowerDown();
    }

    /// <summary>
    /// Extension methods for IPeripheral used by SiLabs peripherals.
    /// </summary>
    public static class SiLabs_IPeripheralExtensions
    {
        /// <summary>
        /// Returns the enabled state of the peripheral if it implements SiLabs_IRetention; otherwise returns false.
        /// </summary>
        public static bool Enabled(this IPeripheral @this)
        {
            return @this is SiLabs_IRetention peripheral ? peripheral.Enabled : false;
        }

        /// <summary>
        /// Power down the peripheral if it implements SiLabs_IRetention; otherwise reset it.
        /// Only used when GetPowerDownRegisters returns an empty list.
        /// </summary>
        public static void PowerDown(this IPeripheral @this)
        {
            if(@this is SiLabs_IRetention peripheral) peripheral.PowerDown(); else @this.Reset();
        }

        /// <summary>
        /// Returns a list of the registers to power down if it implements SiLabs_IPartialRetention; otherwise returns an empty list.
        /// Returning an empty list means that the entire peripheral is not retained when the system powers down.
        /// </summary>
        public static List<DoubleWordRegister> GetPowerDownRegisters(this IPeripheral @this)
        {
            return @this is SiLabs_IPartialRetention peripheral ? peripheral.GetPowerDownRegisters() : new List<DoubleWordRegister>();
        }

        /// <summary>
        /// Invokes the opt-in partial-power-down hook if the peripheral implements SiLabs_IPartialPowerDownHook; otherwise no-op.
        /// Called by EMU before resetting the registers reported by GetPowerDownRegisters().
        /// </summary>
        public static void OnPartialPowerDown(this IPeripheral @this)
        {
            (@this as SiLabs_IPartialPowerDownHook)?.OnPartialPowerDown();
        }
    }
}
