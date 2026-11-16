//
// Copyright (c) 2010-2026 Antmicro
// Copyright (c) 2022-2026 Silicon Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.Miscellaneous.SiLabs
{
    public partial class SiLabs_HFRCO_2
    {
        public SiLabs_HFRCO_2(Machine machine, uint instance = 0) : base(machine)
        {
            Define_Registers();
            localInstance = instance;
            SiLabs_HFRCO_2_Constructor();
        }

        partial void HFRCO_Reset()
        {
            oscillatorEM23OnDemand = false;
            oscillatorOnDemand = false;
            oscillatorForce = false;

            registers.Reset();
            (cmu as SiLabs_CMU_3)?.NotifyClockTreeChanged();
        }

        // The oscillator's output frequency in hertz, from CAL.FREQRANGE: the band table that
        // emlib's CMU_HFRCODPLLBandSet() and the Gecko bootloader's util_getClockFreq() share
        // (index -> MHz). The reset value is FREQRANGE 8 = 19 MHz. A band the table has no entry
        // for reads 19 MHz, as the bootloader's own lookup does. The DPLL is not folded in.
        public uint Frequency
        {
            get
            {
                var range = (int)cal_freqrange_field.Value;
                var megahertz = range < BandMegahertz.Length ? BandMegahertz[range] : 0u;
                if(megahertz == 0)
                {
                    megahertz = 19;
                }
                var hertz = megahertz * 1000000u;
                if(megahertz == 4)
                {
                    hertz >>= (int)cal_clkdiv_field.Value;
                }
                return hertz;
            }
        }

        partial void Cal_Freqrange_Write(ulong a, ulong b)
        {
            if(a != b)
            {
                (cmu as SiLabs_CMU_3)?.NotifyClockTreeChanged();
            }
        }

        partial void Cal_Clkdiv_Write(CAL_CLKDIV a, CAL_CLKDIV b)
        {
            if(a != b)
            {
                (cmu as SiLabs_CMU_3)?.NotifyClockTreeChanged();
            }
        }

        partial void Ctrl_Forceen_Write(bool a, bool b)
        {
            oscillatorForce = (b == true);
        }

        partial void Ctrl_Disondemand_Write(bool a, bool b)
        {
            oscillatorOnDemand = (b != true);
        }

        partial void Ctrl_Em23ondemand_Write(bool a, bool b)
        {
            oscillatorEM23OnDemand = (b != true);
        }

        partial void Lock_Lockkey_Write(ulong a, ulong b)
        {
            if(b == 0x8195)
            {
                status_lock_bit.Value = STATUS_LOCK.UNLOCKED;
            }
            else
            {
                status_lock_bit.Value = STATUS_LOCK.LOCKED;
            }
        }

        partial void Status_Rdy_ValueProvider(bool a)
        {
            status_rdy_bit.Value = IsUsed;
        }

        partial void Status_Ens_ValueProvider(bool a)
        {
            status_ens_bit.Value = IsUsed;
        }

        partial void If_Rdy_ValueProvider(bool a)
        {
            if_rdy_bit.Value = IsUsed && ien_rdy_bit.Value;
        }

        private bool IsUsed
        {
            get
            {
                return oscillatorForce
                    || ((oscillatorOnDemand || oscillatorEM23OnDemand) || cmu.OscHfrcoRequested(localInstance));
            }
        }

        private bool IsEnabled
        {
            get { return cmu.OscHfrcoEnabled(localInstance); }
        }

        private bool oscillatorEM23OnDemand = false;
        private bool oscillatorOnDemand = false;
        private bool oscillatorForce = false;
        private readonly uint localInstance = 0;

        private static readonly uint[] BandMegahertz = { 4, 0, 0, 7, 0, 0, 13, 16, 19, 0, 26, 32, 38, 48, 56, 64, 80 };
    }
}
