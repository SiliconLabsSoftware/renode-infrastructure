//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.SPI
{
    public static class SPILinkExtensions
    {
        public static void CreateSPILink(this Emulation emulation, string name)
        {
            emulation.ExternalsManager.AddExternal(new SPILink(), name);
        }
    }

    // A point-to-point SPI wire between two MACHINES: an SPI controller in one (the master) and an
    // ISPIPeripheral in another (the slave, e.g. an EUSART in slave mode running its own firmware).
    //
    // Why it exists: Renode's SPI model is intra-machine - the controller calls the slave's
    // Transmit() and expects the MISO byte back on the spot. Across machines that call would land
    // on another machine's peripheral from a foreign CPU thread at an arbitrary point in its
    // quantum. This external does what LogicGate and the wireless medium do for GPIOs and frames:
    // it hands each byte to the slave machine through Machine.HandleTimeDomainEvent, so the slave
    // processes it at its next synced state, and delivers the answer back to the master the same
    // way. The cost is that a frame takes up to one quantum per direction; the two-node scripts
    // keep the global quantum at 100 us for that reason (a 1 Mbit/s byte is 8 us on silicon).
    //
    // Monitor usage:
    //     emulation CreateSPILink "spilink"
    //     mach set "master";  spilink AttachMaster sysbus.usart2
    //                         spilink AttachChipSelect sysbus.gpioPort 52     # PD4, master's CS output
    //     mach set "slave";   spilink AttachSlave sysbus.eusart1
    //                         spilink AttachSlaveChipSelectPin sysbus.gpioPort 36   # PC4, slave's CS input
    //
    // The master side registers this object as the controller's single ISPIPeripheral child. A
    // controller that implements the deferred protocol (IDeferredSPIPeripheral - SiLabs_USART_0
    // does) gets its frame completed asynchronously; any other controller gets a synchronous
    // Transmit(), which calls straight into the slave machine and is documented as such.
    //
    // Without a master attached, Inject() drives the slave as a scripted master, so a
    // single-machine script can send the slave a frame.
    public class SPILink : IExternal, IDeferredSPIPeripheral, IGPIOReceiver
    {
        public SPILink()
        {
            lastMosi = new List<byte>();
            lastMiso = new List<byte>();
        }

        public void Reset()
        {
            // Deliberately keeps attachments and counters: a machine reset is a firmware event,
            // the wire is still there.
        }

        // -- attachment ---------------------------------------------------------------------

        public void AttachMaster(IPeripheralContainer<ISPIPeripheral, NullRegistrationPoint> controller)
        {
            if(master != null)
            {
                throw new RecoverableException("A master is already attached");
            }
            master = controller;
            masterMachine = ((IPeripheral)controller).GetMachine();
            WatchMasterReset();
            controller.Register(this, NullRegistrationPoint.Instance);
        }

        // Attach as one chip-select slot of a SPIBusMux on the master's controller, for a bus
        // that carries other devices too (for example this link on slot 0 and a flash on slot 1).
        // The mux forwards the slot's chip-select level here, so no AttachChipSelect is needed.
        //     spilink AttachMasterSlot sysbus.usart2.spibus 0
        public void AttachMasterSlot(SPIBusMux mux, int slot)
        {
            if(master != null || masterMux != null)
            {
                throw new RecoverableException("A master is already attached");
            }
            masterMux = mux;
            masterMachine = ((IPeripheral)mux).GetMachine();
            WatchMasterReset();
            mux.AttachExternalSlave(this, slot);
            hasChipSelect = true;
            chipSelectDriven = true;
        }

        public void AttachSlave(ISPIPeripheral peripheral)
        {
            if(slave != null)
            {
                throw new RecoverableException("A slave is already attached");
            }
            slave = peripheral;
            slaveMachine = peripheral.GetMachine();
            WatchSlaveReset();
        }

        // The master's chip-select output pin (a GPIO port pin number). Its falling edge opens a
        // transaction, its rising edge closes one: FinishTransmission() reaches the slave and the
        // level is mirrored onto the slave's CS input pin if one is attached.
        public void AttachChipSelect(INumberedGPIOOutput port, int pinNumber)
        {
            if(!port.Connections.TryGetValue(pinNumber, out var gpio))
            {
                throw new RecoverableException($"{port.GetName()}@{pinNumber} is not a GPIO of that port");
            }
            gpio.Connect(this, ChipSelectInput);
            chipSelectSource = gpio;
            hasChipSelect = true;
        }

        public void AttachSlaveChipSelectPin(IGPIOReceiver receiver, int pinNumber)
        {
            slaveChipSelectReceiver = receiver;
            slaveChipSelectPin = pinNumber;
        }

        // The slave's "I have data" line: an open-drain output on the slave (low =
        // message queued) read by the master as a GPIO input with a falling-edge interrupt.
        // Both ends attach here; the level crosses machines the same way the bytes do.
        //     mach set "slave";   spilink AttachInterruptSource sysbus.gpioPort 38
        //     mach set "master";  spilink AttachInterruptSink sysbus.gpioPort 35
        public void AttachInterruptSource(INumberedGPIOOutput port, int pinNumber)
        {
            if(!port.Connections.TryGetValue(pinNumber, out var gpio))
            {
                throw new RecoverableException($"{port.GetName()}@{pinNumber} is not a GPIO of that port");
            }
            gpio.Connect(this, InterruptInput);
        }

        public void AttachInterruptSink(IGPIOReceiver receiver, int pinNumber)
        {
            masterInterruptReceiver = receiver;
            masterInterruptPin = pinNumber;
            // Deliver the current level so a sink attached after the slave booted is not stale.
            ForwardInterrupt(interruptLevel);
        }

        public bool InterruptAsserted => !interruptLevel;
        public ulong InterruptAssertions { get; private set; }

        private void ForwardInterrupt(bool level)
        {
            if(masterInterruptReceiver == null || masterMachine == null)
            {
                return;
            }
            var stamp = CurrentTimeStamp();
            masterMachine.HandleTimeDomainEvent<bool>(v => masterInterruptReceiver.OnGPIO(masterInterruptPin, v), level, stamp);
        }

        // -- master-facing ISPIPeripheral ----------------------------------------------------

        // Synchronous path, for controllers that do not implement the deferred protocol. This
        // calls into the slave machine from the master's thread; with SetGlobalSerialExecution
        // that is race-free but the slave sees the whole frame burst at one instant, so a slave
        // that needs its CPU between bytes (any firmware-driven slave) will underflow.
        public byte Transmit(byte data)
        {
            if(slave == null)
            {
                this.Log(LogLevel.Warning, "Transmit with no slave attached; returning 0xFF");
                return 0xFF;
            }
            if(!SlaveSelected)
            {
                UnselectedBytes++;
                return 0xFF;
            }
            if(!warnedSynchronous)
            {
                warnedSynchronous = true;
                this.Log(LogLevel.Warning, "The attached master does not support deferred transfers; "
                         + "bytes are delivered to the slave synchronously (no slave CPU time between bytes)");
            }
            var result = slave.Transmit(data);
            Record(data, result);
            return result;
        }

        public void TransmitDeferred(byte data, Action<byte> completion)
        {
            if(slave == null)
            {
                this.Log(LogLevel.Warning, "TransmitDeferred with no slave attached; answering 0xFF");
                completion(0xFF);
                return;
            }
            if(!SlaveSelected)
            {
                // The master is clocking the bus with our chip select high: the frame is for another
                // device on it (an SPI flash sharing the bus, for example),
                // or for nobody. A slave whose CS is high ignores its SCLK and leaves MISO
                // released; the master reads the pulled-up line, 0xFF.
                UnselectedBytes++;
                completion(0xFF);
                return;
            }
            var stamp = NextSlaveStamp();
            slaveMachine.HandleTimeDomainEvent<byte>(mosi =>
            {
                var miso = slave.Transmit(mosi);
                Record(mosi, miso);
                var back = CurrentTimeStamp();
                masterMachine.HandleTimeDomainEvent<byte>(completion, miso, back);
            }, data, stamp);
        }

        public void FinishTransmission()
        {
            if(slave == null)
            {
                return;
            }
            slaveMachine.HandleTimeDomainEvent<bool>(_ => slave.FinishTransmission(), true, NextSlaveStamp());
        }

        // -- chip select ---------------------------------------------------------------------

        public void OnGPIO(int number, bool value)
        {
            if(number == InterruptInput)
            {
                if(value != interruptLevel)
                {
                    interruptLevel = value;
                    if(!value)
                    {
                        InterruptAssertions++;
                    }
                    ForwardInterrupt(value);
                }
                return;
            }
            if(number != ChipSelectInput)
            {
                this.Log(LogLevel.Warning, "Unexpected GPIO input {0}", number);
                return;
            }
            SetChipSelect(value);
        }

        private void SetChipSelect(bool level)
        {
            // A GPIO port comes out of reset driving its pins low, and Renode delivers that as an
            // initial OnGPIO(false). On the board the CS line idles HIGH (pulled up) until the SPI
            // driver takes the pin as an output at 1, so the
            // slave is not selected before that. Treat the line as undriven
            // until it has been seen high once; otherwise traffic the master sends to another
            // device early in boot would be delivered to the slave and counted as a transaction.
            if(level)
            {
                chipSelectDriven = true;
            }
            else if(!chipSelectDriven)
            {
                return;
            }
            if(level == chipSelectLevel)
            {
                return;
            }
            chipSelectLevel = level;
            if(!level)
            {
                Transactions++;
                lock(lastMosi)
                {
                    lastMosi.Clear();
                    lastMiso.Clear();
                }
            }
            else
            {
                lock(lastMosi)
                {
                    if(lastMosi.Count > 0)
                    {
                        history.Enqueue($"#{Transactions} @{CurrentTimeStamp().TimeElapsed}  >{Hex(lastMosi)}  <{Hex(lastMiso)}");
                        while(history.Count > HistoryLimit)
                        {
                            history.Dequeue();
                        }
                    }
                }
            }
            if(slaveChipSelectReceiver != null && slaveMachine != null)
            {
                // Ordered against the data by the same watermark: the release must reach the slave
                // AFTER the last byte of the frame, and the assertion before the first.
                slaveMachine.HandleTimeDomainEvent<bool>(v => slaveChipSelectReceiver.OnGPIO(slaveChipSelectPin, v), level, NextSlaveStamp());
            }
            if(level)
            {
                FinishTransmission();
            }
        }

        // -- machine resets ------------------------------------------------------------------
        //
        // The wire is not reset with either MCU, but each MCU's GPIO port is, and Renode's GPIO
        // port comes out of reset driving every pin low and remembering every input as low. On
        // the board the lines are pulled up, so a reset MCU sees CS and INT HIGH until the other
        // side drives them. The link therefore re-drives those levels into a machine that resets;
        // otherwise a slave whose host warm-reboots would see CS asserted forever.

        private void WatchMasterReset()
        {
            if(masterMachine == null || masterResetWatched)
            {
                return;
            }
            masterResetWatched = true;
            masterMachine.MachineReset += _ =>
            {
                // The master's CS pin is back to reset-low: the line is undriven until the SPI
                // driver takes the pin as an output at 1 again, and any frame in flight is over.
                chipSelectDriven = false;
                if(!chipSelectLevel)
                {
                    this.Log(LogLevel.Info, "Master reset with CS asserted; releasing the slave");
                    chipSelectLevel = true;
                    if(slaveChipSelectReceiver != null && slaveMachine != null)
                    {
                        slaveMachine.HandleTimeDomainEvent<bool>(v => slaveChipSelectReceiver.OnGPIO(slaveChipSelectPin, v), true, NextSlaveStamp());
                    }
                    FinishTransmission();
                }
                // Its INT input was reset to low too; re-deliver what the slave is really driving.
                if(masterInterruptReceiver != null)
                {
                    masterInterruptReceiver.OnGPIO(masterInterruptPin, interruptLevel);
                }
            };
        }

        private void WatchSlaveReset()
        {
            if(slaveMachine == null || slaveResetWatched)
            {
                return;
            }
            slaveResetWatched = true;
            slaveMachine.MachineReset += _ =>
            {
                // The slave's CS input reset to low: give it the level the master holds.
                if(slaveChipSelectReceiver != null)
                {
                    slaveChipSelectReceiver.OnGPIO(slaveChipSelectPin, chipSelectLevel);
                }
                // Its open-drain INT output reset to low (driven), which the link forwarded to
                // the master as "I have data". The pulled-up line idles high until the slave's
                // firmware drives it; say so.
                if(!interruptLevel)
                {
                    interruptLevel = true;
                    ForwardInterrupt(true);
                }
            };
        }

        // -- scripted master -----------------------------------------------------------------

        // Drive the slave without a master machine: asserts CS, clocks the given bytes in one per
        // synced state (so the slave's CPU runs between them, as it would on silicon), then
        // releases CS. The MISO bytes land in LastMiso. Hex bytes, separated by spaces or not:
        //     spilink Inject "01 02 03 04"
        public void Inject(string hexBytes)
        {
            if(HasMaster)
            {
                throw new RecoverableException("Inject is for a link with no master attached");
            }
            if(slave == null)
            {
                throw new RecoverableException("No slave attached");
            }
            var bytes = ParseHex(hexBytes);
            if(bytes.Count == 0)
            {
                throw new RecoverableException("No bytes to send");
            }
            chipSelectDriven = true;
            SetChipSelect(false);
            injectQueue = new Queue<byte>(bytes);
            // Pace the transaction in the slave's own time: one byte per InjectByteIntervalMicroseconds,
            // each scheduled at a FUTURE timestamp of the slave's domain. Scheduling "now" from inside
            // the slave's sync phase runs in that same phase, which would deliver a whole frame and
            // the chip-select release before the slave's CPU executed a single instruction.
            ScheduleNextInjectedByte();
        }

        // Kept as an alias of ByteIntervalMicroseconds: the scripted master and a real master are
        // paced by the same rule, and scripts that set this still work.
        public uint InjectByteIntervalMicroseconds
        {
            get => ByteIntervalMicroseconds;
            set => ByteIntervalMicroseconds = value;
        }

        // Clock `count` idle frames (0xFF) into the slave to read out what it has queued - the
        // host does the same with its DEFAULT byte when the slave raises its INT line.
        public void Poll(int count)
        {
            if(count <= 0 || count > 4096)
            {
                throw new RecoverableException("count must be 1..4096");
            }
            Inject(string.Join(" ", Enumerable.Repeat("FF", count)));
        }

        private void ScheduleNextInjectedByte()
        {
            var stamp = NextSlaveStamp();
            slaveMachine.HandleTimeDomainEvent<bool>(_ =>
            {
                if(injectQueue == null || injectQueue.Count == 0)
                {
                    SetChipSelect(true);
                    injectQueue = null;
                    return;
                }
                var mosi = injectQueue.Dequeue();
                var miso = slave.Transmit(mosi);
                Record(mosi, miso);
                ScheduleNextInjectedByte();
            }, true, stamp);
        }

        // -- observation ---------------------------------------------------------------------

        public ulong BytesToSlave { get; private set; }
        public ulong BytesToMaster { get; private set; }
        public ulong Transactions { get; private set; }
        public bool ChipSelectAsserted => !chipSelectLevel;
        // Bytes the master clocked while our chip select was high - traffic for another device on
        // the same bus, answered with 0xFF and never shown to the slave.
        public ulong UnselectedBytes { get; private set; }

        // With no chip-select wire attached the link is a dedicated bus and every byte is ours.
        private bool SlaveSelected => !hasChipSelect || !chipSelectLevel;
        public bool HasMaster => master != null || masterMux != null;
        public bool HasSlave => slave != null;

        // The bytes of the current/last transaction, oldest first, capped at 512 each way.
        public string LastMosi { get { lock(lastMosi) { return Hex(lastMosi); } } }
        public string LastMiso { get { lock(lastMosi) { return Hex(lastMiso); } } }

        // The last HistoryLimit completed transactions (chip select released), one per line:
        // number, virtual time, master bytes (>), slave bytes (<).
        public string History { get { lock(lastMosi) { return string.Join("\n", history); } } }

        private void Record(byte mosi, byte miso)
        {
            BytesToSlave++;
            BytesToMaster++;
            lock(lastMosi)
            {
                if(lastMosi.Count < RecordLimit)
                {
                    lastMosi.Add(mosi);
                    lastMiso.Add(miso);
                }
            }
        }

        private static string Hex(List<byte> bytes)
        {
            var sb = new StringBuilder(bytes.Count * 3);
            foreach(var b in bytes)
            {
                sb.Append(b.ToString("X2")).Append(' ');
            }
            return sb.ToString().TrimEnd();
        }

        private static List<byte> ParseHex(string text)
        {
            var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ',').ToArray());
            if(clean.Length % 2 != 0)
            {
                throw new RecoverableException("Hex string must have an even number of digits");
            }
            var result = new List<byte>(clean.Length / 2);
            for(var i = 0; i < clean.Length; i += 2)
            {
                result.Add(Convert.ToByte(clean.Substring(i, 2), 16));
            }
            return result;
        }

        // ------------------------------------------------------------------------------------
        // Pacing. Every event this link delivers into the slave machine - each byte, and each
        // chip-select edge - is stamped at least ByteIntervalMicroseconds after the previous one,
        // and never in the past. Without it a master that clocks a frame back to back (handing
        // over the next byte from its TX-complete interrupt the instant the previous completes)
        // delivers the whole frame AND the chip-select release inside one synced state: the slave's
        // RX interrupt then runs once, reads one byte, and the CS edge ends the frame before the
        // rest of it has been read.
        // Silicon's 1 Mbit/s byte is 8 us; the default here is one 20 us quantum.
        // ------------------------------------------------------------------------------------
        // A FLOOR, not the spacing: the actual interval is this or one quantum of the slave's time
        // source, whichever is larger. Below a quantum the events land in the same synced state
        // again and the slave's CPU still does not run between them - which is the whole point -
        // so a script that sets a coarser quantum (100 us, say) is
        // paced by that automatically instead of silently losing bytes.
        public uint ByteIntervalMicroseconds { get; set; } = DefaultByteIntervalMicroseconds;

        // What the pacing actually works out to right now, for a script that wants to check.
        public ulong EffectiveByteIntervalMicroseconds => (ulong)ByteInterval().TotalMicroseconds;

        private TimeInterval ByteInterval()
        {
            var floor = TimeInterval.FromMicroseconds(ByteIntervalMicroseconds);
            var quantum = slaveMachine.LocalTimeSource.Quantum;
            return quantum > floor ? quantum : floor;
        }

        private TimeStamp NextSlaveStamp()
        {
            var now = slaveMachine.LocalTimeSource.ElapsedVirtualTime;
            var next = nextSlaveTime > now ? nextSlaveTime : now;
            next += ByteInterval();
            nextSlaveTime = next;
            return new TimeStamp(next, slaveMachine.LocalTimeSource.Domain);
        }

        private static TimeStamp CurrentTimeStamp()
        {
            if(!TimeDomainsManager.Instance.TryGetVirtualTimeStamp(out var vts))
            {
                vts = new TimeStamp(default(TimeInterval), EmulationManager.ExternalWorld);
            }
            return vts;
        }

        private IPeripheralContainer<ISPIPeripheral, NullRegistrationPoint> master;
        private IMachine masterMachine;
        private ISPIPeripheral slave;
        private IMachine slaveMachine;
        private IGPIO chipSelectSource;
        private SPIBusMux masterMux;
        private bool hasChipSelect;
        private IGPIOReceiver slaveChipSelectReceiver;
        private int slaveChipSelectPin;
        private bool chipSelectLevel = true;
        private bool chipSelectDriven;
        private bool masterResetWatched;
        private bool slaveResetWatched;
        private bool warnedSynchronous;
        private Queue<byte> injectQueue;
        private TimeInterval nextSlaveTime;
        private readonly List<byte> lastMosi;
        private readonly List<byte> lastMiso;
        private readonly Queue<string> history = new Queue<string>();
        private const int HistoryLimit = 32;

        private IGPIOReceiver masterInterruptReceiver;
        private int masterInterruptPin;
        private bool interruptLevel = true;

        private const uint DefaultByteIntervalMicroseconds = 20;
        private const int ChipSelectInput = 0;
        private const int InterruptInput = 1;
        private const int RecordLimit = 512;
    }
}
