//
// Copyright (c) 2010-2025 Silicon Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;
using Range = Antmicro.Renode.Core.Range;

namespace Antmicro.Renode.Peripherals.Miscellaneous.SiLabs
{
    /// <summary>
    /// Optional: implement this to notify when a register is accessed.
    /// </summary>
    public interface SiLabs_IRegisterAccess
    {
        Action OnRegisterAccess { get; set; }
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
    /// Optional: implement this to report registers and ranges to power down. If not implemented, the entire peripheral is treated as not retained.
    /// </summary>
    public interface SiLabs_IPartialRetention
    {
        List<DoubleWordRegister> GetPowerDownRegisters();
        List<Range> AddRangesToPowerDown();
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

    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public abstract class SiLabsPeripheral : IDoubleWordPeripheral, SiLabs_IRegisterAccess
    {
        public SiLabsPeripheral(Machine machine, bool buildRegisters = true)
        {
            this.machine = machine;

            if(buildRegisters)
            {
                registersCollection = BuildRegistersCollection();
            }
        }

        public virtual byte ReadByte(long offset)
        {
            OnRegisterAccess?.Invoke();
            int byteOffset = (int)(offset & 0x3);
            uint registerValue = ReadRegister(offset, true);
            byte result = (byte)((registerValue >> byteOffset*8) & 0xFF);
            return result;
        }

        public virtual uint ReadDoubleWord(long offset)
        {
            OnRegisterAccess?.Invoke();
            return ReadRegister(offset);
        }

        public virtual void WriteDoubleWord(long offset, uint value)
        {
            OnRegisterAccess?.Invoke();
            WriteRegister(offset, value);
        }

        public virtual void Reset()
        {
            registersCollection.Reset();
            UpdateInterrupts();
        }

        public Action OnRegisterAccess { get; set; }

        protected TimeInterval GetTime() => machine.LocalTimeSource.ElapsedVirtualTime;

        protected bool TrySyncTime()
        {
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu))
            {
                cpu.SyncTime();
                return true;
            }
            return false;
        }

        protected virtual void UpdateInterrupts()
        {
        }

        protected uint ReadRegister(long offset, bool internal_read = false)
        {
            var result = 0U;
            long internal_offset = offset;

            // Set, Clear, Toggle registers should only be used for write operations. But just in case we convert here as well.
            if(offset >= SetRegisterOffset && offset < ClearRegisterOffset)
            {
                // Set register
                internal_offset = offset - SetRegisterOffset;
                if(!internal_read)
                {
                    this.Log(LogLevel.Noisy, "SET Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}", Enum.ToObject(RegistersType, internal_offset), offset, internal_offset);
                }
            }
            else if(offset >= ClearRegisterOffset && offset < ToggleRegisterOffset)
            {
                // Clear register
                internal_offset = offset - ClearRegisterOffset;
                if(!internal_read)
                {
                    this.Log(LogLevel.Noisy, "CLEAR Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}", Enum.ToObject(RegistersType, internal_offset), offset, internal_offset);
                }
            }
            else if(offset >= ToggleRegisterOffset)
            {
                // Toggle register
                internal_offset = offset - ToggleRegisterOffset;
                if(!internal_read)
                {
                    this.Log(LogLevel.Noisy, "TOGGLE Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}", Enum.ToObject(RegistersType, internal_offset), offset, internal_offset);
                }
            }

            try
            {
                if(registersCollection.TryRead(internal_offset, out result))
                {
                    return result;
                }
            }
            finally
            {
                if(!internal_read)
                {
                    this.Log(LogLevel.Debug, "Read at offset 0x{0:X} ({1}), returned 0x{2:X}.", internal_offset, Enum.ToObject(RegistersType, internal_offset), result);
                }
            }

            if(!internal_read)
            {
                this.Log(LogLevel.Warning, "Unhandled read at offset 0x{0:X} ({1}).", internal_offset, Enum.ToObject(RegistersType, internal_offset));
            }

            return 0;
        }

