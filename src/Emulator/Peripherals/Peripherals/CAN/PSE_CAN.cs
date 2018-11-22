﻿﻿//
// Copyright (c) 2010-2018 Antmicro
//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.CAN;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.CAN
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class PSE_CAN : IKnownSize, IDoubleWordPeripheral, ICAN
    {
        public PSE_CAN()
        {
            IRQ = new GPIO();
            InitializeBuffers();

            var registersMap = new Dictionary<long, DoubleWordRegister>
            {
                {
                    (long)ControllerRegisters.InterruptEnable,
                    new DoubleWordRegister(this)
                        .WithFlag(0, out interruptsEnabled, name: "Int_enbl")
                        .WithReservedBits(1, 1)
                        .WithTag("arb_loss_enbl", 2, 1)
                        .WithTag("ovr_load_enbl", 3, 1)
                        .WithTag("bit_err_enbl", 4, 1)
                        .WithTag("stuff_err_enbl", 5, 1)
                        .WithTag("ack_err_enbl", 6, 1)
                        .WithTag("form_err_enbl", 7, 1)
                        .WithTag("crc_err_enbl", 8, 1)
                        .WithTag("bus_off_enbl", 9, 1)
                        .WithFlag(10, out rxMessageLossEnabled, name: "rx_msg_loss")
                        .WithFlag(11, out txInterruptsEnabled, name: "tx_msg_enbl")
                        .WithFlag(12, out rxInterruptsEnabled, name: "rx_msg_enbl")
                        .WithTag("rtr_msg_enbl", 13, 1)
                        .WithTag("stuck_at_0_enbl", 14, 1)
                        .WithTag("sst_failure_enbl", 15, 1)
                        .WithReservedBits(16, 15)
                },
                {
                    (long)ControllerRegisters.InterruptStatus,
                    new DoubleWordRegister(this)
                        .WithReservedBits(0, 2)
                        .WithTag("ARB_LOSS", 2, 1)
                        .WithTag("OVR_LOAD", 3, 1)
                        .WithTag("BIT_ERR", 4, 1)
                        .WithTag("STUFF_ERR", 5, 1)
                        .WithTag("ACK_ERR", 6, 1)
                        .WithTag("FORM_ERR", 7, 1)
                        .WithTag("CRC_ERR", 8, 1)
                        .WithTag("BUS_OFF", 9, 1)
                        .WithFlag(10, out rxMessageLossStatus, FieldMode.WriteOneToClear | FieldMode.Read, name: "RX_MSG_LOSS")
                        .WithFlag(11, out txMessageInterruptsStatus, FieldMode.WriteOneToClear | FieldMode.Read, name: "TX_MSG")
                        .WithFlag(12, out rxMessageInterruptsStatus, FieldMode.WriteOneToClear | FieldMode.Read, name: "RX_MSG")
                        .WithTag("RTR_MSG", 13, 1)
                        .WithTag("STUCK_AT_0", 14, 1)
                        .WithTag("SST_FAILURE", 15, 1)
                        .WithReservedBits(16, 15)
                        .WithWriteCallback((_, __) => UpdateInterrupts())
                },
                {
                    (long)ControllerRegisters.Config,
                    new DoubleWordRegister(this)
                        .WithTag("EDGE_MODE", 0, 1)
                        .WithTag("SAMPLING_MODE", 1, 1)
                        .WithTag("CFG_SJW", 2, 2)
                        .WithTag("AUTO_RESTART", 4, 1)
                        .WithTag("CFG_TSEG2", 5, 3)
                        .WithTag("CFG_TSEG1", 8, 4)
                        .WithTag("CFG_ARBITER", 12, 1)
                        .WithFlag(13, out swapEndian, name: "SWAP_ENDIAN")
                        .WithTag("ECR_MODE", 14, 1)
                        .WithReservedBits(15, 1)
                        .WithTag("CFG_BITRATE", 16, 15)
                },
                {
                    (long)ControllerRegisters.Command,
                    new DoubleWordRegister(this)
                        .WithFlag(0, out runStopMode, name: "RunStopMode")
                        .WithTag("ListenOnlyMode", 1, 1)
                        .WithTag("LoopbackTestMode", 2, 1)
                        .WithTag("SRAMTestMode", 3, 1)
                        .WithReservedBits(4, 12)
                        .WithTag("Revision_Control", 16, 15)
                },
                {
                    (long)ControllerRegisters.TransmitBufferStatus,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 32, FieldMode.Read,
                            valueProviderCallback: _ => BitHelper.GetValueFromBitsArray(txMessageBuffers.Select(x => x.IsRequestPending)),
                            name: "TX_STATUS")
                },
                {
                    (long)ControllerRegisters.ReceiveBufferStatus,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 32, FieldMode.Read,
                            valueProviderCallback: _ => BitHelper.GetValueFromBitsArray(rxMessageBuffers.Select(x => x.IsMessageAvailable)),
                            name: "RX_STATUS")
                }
            };

            for(int i = 0; i < BufferCount; ++i)
            {
                var index = i;

                // TX buffer registers
                registersMap.Add(
                    (long)ControllerRegisters.TransmitMessageControlCommand + shiftBetweenTxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithFlag(0, FieldMode.Set | FieldMode.Read,
                            changeCallback: (_, val) =>
                            {
                                txMessageBuffers[index].IsRequestPending = true;
                                RequestSendingMessage(txMessageBuffers[index]);
                            },
                            valueProviderCallback: _ => txMessageBuffers[index].IsRequestPending,
                            name: "TxReq")
                        .WithTag("TxAbort", 1, 1)
                        .WithFlag(2,
                            writeCallback: (_, val) => txMessageBuffers[index].InterruptEnable = val,
                            valueProviderCallback: _ => txMessageBuffers[index].InterruptEnable,
                            name: "TxIntEbl")
                        .WithTag("WPN", 3, 1)
                        .WithValueField(16, 4,
                            writeCallback: (_, val) => txMessageBuffers[index].DataLengthCode = val,
                            valueProviderCallback: _ => txMessageBuffers[index].DataLengthCode,
                            name: "DLC")
                        .WithTag("IDE", 20, 1)
                        .WithTag("RTR", 21, 1)
                        .WithReservedBits(22, 1)
                        .WithTag("WPN", 23, 1)
                );
                registersMap.Add(
                    (long)ControllerRegisters.TransmitMessageID + shiftBetweenTxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithValueField(3, 29,
                            writeCallback: (_, val) => txMessageBuffers[index].MessageId = val,
                            valueProviderCallback: _ => txMessageBuffers[index].MessageId,
                            name: "ID")
                );
                registersMap.Add(
                    (long)ControllerRegisters.TransmitMessageDataHigh + shiftBetweenTxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 32, FieldMode.Write,
                            writeCallback: (_, val) => SetData(txMessageBuffers[index], val, Offset.High),
                            name: $"TX_MSG{index}_DATA_HIGH")
                );
                registersMap.Add(
                    (long)ControllerRegisters.TransmitMessageDataLow + shiftBetweenTxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 32, FieldMode.Write,
                            writeCallback: (_, val) => SetData(txMessageBuffers[index], val, Offset.Low),
                            name: $"TX_MSG{index}_DATA_LOW")
                );

                // RX buffer registers
                registersMap.Add(
                    (long)ControllerRegisters.ReceiveMessageControlCommand + shiftBetweenRxRegisters * index,
                    new DoubleWordRegister(this)
                        // The docs states that on write: "Acknowledges receipt of new message or transmission of RTR auto-reply message (...)"
                        // but as we don't have RTR implemented, this will be RO for now
                        .WithFlag(0,
                            writeCallback: (_, val) => rxMessageBuffers[index].IsMessageAvailable &= !val,
                            valueProviderCallback: _ => rxMessageBuffers[index].IsMessageAvailable,
                            name: "MsgAv")
                        .WithTag("RTRP", 1, 1)
                        .WithTag("RTRabort", 2, 1)
                        .WithFlag(3,
                            writeCallback: (_, val) => rxMessageBuffers[index].Enabled = val,
                            valueProviderCallback: _ => rxMessageBuffers[index].Enabled,
                            name: "RxBufferEbl")
                        .WithFlag(4,
                            writeCallback: (_, val) => rxMessageBuffers[index].IsAutoreplyEnabled = val,
                            valueProviderCallback: _ => rxMessageBuffers[index].IsAutoreplyEnabled,
                            name: "RTRreply")
                        .WithFlag(5,
                            writeCallback: (_, val) => rxMessageBuffers[index].InterruptEnable = val,
                            valueProviderCallback: _ => rxMessageBuffers[index].InterruptEnable,
                            name: "RxIntEbl")
                        .WithFlag(6,
                            writeCallback: (_, val) => rxMessageBuffers[index].IsLinked = val,
                            valueProviderCallback: _ => rxMessageBuffers[index].IsLinked,
                            name: "LF")
                        .WithFlag(7, name: "WPNL")
                        .WithValueField(16, 4,
                            writeCallback: (_, val) => rxMessageBuffers[index].DataLengthCode = val,
                            valueProviderCallback: _ => rxMessageBuffers[index].DataLengthCode,
                            name: "DLC")
                        .WithTag("IDE", 20, 1)
                        .WithTag("RTR", 21, 1)
                        .WithFlag(23, name: "WPNH")
                );
                registersMap.Add(
                    (long)ControllerRegisters.ReceiveMessageID + shiftBetweenRxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithValueField(3, 29,
                            writeCallback: (_, val) => rxMessageBuffers[index].MessageId = val,
                            valueProviderCallback: _ => rxMessageBuffers[index].MessageId,
                            name: "ID")
                );
                registersMap.Add(
                    (long)ControllerRegisters.ReceiveMessageDataHigh + shiftBetweenRxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 32, FieldMode.Read,
                            valueProviderCallback: _ => GetDataHigh(rxMessageBuffers[index], swapEndian.Value),
                            name: $"RX_MSG{index}_DATA_HIGH")
                );
                registersMap.Add(
                    (long)ControllerRegisters.ReceiveMessageDataLow + shiftBetweenRxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 32, FieldMode.Read,
                            valueProviderCallback: _ => GetDataLow(rxMessageBuffers[index], swapEndian.Value),
                            name: $"RX_MSG{index}_DATA_LOW")
                );
                registersMap.Add(
                    (long)ControllerRegisters.ReceiveMessageAcceptanceMaskRegister + shiftBetweenRxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithReservedBits(0, 1)
                        .WithTag("RTR", 1, 1)
                        .WithTag("IDE", 2, 1)
                        .WithValueField(3, 29,
                            writeCallback: (_, val) => rxMessageBuffers[index].AcceptanceMaskId = val,
                            valueProviderCallback: (val) => rxMessageBuffers[index].AcceptanceMaskId,
                            name: "Identifier")
                );
                registersMap.Add(
                    (long)ControllerRegisters.ReceiveMessageAcceptanceCodeRegister + shiftBetweenRxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithReservedBits(0, 1)
                        .WithTag("RTR", 1, 1)
                        .WithTag("IDE", 2, 1)
                        .WithTag("Identifier", 3, 29)
                );
                registersMap.Add(
                    (long)ControllerRegisters.ReceiveMessageAcceptanceMaskRegisterData + shiftBetweenRxRegisters * index,
                    new DoubleWordRegister(this)
                    .WithValueField(0, 32,
                        writeCallback: (_, val) => rxMessageBuffers[index].AcceptanceMask = val,
                        valueProviderCallback: (val) => rxMessageBuffers[index].AcceptanceMask,
                        name: $"RX_MSG{index}_AMR_DATA")
                );
                registersMap.Add(
                    (long)ControllerRegisters.ReceiveMessageAcceptanceCodeRegisterData + shiftBetweenRxRegisters * index,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 32,
                            writeCallback: (_, val) => rxMessageBuffers[index].AcceptanceCode = val,
                            valueProviderCallback: (val) => rxMessageBuffers[index].AcceptanceCode,
                            name: $"RX_MSG{index}_ACR_DATA")
                );
            }

            registers = new DoubleWordRegisterCollection(this, registersMap);
            UpdateInterrupts();
        }

        public void OnFrameReceived(CANMessageFrame message)
        {
            var wasMessageSet = false;
            var buffer = rxMessageBuffers.FirstOrDefault(x => x.CanReceiveMessage(message, swapEndian.Value));
            if(buffer != null)
            {
                wasMessageSet = buffer.TrySetMessage(message);
                if(!wasMessageSet && buffer.IsLinked)
                {
                    for(var index = buffer.BufferId + 1; index < BufferCount; index++)
                    {
                        if(rxMessageBuffers[index].AcceptanceMask == buffer.AcceptanceMask
                            && rxMessageBuffers[index].AcceptanceCode == buffer.AcceptanceCode)
                        {
                            wasMessageSet = rxMessageBuffers[index].TrySetMessage(message);
                            if(wasMessageSet || !rxMessageBuffers[index].IsLinked)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            if(wasMessageSet)
            {
                rxMessageInterruptsStatus.Value = true;
            }
            else
            {
                this.Log(LogLevel.Warning, "Could not find any empty mailbox for message: {0}", message);
                rxMessageLossStatus.Value = true;
            }
            UpdateInterrupts();
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public void Reset()
        {
            registers.Reset();
            InitializeBuffers();
            UpdateInterrupts();
        }

        public long Size => 0x1000;
        public GPIO IRQ { get; private set; }
        public event Action<CANMessageFrame> FrameSent;

        private void InitializeBuffers()
        {
            txMessageBuffers = new TxMessageBuffer[BufferCount];
            rxMessageBuffers = new RxMessageBuffer[BufferCount];
            for(uint i = 0; i < BufferCount; ++i)
            {
                txMessageBuffers[i] = new TxMessageBuffer(this, i);
                rxMessageBuffers[i] = new RxMessageBuffer(this, i);
            }
        }

        private void RequestSendingMessage(TxMessageBuffer buffer)
        {
            var message = buffer.GetMessageFromBuffer(swapEndian.Value);
            if(message == null)
            {
                this.Log(LogLevel.Error, "No message in mailbox.");
                return;
            }
            FrameSent?.Invoke(message);
            this.Log(LogLevel.Info, "Message sent: {0}.", message);
            txMessageInterruptsStatus.Value = true;
            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            var rxInterrupt = rxInterruptsEnabled.Value && rxMessageBuffers.Any(x => x.IsMessageAvailable && x.InterruptEnable);
            var txInterrupt = txInterruptsEnabled.Value && txMessageBuffers.Any(x => x.IsRequestPending && x.InterruptEnable);

            var configInterrupt = (txInterruptsEnabled.Value && txMessageInterruptsStatus.Value)
                || (rxInterruptsEnabled.Value && rxMessageInterruptsStatus.Value)
                || (rxMessageLossEnabled.Value && rxMessageLossStatus.Value);

            IRQ.Set(interruptsEnabled.Value && (rxInterrupt || txInterrupt || configInterrupt));
        }

        private uint GetDataHigh(MessageBuffer buffer, bool isSwapped)
        {
            if(buffer.Message == null)
            {
                return 0;
            }
            return BitConverter.ToUInt32(isSwapped ? buffer.Message.Data.Reverse().ToArray() : buffer.Message.Data, 4);
        }

        private uint GetDataLow(MessageBuffer buffer, bool isSwapped)
        {
            if(buffer.Message == null)
            {
                return 0;
            }
            return BitConverter.ToUInt32(isSwapped ? buffer.Message.Data.Reverse().Take(4).ToArray() : buffer.Message.Data.Take(4).ToArray(), 0);
        }

        private void SetData(MessageBuffer buffer, uint registerValue, Offset offset)
        {
            if(buffer.Message == null)
            {
                buffer.Message = new CANMessageFrame(0, new byte[8]);
            }
            var bits = BitHelper.GetBits(registerValue);
            for(var i = 0; i < 4; ++i)
            {
                buffer.Message.Data[i + (int)offset] = (byte)BitHelper.GetValueFromBitsArray(bits.Skip(i * 8).Take(8));
            }
        }

        private TxMessageBuffer[] txMessageBuffers;
        private RxMessageBuffer[] rxMessageBuffers;
        private IFlagRegisterField interruptsEnabled;
        private IFlagRegisterField rxMessageLossEnabled;
        private IFlagRegisterField txInterruptsEnabled;
        private IFlagRegisterField rxInterruptsEnabled;
        private IFlagRegisterField rxMessageLossStatus;
        private IFlagRegisterField txMessageInterruptsStatus;
        private IFlagRegisterField rxMessageInterruptsStatus;
        private IFlagRegisterField swapEndian;
        private IFlagRegisterField runStopMode;
        
        private readonly DoubleWordRegisterCollection registers;
        
        private const int BufferCount = 32;
        private const int shiftBetweenRxRegisters = 0x20;
        private const int shiftBetweenTxRegisters = 0x10;

        private abstract class MessageBuffer
        {
            protected MessageBuffer(PSE_CAN parent, uint id)
            {
                this.parent = parent;
                BufferId = id;
            }

            public uint BufferId { get; private set; }
            public uint MessageId { get; set; }
            public CANMessageFrame Message { get; set; }
            public bool InterruptEnable { get; set; }
            public uint DataLengthCode
            {
                get
                {
                    return dataLengthCode;
                }
                set
                {
                    if(value > MaxDataLength)
                    {
                        parent.Log(LogLevel.Warning, "Tried to set data length code to {0}, but it was truncated to {1}.", value, MaxDataLength);
                        dataLengthCode = MaxDataLength;
                    }
                    else
                    {
                        dataLengthCode = value;
                    }
                }
            }
            
            protected readonly PSE_CAN parent;

            private uint dataLengthCode;
            private const int MaxDataLength = 8;
        }

        private class TxMessageBuffer : MessageBuffer
        {
            public TxMessageBuffer(PSE_CAN parent, uint bufferId) : base(parent, bufferId)
            {
            }

            public CANMessageFrame GetMessageFromBuffer(bool isSwapped)
            {
                if(Message == null)
                {
                    return null;
                }
                var data = isSwapped
                    ? Message.Data.Take((int)DataLengthCode).ToArray()
                    : Message.Data.Reverse().Take((int)DataLengthCode).ToArray();
                IsRequestPending = false;
                Message = null;
                return new CANMessageFrame(MessageId, data);
            }

            public bool IsRequestPending { get; set; }
        }

        private class RxMessageBuffer : MessageBuffer
        {
            public RxMessageBuffer(PSE_CAN parent, uint bufferId) : base(parent, bufferId)
            {
            }

            public bool CanReceiveMessage(CANMessageFrame message, bool isSwapped)
            {
                if(!Enabled)
                {
                    return false;
                }
                // we take only 16 bits because AMR/ACR data registers use filters based on 2 first message bytes

                var idBits = BitHelper.GetBits(message.Id);
                var idBitComparison = BitHelper.GetBits(message.Id).Skip(BitsToVerify)
                        .Zip(BitHelper.GetBits(AcceptanceMaskId).Skip(BitsToVerify),
                             (idBit, comparisonBit) => idBit != comparisonBit);
                var hasIdFilteringPassed = !idBitComparison.Any(x => x == true);
                

                var dataVal = BitConverter.ToUInt32(isSwapped ? message.Data : message.Data.Reverse().ToArray(), (int)Offset.High);
                var dataBitComparison = BitHelper.GetBits(dataVal).Skip(BitsToVerify)
                        .Zip(BitHelper.GetBits(AcceptanceCode).Skip(BitsToVerify),
                             (dataBits, codeBits) => dataBits != codeBits);
                var result = BitHelper.GetBits(AcceptanceMask).Skip(BitsToVerify)
                        .Zip(dataBitComparison,
                             (maskBit, comparisonBit) => !maskBit && comparisonBit);
                var hasDataFilteringPassed = !result.Any(x => x == true);

                return hasIdFilteringPassed && hasDataFilteringPassed;
            }

            public bool TrySetMessage(CANMessageFrame message)
            {
                if(Message == null)
                {
                    Message = message;
                    parent.Log(LogLevel.Info, "Received message {0} in mailbox #{1}", message, BufferId);
                    IsMessageAvailable = true;
                    return true;
                }
                return false;
            }

            public bool Enabled { get; set; }
            public uint AcceptanceMask { get; set; }
            public uint AcceptanceMaskId { get; set; }
            public uint AcceptanceCode { get; set; }
            public bool IsLinked { get; set; }
            public bool IsAutoreplyEnabled { get; set; }
            public bool IsMessageAvailable { get; set; }

            private const int BitsToVerify = 16;
        }

        private enum Offset
        {
            High = 4,
            Low = 0
        }

        private enum ControllerRegisters
        {
            InterruptStatus = 0x000,
            InterruptEnable = 0x004,
            TransmitBufferStatus = 0x00C,
            ReceiveBufferStatus = 0x008,
            Command = 0x014,
            Config = 0x018,
            TransmitMessageControlCommand = 0x020,
            TransmitMessageID = 0x024,
            TransmitMessageDataHigh = 0x028,
            TransmitMessageDataLow = 0x02C,
            ReceiveMessageControlCommand = 0x220,
            ReceiveMessageID = 0x224,
            ReceiveMessageDataHigh = 0x228,
            ReceiveMessageDataLow = 0x22C,
            ReceiveMessageAcceptanceMaskRegister = 0x230,
            ReceiveMessageAcceptanceCodeRegister = 0x234,
            ReceiveMessageAcceptanceMaskRegisterData = 0x238,
            ReceiveMessageAcceptanceCodeRegisterData = 0x23C
        }
    }
}
