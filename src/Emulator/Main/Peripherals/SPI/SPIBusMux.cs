//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.SPI
{
    // One SPI bus, several chip selects.
    //
    // Renode's SPI controllers take a single ISPIPeripheral child and give it every frame. A real
    // bus fans out by chip select: one controller may carry, say, a companion MCU on one chip
    // select and an external SPI flash on another. This registers on the controller as that
    // single child and routes each frame to whichever slot has its CS low - a slot's slave is an
    // ordinary ISPIPeripheral registered here by slot number, or a SPILink to another machine.
    // With no slot selected the bus reads 0xFF, as pulled-up MISO does with nothing driving it.
    //
    //     spibus: SPI.SPIBusMux @ usart2
    //     flash: SPI.GenericSpiFlash @ spibus 1
    //         ...
    //     gpioPort:
    //         52 -> spibus@0        // PD4: slot 0's chip select (filled by a SPILink)
    //         49 -> spibus@1        // PD1: slot 1's chip select (the flash)
    //
    // Chip-select inputs are GPIO pin numbers = slot numbers, active low. A GPIO port comes out of
    // reset driving low, so a slot counts as selected only after its line has been seen high once
    // (the board pulls CS up; firmware takes the pin as an output at 1 before the first frame).
    public class SPIBusMux : IPeripheralContainer<ISPIPeripheral, NumberRegistrationPoint<int>>, IDeferredSPIPeripheral, IGPIOReceiver
    {
        public SPIBusMux(IMachine machine, int slots = 4)
        {
            if(slots < 1 || slots > 32)
            {
                throw new ConstructionException("slots must be 1..32");
            }
            this.machine = machine;
            slaves = new ISPIPeripheral[slots];
            selected = new bool[slots];
            driven = new bool[slots];
            selectedBytes = new ulong[slots];
        }

        public void Reset()
        {
            // The MCU that owns the chip selects has just reset: its GPIO port comes back driving
            // every line low, and the board's pull-ups mean nothing is selected until the SPI
            // driver takes a CS pin as an output at 1 again. So every slot goes back to
            // "undriven, not selected" -- a frame in flight is over, and a link tells the other
            // machine so. Counters restart.
            for(var i = 0; i < selected.Length; i++)
            {
                if(selected[i])
                {
                    if(slaves[i] is SPILink link)
                    {
                        link.OnGPIO(0, true);
                    }
                    else
                    {
                        slaves[i]?.FinishTransmission();
                    }
                }
                selected[i] = false;
                driven[i] = false;
                selectedBytes[i] = 0;
            }
            UnselectedBytes = 0;
        }

        // -- container ----------------------------------------------------------------------

        public void Register(ISPIPeripheral peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            var slot = registrationPoint.Address;
            CheckSlot(slot);
            if(slaves[slot] != null)
            {
                throw new RegistrationException($"Slot {slot} is already taken");
            }
            slaves[slot] = peripheral;
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
        }

        public void Unregister(ISPIPeripheral peripheral)
        {
            var slot = Array.IndexOf(slaves, peripheral);
            if(slot < 0)
            {
                throw new RegistrationException("Peripheral is not registered on this bus");
            }
            slaves[slot] = null;
            machine.UnregisterAsAChildOf(this, peripheral);
        }

        public IEnumerable<NumberRegistrationPoint<int>> GetRegistrationPoints(ISPIPeripheral peripheral)
        {
            var slot = Array.IndexOf(slaves, peripheral);
            return slot < 0 ? Enumerable.Empty<NumberRegistrationPoint<int>>() : new[] { new NumberRegistrationPoint<int>(slot) };
        }

        public IEnumerable<IRegistered<ISPIPeripheral, NumberRegistrationPoint<int>>> Children =>
            slaves.Select((s, i) => (s, i)).Where(p => p.s != null)
                  .Select(p => Registered.Create(p.s, new NumberRegistrationPoint<int>(p.i)));

        // For a slave that is not a machine peripheral - a SPILink external - and so cannot be
        // registered through the .repl. The link calls this from AttachMasterSlot.
        public void AttachExternalSlave(ISPIPeripheral slave, int slot)
        {
            CheckSlot(slot);
            if(slaves[slot] != null)
            {
                throw new RecoverableException($"Slot {slot} is already taken");
            }
            slaves[slot] = slave;
        }

        // -- chip selects ---------------------------------------------------------------------

        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= slaves.Length)
            {
                this.Log(LogLevel.Warning, "Chip select {0} does not exist (slots 0-{1})", number, slaves.Length - 1);
                return;
            }
            if(value)
            {
                driven[number] = true;
            }
            else if(!driven[number])
            {
                return;   // the port's reset-low, not a selection
            }
            var nowSelected = !value;
            if(nowSelected == selected[number])
            {
                return;
            }
            selected[number] = nowSelected;
            var slave = slaves[number];
            if(slave is SPILink link)
            {
                link.OnGPIO(0, value);       // the link forwards the level to the other machine
            }
            else if(!nowSelected && slave != null)
            {
                slave.FinishTransmission();
            }
            if(nowSelected && SelectedCount > 1)
            {
                this.Log(LogLevel.Warning, "More than one chip select is low ({0}); frames go to the lowest slot", SelectedCount);
            }
        }

        // -- master-facing -------------------------------------------------------------------

        public byte Transmit(byte data)
        {
            var slot = SelectedSlot;
            if(slot < 0)
            {
                UnselectedBytes++;
                return 0xFF;
            }
            selectedBytes[slot]++;
            return slaves[slot].Transmit(data);
        }

        public void TransmitDeferred(byte data, Action<byte> completion)
        {
            var slot = SelectedSlot;
            if(slot < 0)
            {
                UnselectedBytes++;
                completion(0xFF);
                return;
            }
            selectedBytes[slot]++;
            if(slaves[slot] is IDeferredSPIPeripheral deferred)
            {
                deferred.TransmitDeferred(data, completion);
                return;
            }
            completion(slaves[slot].Transmit(data));
        }

        public void FinishTransmission()
        {
            // The controller's notion of end-of-transfer; chip select is what the slaves follow.
        }

        // -- observation ---------------------------------------------------------------------

        public ulong UnselectedBytes { get; private set; }
        public int SelectedSlot
        {
            get
            {
                for(var i = 0; i < selected.Length; i++)
                {
                    if(selected[i] && slaves[i] != null)
                    {
                        return i;
                    }
                }
                return -1;
            }
        }
        public ulong SlotBytes(int slot)
        {
            CheckSlot(slot);
            return selectedBytes[slot];
        }
        public string Slots => string.Join(", ", slaves.Select((s, i) =>
            $"{i}:{(s == null ? "empty" : s.GetType().Name)}{(selected[i] ? " [selected]" : "")}"));

        private int SelectedCount => selected.Count(s => s);

        private void CheckSlot(int slot)
        {
            if(slot < 0 || slot >= slaves.Length)
            {
                throw new RecoverableException($"slot must be 0..{slaves.Length - 1}");
            }
        }

        private readonly IMachine machine;
        private readonly ISPIPeripheral[] slaves;
        private readonly bool[] selected;
        private readonly bool[] driven;
        private readonly ulong[] selectedBytes;
    }
}
