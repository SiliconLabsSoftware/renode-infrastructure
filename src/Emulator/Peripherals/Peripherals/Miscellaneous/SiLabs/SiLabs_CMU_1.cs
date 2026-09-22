//
// Copyright (c) 2010-2025 Silicon Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous.SiLabs
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public partial class SiLabs_CMU_1 : BasicDoubleWordPeripheral, IKnownSize, SiLabs_ICMU
    {
        public SiLabs_CMU_1(Machine machine, ulong hfxoFrequency) : base(machine)
        {
            this.hfxoFrequency = hfxoFrequency;

            Define_Registers();
            CMU_Reset();
        }

        partial void CMU_Reset()
        {
            sysclkctrl_clksel_field.Value = SYSCLKCTRL_CLKSEL.FSRCO;
            status_calrdy_bit.Value = true;
            status_lock_bit.Value = STATUS_LOCK.UNLOCKED;
        }

        public ulong WdogClockFrequency(uint instance)
        {
            if(instance != 0)
            {
                this.Log(LogLevel.Warning, "WdogClockFrequency: WDOG{0} not present on CMU_1", instance);
                return 0UL;
            }
            var clksel = (SiLabs_WdogClksel)wdog0clkctrl_clksel_field.Value;
            return SiLabs_CMU_Constants.WdogFrequencyFromClksel(clksel, hfxoFrequency);
        }

        public bool GetPeripheralClockEnable(IPeripheral peripheral)
        {
            return true; // Default to true for peripherals not handled by this method
        }

        public bool OscSocpllRequested(uint instance)
        {
            return false;
        }

        public bool OscSocpllEnabled(uint instance)
        {
            return false;
        }

        public bool OscPerpllRequested(uint instance)
        {
            return false;
        }

        public bool OscPerpllEnabled(uint instance)
        {
            return false;
        }

        public ulong HfxoFrequency
        {
            get
            {
                return hfxoFrequency;
            }
        }

        public bool OscLfrcoRequested { get; }

        public bool OscLfrcoEnabled { get; }

        public bool OscHfrcoRequested(uint instance)
        {
            return false;
        }

        public bool OscHfrcoEnabled(uint instance)
        {
            return false;
        }

        public bool OscHfrcoEM23Requested { get; }

        public bool OscHfrcoEM23Enabled { get; }

        public bool OscHfxoRequested { get; }

        public bool OscHfxoEnabled { get; }

        public bool OscLfxoRequested { get; }

        public bool OscLfxoEnabled { get; }

        public bool OscDpllEnabled { get; }

        public ulong Dpll_N { get; set; }

        public ulong Dpll_M { get; set; }

        private SiLabs_IDPLL dpll0
        {
            get
            {
                if(_dpll0 is null)
                {
                    foreach(var dpll0 in machine.GetPeripheralsOfType<SiLabs_IDPLL>())
                    {
                        if(dpll0.Instance == 0)
                        {
                            _dpll0 = dpll0;
                        }
                    }
                }
                return _dpll0;
            }

            set
            {
                _dpll0 = value;
            }
        }

        private bool Dpll0Enabled
        {
            get
            {
                return dpll0.Enabled;
            }
        }

        // Reference to dpll to check if a peripheral is enabled
        private SiLabs_IDPLL _dpll0;
        private readonly ulong hfxoFrequency;
    }
}