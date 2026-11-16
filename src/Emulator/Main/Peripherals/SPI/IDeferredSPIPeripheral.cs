//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

namespace Antmicro.Renode.Peripherals.SPI
{
    // An SPI slave whose answer to a frame may not be available synchronously - in practice a
    // slave that lives in ANOTHER MACHINE, reached through a link external such as SPILink.
    //
    // ISPIPeripheral.Transmit() has to return the MISO byte on the spot, which forces the master's
    // CPU thread to call straight into the other machine's peripheral. With the two machines
    // interleaving per quantum that is both unsafe and wrong-in-time. A master that recognizes
    // this interface hands the byte over and completes the frame - RX data, TXC - when the slave's
    // answer arrives via `completion`, which the link invokes in the master machine's own time
    // domain (Machine.HandleTimeDomainEvent). Masters that do not know the interface still work
    // through Transmit(), so a link is usable with any Renode SPI controller.
    public interface IDeferredSPIPeripheral : ISPIPeripheral
    {
        void TransmitDeferred(byte data, Action<byte> completion);
    }
}