        protected void WriteRegister(long offset, uint value, bool internal_write = false)
        {
            machine.ClockSource.ExecuteInLock(delegate
            {
                long internal_offset = offset;
                uint internal_value = value;

                if(offset >= SetRegisterOffset && offset < ClearRegisterOffset)
                {
                    // Set register
                    internal_offset = offset - SetRegisterOffset;
                    uint old_value = ReadRegister(internal_offset, true);
                    internal_value = old_value | value;
                    this.Log(LogLevel.Noisy, "SET Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}, SET_value=0x{3:X}, old_value=0x{4:X}, new_value=0x{5:X}", Enum.ToObject(RegistersType, internal_offset), offset, internal_offset, value, old_value, internal_value);
                }
                else if(offset >= ClearRegisterOffset && offset < ToggleRegisterOffset)
                {
                    // Clear register
                    internal_offset = offset - ClearRegisterOffset;
                    uint old_value = ReadRegister(internal_offset, true);
                    internal_value = old_value & ~value;
                    this.Log(LogLevel.Noisy, "CLEAR Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}, CLEAR_value=0x{3:X}, old_value=0x{4:X}, new_value=0x{5:X}", Enum.ToObject(RegistersType, internal_offset), offset, internal_offset, value, old_value, internal_value);
                }
                else if(offset >= ToggleRegisterOffset)
                {
                    // Toggle register
                    internal_offset = offset - ToggleRegisterOffset;
                    uint old_value = ReadRegister(internal_offset, true);
                    internal_value = old_value ^ value;
                    this.Log(LogLevel.Noisy, "TOGGLE Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}, TOGGLE_value=0x{3:X}, old_value=0x{4:X}, new_value=0x{5:X}", Enum.ToObject(RegistersType, internal_offset), offset, internal_offset, value, old_value, internal_value);
                }

                this.Log(LogLevel.Debug, "Write at offset 0x{0:X} ({1}), value 0x{2:X}.", internal_offset, Enum.ToObject(RegistersType, internal_offset), internal_value);

                if(!registersCollection.TryWrite(internal_offset, internal_value))
                {
                    this.Log(LogLevel.Warning, "Unhandled write at offset 0x{0:X} ({1}), value 0x{2:X}.", internal_offset, Enum.ToObject(RegistersType, internal_offset), internal_value);
                    return;
                }
            });
        }

        protected ulong HfxoFrequency
        {
            get
            {
                if(Object.ReferenceEquals(cmu, null))
                {
                    foreach(var peripheral in machine.GetPeripheralsOfType<SiLabs_ICMU>())
                    {
                        cmu = peripheral;
                        break;
                    }
                }

                return cmu.HfxoFrequency;
            }
        }

        // Abstract methods to be implemented
        protected abstract DoubleWordRegisterCollection BuildRegistersCollection();

        protected abstract Type RegistersType { get; }

        protected DoubleWordRegisterCollection registersCollection;
        protected readonly Machine machine;
        private SiLabs_ICMU cmu;
        private const uint SetRegisterOffset = 0x1000;
        private const uint ClearRegisterOffset = 0x2000;
        private const uint ToggleRegisterOffset = 0x3000;
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
            if (@this is SiLabs_IRetention peripheral) peripheral.PowerDown(); else @this.Reset();
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
        /// Adds the ranges to the power down list if the peripheral implements SiLabs_IPartialRetention; otherwise returns an empty list.
        /// </summary>
        public static List<Range> AddRangesToPowerDown(this IPeripheral @this)
        {
            return @this is SiLabs_IPartialRetention peripheral ? peripheral.AddRangesToPowerDown() : new List<Range>();
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

    /// <summary>
    /// Helper methods for retention.
    /// </summary>
    public static class SiLabs_Retention
    {
                // Gets the system bus ranges for the registers of a peripheral.
        public static List<Range> GetRegisterRangesFromSystemBus(IPeripheral peripheral, List<ulong> registerAddresses)
        {
            var busPeripheral = peripheral as IBusPeripheral;
            var registrations = peripheral.GetMachine().GetSystemBus(busPeripheral).GetRegistrationPoints(busPeripheral).OfType<BusRangeRegistration>().ToList();
            return GetRegisterRangesFromRegistrations(registrations, registerAddresses);
        }

        // Gets the system bus ranges for the registration.
        public static List<Range> GetRegisterRangesFromRegistrations(List<BusRangeRegistration> registrations, List<ulong> registerAddresses)
        {
            var ranges = new List<Range>();
            foreach(var registration in registrations)
            {
                ulong baseAddress = registration.Range.StartAddress;
                if (registration.Range.Size != 0x4000)
                {
                    continue;
                }
                ranges.AddRange(registerAddresses.Select(registerAddress => new Range(baseAddress + registerAddress, 4)));
            }
            return ranges;
        }
    }
}