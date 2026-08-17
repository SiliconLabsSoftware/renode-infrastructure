//
// Copyright (c) 2010-2025 Antmicro
// Copyright (c) 2022-2025 Silicon Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

namespace Antmicro.Renode.Peripherals.Miscellaneous.SiLabs
{
    public interface SiLabs_IDPLL
    {
        /// <summary>
        /// Instance number for this DPLL instance.
        /// </summary>
        uint Instance { get; }

        /// <summary>
        /// Returns true if the DPLL is enabled, false otherwise.
        /// </summary>
        bool Enabled { get; }
    }
}