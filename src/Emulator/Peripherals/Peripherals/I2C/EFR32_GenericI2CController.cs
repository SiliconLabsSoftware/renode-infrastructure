//
// Copyright (c) 2010-2023 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.I2C
{
    // This only implements I2C master operation
    public abstract class EFR32_GenericI2CController : SimpleContainer<II2CPeripheral>
    {
        public EFR32_GenericI2CController(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            txBuffer = new Queue<byte>();
            rxBuffer = new Queue<byte>();
            interruptsManager = new InterruptManager<Interrupt>(this);
        }

        public override void Reset()
        {
            busBusy = false; busHold = false; nacked = false; busState = BusState.Idle;
            currentAddress = 0;
            isWrite = false;
            waitingForAddressByte = false;
            txBuffer.Clear();
            rxBuffer.Clear();
            interruptsManager.Reset();
        }

        [IrqProvider]
        public GPIO IRQ { get; }

        protected DoubleWordRegister GenerateControlRegister() => new DoubleWordRegister(this)
            .WithFlag(0, out enableFlag, name: "EN")
            .WithTaggedFlag("SLAVE", 1)
            .WithFlag(2, out autoack, name: "AUTOACK")
            .WithTaggedFlag("AUTOSE", 3)
            .WithTaggedFlag("AUTOSN", 4)
            .WithTaggedFlag("ARBDIS", 5)
            .WithTaggedFlag("GCAMEN", 6)
            .WithTaggedFlag("TXBIL", 7)
            .WithTag("CLHR", 8, 2)
            .WithReservedBits(10, 2)
            .WithTag("BITO", 12, 2)
            .WithReservedBits(14, 1)
            .WithTaggedFlag("GIBITO", 15)
            .WithTag("CLTO", 16, 3);

        protected DoubleWordRegister GenerateCommandRegister() => new DoubleWordRegister(this)
            .WithValueField(0, 8, FieldMode.Write, name: "COMMAND", writeCallback: (_, v) => HandleCommand((Command)v));

        // Firmware that drives the controller directly rather than through emlib polls STATE, for
        // example CMD.START, TXDATA=address, wait for BUSHOLD, then test for ADDRACK and NACKED.
        // The fields follow the reference manual's bus state machine: BUSHOLD is set whenever
        // the master is waiting for software (address byte, ACK/NACK seen, data received), STATE
        // walks IDLE/START/ADDR/ADDRACK/DATA/DATAACK, and NACKED latches the last address or data
        // acknowledge until the next START/STOP.
        protected DoubleWordRegister GenerateStateRegister() => new DoubleWordRegister(this)
            .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => busBusy, name: "BUSY")
            .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => busBusy, name: "MASTER")
            .WithFlag(2, FieldMode.Read, valueProviderCallback: _ => busBusy && isWrite, name: "TRANSMITTER")
            .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => nacked, name: "NACKED")
            .WithFlag(4, FieldMode.Read, valueProviderCallback: _ => busHold, name: "BUSHOLD")
            .WithValueField(5, 3, FieldMode.Read, valueProviderCallback: _ => (uint)busState, name: "STATE")
            .WithReservedBits(8, 24);

        protected DoubleWordRegister GenerateStatusRegister() => new DoubleWordRegister(this)
            .WithTaggedFlag("PSTART", 0)
            .WithTaggedFlag("PSTOP", 1)
            .WithTaggedFlag("PACK", 2)
            .WithTaggedFlag("PNACK", 3)
            .WithTaggedFlag("PCONT", 4)
            .WithTaggedFlag("PABORT", 5)
            .WithTaggedFlag("TXC", 6)
            .WithFlag(7, mode: FieldMode.Read, valueProviderCallback: (_) => !txBuffer.Any(), name: "TXBL")
            .WithFlag(8, mode: FieldMode.Read, valueProviderCallback: (_) => rxBuffer.Any(), name: "RXDATAV")
            .WithFlag(9, mode: FieldMode.Read, valueProviderCallback: (_) => rxBuffer.Count >= MaxRxBufferBytes, name: "RXFULL");

        protected DoubleWordRegister GenerateClockDivisionRegister() => new DoubleWordRegister(this)
            .WithTag("DIV", 0, 9)
            .WithReservedBits(9, 23);

        protected DoubleWordRegister GenerateSlaveAddressRegister() => new DoubleWordRegister(this)
            .WithReservedBits(0, 1)
            .WithTag("ADDR", 1, 7)
            .WithReservedBits(8, 24);

        protected DoubleWordRegister GenerateSlaveAddressMaskRegister() => new DoubleWordRegister(this)
            .WithReservedBits(0, 1)
            .WithTag("SADDRMASK", 1, 7)
            .WithReservedBits(8, 24);

        protected DoubleWordRegister GenerateReceiveBufferDataRegister() => new DoubleWordRegister(this)
            .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ => ReadRxByte(), name: "RXDATA");

        protected DoubleWordRegister GenerateReceiveBufferDoubleDataRegister() => new DoubleWordRegister(this)
            .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ => ReadRxByte(), name: "RXDATA0")
            .WithValueField(8, 8, FieldMode.Read, valueProviderCallback: _ => ReadRxByte(), name: "RXDATA1")
            .WithReservedBits(16, 16);

        protected DoubleWordRegister GenerateReceiveBufferDataPeekRegister() => new DoubleWordRegister(this)
            .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ => PeekRxByte(0), name: "RXDATAP");

        protected DoubleWordRegister GenerateReceiveBufferDoubleDataPeekRegister() => new DoubleWordRegister(this)
            .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ => PeekRxByte(0), name: "RXDATAP0")
            .WithValueField(8, 8, FieldMode.Read, valueProviderCallback: _ => PeekRxByte(1), name: "RXDATAP1")
            .WithReservedBits(16, 16);

        protected DoubleWordRegister GenerateTransmitBufferDataRegister() => new DoubleWordRegister(this)
            .WithValueField(0, 8, FieldMode.Write, writeCallback: (_, v) => LoadTxData((byte)v), name: "TXDATA");

        protected DoubleWordRegister GenerateTransmitBufferDoubleDataRegister() => new DoubleWordRegister(this)
            .WithValueField(0, 8, FieldMode.Write, writeCallback: (_, v) => LoadTxData((byte)v), name: "TXDATA0")
            .WithValueField(8, 8, FieldMode.Write, writeCallback: (_, v) => LoadTxData((byte)v), name: "TXDATA1")
            .WithReservedBits(16, 16);

        protected DoubleWordRegister GenerateInterruptFlagClearRegister() => interruptsManager.GetInterruptClearRegister<DoubleWordRegister>();

        protected DoubleWordRegister GenerateInterruptFlagSetRegister() => interruptsManager.GetInterruptSetRegister<DoubleWordRegister>();

        protected DoubleWordRegister GenerateInterruptEnableRegister() => interruptsManager.GetInterruptEnableRegister<DoubleWordRegister>();

        protected DoubleWordRegister GenerateInterruptFlagRegister() => interruptsManager.GetRegister<DoubleWordRegister>(
                valueProviderCallback: (irq, __) => interruptsManager.IsSet(irq) && interruptsManager.IsEnabled(interrupt: irq),
                writeCallback: (irq, prevValue, newValue) =>
                {
                    if(newValue)
                    {
                        interruptsManager.ClearInterrupt(irq);
                    }
                }
            );

        // Series 2 (xG2x) semantics for the interrupt flag register: IF reads the RAW flags -- IEN
        // gates only the IRQ line -- and a write of 1 SETS a flag; clearing is done through the
        // IF_CLR alias. The Series-1 generator above returns flags masked by IEN and clears on
        // write; emlib's polling-mode I2C_Transfer() never enables interrupts, so it needs the raw
        // flags.
        protected DoubleWordRegister GenerateRawInterruptFlagRegister() => interruptsManager.GetRegister<DoubleWordRegister>(
                valueProviderCallback: (irq, __) => interruptsManager.IsSet(irq),
                writeCallback: (irq, prevValue, newValue) =>
                {
                    if(newValue && !IsNotSettable(irq))
                    {
                        interruptsManager.SetInterrupt(irq);
                    }
                }
            );

        protected void SetInterruptFlags(uint mask)
        {
            foreach(var irq in Enum.GetValues(typeof(Interrupt)).Cast<Interrupt>())
            {
                if(((mask >> (int)irq) & 1) != 0 && !IsNotSettable(irq))
                {
                    interruptsManager.SetInterrupt(irq);
                }
            }
        }

        protected void ClearInterruptFlags(uint mask)
        {
            foreach(var irq in Enum.GetValues(typeof(Interrupt)).Cast<Interrupt>())
            {
                if(((mask >> (int)irq) & 1) != 0 && !IsNotSettable(irq))
                {
                    interruptsManager.ClearInterrupt(irq);
                }
            }
        }

        // TXBL and RXDATAV are derived from buffer state ([NotSettable]); software cannot set or
        // clear them.
        private static bool IsNotSettable(Interrupt irq) => irq == Interrupt.TransmitBufferLevel || irq == Interrupt.ReceiveDataValid;

        private byte ReadRxByte()
        {
            if(rxBuffer.TryDequeue(out var result))
            {
                if(autoack.Value)
                {
                    // When autoack is enabled, refill the Rx buffer to simulate an ACK being sent and
                    // a new byte being received
                    LoadRxData(1);
                }
                else
                {
                    // RXDATAV is automatically cleared when reading the receive buffer
                    // Check if any bytes remain and update the flag appropriately
                    interruptsManager.SetInterrupt(Interrupt.ReceiveDataValid, rxBuffer.Any());
                }
                return result;
            }

            // The buffer does not contain any data, the behavior is undefined.
            // Raise the RXUF IRQ to signal the condition
            interruptsManager.SetInterrupt(Interrupt.ReceiveBufferUnderflow);
            return 0x0;
        }

        private byte PeekRxByte(int index)
        {
            if(index < rxBuffer.Count)
            {
                return rxBuffer.ElementAt(index);
            }

            // The buffer does not contain any data, the behavior is undefined.
            // RXUF IRQ is _not_ raised in this case.
            return 0x0;
        }

        private void HandleCommand(Command command)
        {
            foreach(var c in Enum.GetValues(typeof(Command)).Cast<Command>().Where(x => command.HasFlag(x)))
            {
                switch(c)
                {
                case Command.SendStartCondition:
                    OnStartCommand();
                    break;

                case Command.SendStopCondition:
                    OnStopCommand();
                    break;

                case Command.SendAck:
                    OnAckCommand();
                    break;

                case Command.SendNotAck:
                    this.Log(LogLevel.Noisy, "Send NACK");
                    interruptsManager.SetInterrupt(Interrupt.MasterStopCondition);
                    break;

                case Command.AbortTransmission:
                    // Abort: release the bus, drop anything queued, back to idle. emlib issues this
                    // when a transfer times out or errors, so no state may carry into the next one.
                    this.Log(LogLevel.Noisy, "Abort transmission");
                    txBuffer.Clear();
                    isWrite = false;
                    waitingForAddressByte = false;
                    targetPeripheral?.FinishTransmission();
                    targetPeripheral = null;
                    busBusy = false; busHold = false; nacked = false; busState = BusState.Idle;
                    interruptsManager.SetInterrupt(Interrupt.TransmitBufferLevel);
                    break;
                case Command.ContinueTransmission:
                case Command.ClearPendingCommands:
                    // Nothing pending is ever modelled; accept silently.
                    break;
                case Command.ClearTransmitBufferAndShiftRegister:
                    this.Log(LogLevel.Noisy, "Cleared TX buffer");
                    txBuffer.Clear();
                    break;

                default:
                    this.Log(LogLevel.Warning, "Received unsupported command: {0}", c);
                    break;
                }
            }
        }

        private void OnStartCommand()
        {
            // Note: This does not detect repeated starts after a read request
            // We have to specifically handle repeated starts after a write as the data
            // is written to the device only after a stop command is sent.
            if(isWrite)
            {
                this.Log(LogLevel.Noisy, "Repeated start command");
                interruptsManager.SetInterrupt(Interrupt.RepeatedStartCondition);
                OnStopCommand();
            }

            interruptsManager.SetInterrupt(Interrupt.StartCondition);
            busBusy = true;
            nacked = false;
            switch(txBuffer.Count)
            {
            case 0:
                // the first byte contains device address and R/W flag; we have to wait for it
                waitingForAddressByte = true;
                busState = BusState.Start;
                busHold = true;
                interruptsManager.SetInterrupt(Interrupt.BusHold);
                // TODO: here we should also set I2Cn_STATE to 0x57 according to p.442
                break;
            case 1:
                // there is a byte address waiting already in the buffer
                HandleAddressByte();
                break;
            default:
                // there is a byte address waiting already in the buffer, along with some data
                HandleAddressByte();
                // Clear TXBL, as some data is already present in the buffer
                interruptsManager.ClearInterrupt(interrupt: Interrupt.TransmitBufferLevel);
                HandleDataByte();
                break;
            }
        }

        private void OnStopCommand()
        {
            interruptsManager.SetInterrupt(Interrupt.MasterStopCondition);
            busBusy = false;
            busHold = false;
            nacked = false;
            busState = BusState.Idle;
            waitingForAddressByte = false;
            if(!isWrite)
            {
                // A STOP ends the transaction for the slave too: II2CPeripheral implementations
                // reset their register-pointer state in FinishTransmission(), so the first data
                // byte of the next write is taken as a register address, not as more data.
                targetPeripheral?.FinishTransmission();
                return;
            }

            WriteToSlave(targetPeripheral, txBuffer);
            txBuffer.Clear();
            interruptsManager.SetInterrupt(Interrupt.TransmitBufferLevel);
            interruptsManager.SetInterrupt(Interrupt.TransferCompleted);
            isWrite = false;
            targetPeripheral?.FinishTransmission();
        }

        private void OnAckCommand()
        {
            // Sending an ACK implies a read operation is in progress
            if(isWrite)
            {
                return;
            }

            this.Log(LogLevel.Noisy, "ACK command");
            // Fetch another byte from the target
            LoadRxData(1);
        }

        private void LoadTxData(byte value)
        {
            // TXBL is cleared when new data is written to the transmit buffer
            interruptsManager.ClearInterrupt(Interrupt.TransmitBufferLevel);

            txBuffer.Enqueue(value);
            if(waitingForAddressByte)
            {
                HandleAddressByte();
            }
            else
            {
                HandleDataByte();
            }
        }

        private void HandleAddressByte()
        {
            waitingForAddressByte = false;

            currentAddress = txBuffer.Dequeue();
            isWrite = (currentAddress & 0x1) == 0;
            currentAddress >>= 1;

            // The address phase has completed either way, so the bus state advances to ADDRACK and
            // the master keeps holding the bus for software. Only `nacked` distinguishes the two
            // outcomes - and it is the ONLY thing that does, for firmware that reads STATE rather
            // than the interrupt flags: a probe of an absent address must read ADDRACK with
            // NACKED set.
            busState = BusState.AddressAck;
            busHold = true;

            // Try getting the peripheral that the transmission is targeting
            // If a peripheral at that address is not present, raise a NACK
            if(!TryGetByAddress(currentAddress, out targetPeripheral))
            {
                nacked = true;
                interruptsManager.SetInterrupt(Interrupt.NotAcknowledgeReceived);
                this.Log(LogLevel.Warning, "Trying to address non-existent I2C peripheral with address 0x{0:x}", currentAddress);
                return;
            }

            // Device exists, ACK the address byte
            nacked = false;
            interruptsManager.SetInterrupt(Interrupt.AcknowledgeReceived);

            // Immediately read data from the device if the transfer is a read
            if(!isWrite)
            {
                // If autoack is enabled, load two bytes instead to fill the Rx buffer
                LoadRxData(autoack.Value ? 2 : 1);
            }
        }

        private void HandleDataByte()
        {
            if(!isWrite)
            {
                return;
            }

            // The byte was already written to the Tx buffer; only need to update ACK state now
            // If there is a device connected at the current address, then simulate an ACK.
            // Otherwise, raise a NACK
            busState = BusState.DataAck;
            busHold = true;
            if(targetPeripheral == null)
            {
                nacked = true;
                interruptsManager.SetInterrupt(Interrupt.NotAcknowledgeReceived);
            }
            else
            {
                nacked = false;
                interruptsManager.SetInterrupt(Interrupt.AcknowledgeReceived);
            }
        }

        private void LoadRxData(int count)
        {
            var spaceLeft = Math.Max(0, MaxRxBufferBytes - rxBuffer.Count);
            if(spaceLeft == 0)
            {
                this.Log(LogLevel.Warning, "Requesting read but no space left in RX buffer");
                return;
            }

            if(count > spaceLeft)
            {
                this.Log(LogLevel.Warning, "Requesting read of {0} bytes, but have space only for {1} bytes", count, spaceLeft);
            }

            var bytesToRead = Math.Min(spaceLeft, count);
            ReadFromSlave(targetPeripheral, rxBuffer, bytesToRead);
            interruptsManager.SetInterrupt(Interrupt.ReceiveDataValid, rxBuffer.Any());
        }

        private void ReadFromSlave(II2CPeripheral slave, Queue<byte> buffer, int count)
        {
            if(slave == null)
            {
                this.Log(LogLevel.Warning, "Trying to read from nonexisting slave with address \"{0}\"", currentAddress);
                return;
            }

            var rxArray = slave.Read(count);
            buffer.EnqueueRange(rxArray, count);

            this.Log(LogLevel.Noisy, "Devices returned {0} bytes of data.", rxArray.Length);
        }

        private void WriteToSlave(II2CPeripheral slave, IEnumerable<byte> data)
        {
            if(slave == null)
            {
                this.Log(LogLevel.Warning, "Trying to write to nonexisting slave with address \"{0}\"", currentAddress);
                return;
            }

            slave.Write(data.ToArray());
        }

        private bool busBusy;
        private bool busHold;
        private bool nacked;
        private BusState busState;

        private enum BusState
        {
            Idle = 0,
            Wait = 1,
            Start = 2,
            Address = 3,
            AddressAck = 4,
            Data = 5,
            DataAck = 6,
        }

        private int currentAddress;
        private bool isWrite;
        private bool waitingForAddressByte;
        private IFlagRegisterField enableFlag;
        private IFlagRegisterField autoack;
        private II2CPeripheral targetPeripheral;
        private readonly Queue<byte> txBuffer;
        private readonly Queue<byte> rxBuffer;
        private readonly InterruptManager<Interrupt> interruptsManager;
        private const int MaxRxBufferBytes = 2;

        private enum Registers
        {
            Control = 0x00,
            Command = 0x04,
            State = 0x08,
            Status = 0x0C,
            ClockDivision = 0x10,
            SlaveAddress = 0x14,
            SlaveAddressMask = 0x18,
            ReceiveBufferData = 0x1C,
            ReceiveBufferDoubleData = 0x20,
            ReceiveBufferDataPeek = 0x24,
            ReceiveBufferDoubleDataPeek = 0x28,
            TransmitBufferData = 0x2C,
            TransmitBufferDoubleData = 0x30,
            InterruptFlag = 0x34,
            InterruptFlagSet = 0x38,
            InterruptFlagClear = 0x3C,
            InterruptEnable = 0x40,
            IORoutingPinEnable = 0x44,
            IORoutingLocation = 0x48
        }

        [Flags]
        private enum Command
        {
            SendStartCondition = 0x01,
            SendStopCondition = 0x02,
            SendAck = 0x04,
            SendNotAck = 0x08,
            ContinueTransmission = 0x10,
            AbortTransmission = 0x20,
            ClearTransmitBufferAndShiftRegister = 0x40,
            ClearPendingCommands = 0x80
        }

        private enum Interrupt
        {
            StartCondition = 0x00,
            RepeatedStartCondition = 0x01,
            Address = 0x02,
            TransferCompleted = 0x03,
            [NotSettable]
            TransmitBufferLevel = 0x04,
            [NotSettable]
            ReceiveDataValid = 0x05,
            AcknowledgeReceived = 0x06,
            NotAcknowledgeReceived = 0x07,
            MasterStopCondition = 0x08,
            ArbitrationLost = 0x09,
            BusError = 0x0A,
            BusHold = 0x0B,
            TransmitBufferOverflow = 0x0C,
            ReceiveBufferUnderflow = 0x0D,
            BusIdleTimeout = 0x0E,
            ClockLowTimeout = 0x0F,
            SlaveStopCondition = 0x10,
            ReceiveBufferFull = 0x11,
            ClockLowError = 0x12
        }
    }
}