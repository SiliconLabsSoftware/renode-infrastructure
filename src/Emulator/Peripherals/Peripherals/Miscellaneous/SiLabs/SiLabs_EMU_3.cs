//
// Copyright (c) 2010-2026 Antmicro
// Copyright (c) 2022-2026 Silicon Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Sensor;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Miscellaneous.SiLabs
{
    public partial class SiLabs_EMU_3 : ISiLabs_Emu, ITemperatureSensor, IResetCauseSink
    {
        partial void SiLabs_EMU_3_Constructor()
        {
            emuBase = new SiLabs_EMU_Base(machine, this);
            // A peripheral is not reset when its machine first starts, so the first boot reads
            // the cause latched here: a power-on reset.
            LatchResetCause(ResetCausePor);
        }

        partial void EMU_Reset()
        {
            registers.Reset();
            // RSTCAUSE latches why the core came out of reset: a system reset request, unless
            // a pin or power-on reset was announced beforehand.
            // It can be cleared by SW using the Rstcauseclr command.
            LatchResetCause(pendingResetCause);
            pendingResetCause = ResetCauseSysreq;
        }

        // Makes the next reset read as an external pin reset. Called by whatever pulls the
        // RESETn pin (ResetLine) just before it resets the machine.
        public void ReportPinReset()
        {
            pendingResetCause = ResetCausePin;
        }

        // Makes the next reset read as a power-on reset. Called from the Monitor before
        // `machine Reset` to script a power cycle.
        public void ReportPowerOnReset()
        {
            pendingResetCause = ResetCausePor;
        }

        // The POR, PIN and SYSREQ bits of RSTCAUSE as the firmware currently reads them.
        public uint ResetCause =>
            (rstcause_por_bit.Value ? ResetCausePor : 0)
            | (rstcause_pin_bit.Value ? ResetCausePin : 0)
            | (rstcause_sysreq_bit.Value ? ResetCauseSysreq : 0);

        // Die temperature in degrees Celsius, as read through TEMP. It holds whatever was set
        // last; self-heating is not modeled.
        public decimal Temperature
        {
            get
            {
                return temperature;
            }

            set
            {
                if(value < MinTemperature || value > MaxTemperature)
                {
                    throw new RecoverableException(
                        "The temperature value must be between {0} and {1} degrees Celsius - EMU.TEMP holds Kelvin x 4 in 11 bits."
                            .FormatWith(MinTemperature, MaxTemperature));
                }
                temperature = value;
            }
        }

        partial void Cmd_Rstcauseclr_Write(bool a, bool b)
        {
            if(b)
            {
                LatchResetCause(0);
            }
        }

        // TEMP[10:0] is the die temperature in kelvin x 4: TEMPLSB[1:0] is the fractional
        // quarter-kelvin and TEMP[10:2] the integer kelvin. TEMPAVG uses the same encoding.
        partial void Temp_Templsb_ValueProvider(ulong a)
        {
            temp_templsb_field.Value = KelvinQuarters & 0x3;
        }

        partial void Temp_Temp_ValueProvider(ulong a)
        {
            temp_temp_field.Value = (KelvinQuarters >> 2) & 0x1FF;
        }

        partial void Temp_Tempavg_ValueProvider(ulong a)
        {
            temp_tempavg_field.Value = KelvinQuarters;
        }

        partial void Cmd_Tempavgreq_Write(bool a, bool b)
        {
            if(!b)
            {
                return;
            }
            // emlib waits on the flag, so it is raised at once. The average reported is the
            // instantaneous value.
            if_tempavg_bit.Value = true;
            this.Log(LogLevel.Debug, "TEMPAVGREQ: reporting the instantaneous {0} degC as the average", temperature);
        }

        partial void Cmd_Em01vscale0_Write(bool a, bool b)
        {
            if(b)
            {
                ApplyEm01VScale(STATUS_VSCALE.VSCALE0);
            }
        }

        partial void Cmd_Em01vscale1_Write(bool a, bool b)
        {
            if(b)
            {
                ApplyEm01VScale(STATUS_VSCALE.VSCALE1);
            }
        }

        partial void Cmd_Em01vscale2_Write(bool a, bool b)
        {
            if(b)
            {
                ApplyEm01VScale(STATUS_VSCALE.VSCALE2);
            }
        }

        partial void Status_Em1p_ValueProvider(bool a)
        {
            status_em1p_bit.Value = emuBase.CurrentEnergyMode == SiLabs_EnergyMode.EM1P;
        }

        public void AddEnterSleepHook(Action hook)
        {
            emuBase.AddEnterSleepHook(hook);
        }

        public void AddExitSleepHook(Action hook)
        {
            emuBase.AddExitSleepHook(hook);
        }

        public void AddEnterDeepSleepHook(Action hook)
        {
            emuBase.AddEnterDeepSleepHook(hook);
        }

        public void AddExitDeepSleepHook(Action hook)
        {
            emuBase.AddExitDeepSleepHook(hook);
        }

        public void AddEnterEm1pHook(Action hook)
        {
            emuBase.AddEnterEm1pHook(hook);
        }

        public void AddExitEm1pHook(Action hook)
        {
            emuBase.AddExitEm1pHook(hook);
        }

        public void RegisterRadioSequencerCpu(CortexM cpu)
        {
            emuBase.RegisterRadioSequencerCpu(cpu);
        }

        public void SetEm1pRequest(object requester, bool request)
        {
            emuBase.SetEm1pRequest(requester, request);
        }

        public void AddEnterHostClockGateHook(Action hook)
        {
            emuBase.AddEnterHostClockGateHook(hook);
        }

        public void AddExitHostClockGateHook(Action hook)
        {
            emuBase.AddExitHostClockGateHook(hook);
        }

        public void AddEnterHostAllClockGateHook(Action hook)
        {
            emuBase.AddEnterHostAllClockGateHook(hook);
        }

        public void AddExitHostAllClockGateHook(Action hook)
        {
            emuBase.AddExitHostAllClockGateHook(hook);
        }

        public void SetEm1pHostClockRequest(object requester, bool request)
        {
            emuBase.SetEm1pHostClockRequest(requester, request);
        }

        public void SetQChannelBusy(object peripheral, bool busy)
        {
            emuBase.SetQChannelBusy(peripheral, busy);
        }

        public SiLabs_EnergyMode CurrentEnergyMode => emuBase.CurrentEnergyMode;

        public bool HostClocksEnabled => emuBase.HostClocksEnabled;

        public bool HostAllClocksEnabled => emuBase.HostAllClocksEnabled;

        public void UpdatePeripheralEnabledStatus(IPeripheral peripheral, bool enabled)
        {
            emuBase.UpdatePeripheralEnabledStatus(peripheral, enabled);
        }

        public List<IPeripheral> GetNoRetainPeripherals()
        {
            return emuBase.GetNoRetainPeripherals();
        }

        private void LatchResetCause(uint cause)
        {
            rstcause_por_bit.Value = (cause & ResetCausePor) != 0;
            rstcause_pin_bit.Value = (cause & ResetCausePin) != 0;
            rstcause_sysreq_bit.Value = (cause & ResetCauseSysreq) != 0;
        }

        private void ApplyEm01VScale(STATUS_VSCALE level)
        {
            status_vscalebusy_bit.Value = false;
            status_vscalefailed_bit.Value = false;
            status_vscale_field.Value = level;
            if_vscaledone_bit.Value = true;
        }

        // The TEMP/TEMPLSB pair in quarter-kelvin, rounded so that a whole-degree setting reads
        // back as that degree.
        private ulong KelvinQuarters => (ulong)decimal.Round((temperature + ZeroCelsiusInKelvin) * 4);

        private SiLabs_EMU_Base emuBase;
        private uint pendingResetCause = ResetCauseSysreq;
        private decimal temperature = 25;

        private const uint ResetCausePor = 1u << 0;
        private const uint ResetCausePin = 1u << 1;
        private const uint ResetCauseSysreq = 1u << 6;
        private const decimal ZeroCelsiusInKelvin = 273.15m;
        private const decimal MinTemperature = -ZeroCelsiusInKelvin;
        private const decimal MaxTemperature = 2047m / 4 - ZeroCelsiusInKelvin;
    }
}
