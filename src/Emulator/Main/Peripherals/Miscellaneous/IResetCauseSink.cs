//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Implemented by a reset-cause register model (SiLabs_EMU_3) so that the thing pulling the
    // RESETn pin - ResetLine - can tell it the coming reset is an external pin reset rather than
    // a software one. Firmware commonly classifies its boot from that register.
    public interface IResetCauseSink : IPeripheral
    {
        void ReportPinReset();
    }
}
