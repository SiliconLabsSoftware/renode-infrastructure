//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Analog
{
    // Series-2 VDAC (efm32pg23_vdac.h / efr32mg24_vdac.h - the two layouts are identical, 14
    // registers, checked offset by offset).
    //
    // What it models: enable/disable with the DISABLING handshake, per-channel enable through
    // CMD, the data FIFO as a one-deep latch whose latest value is the channel's output, the
    // STATUS bits emlib polls (CHnENS, SYNCBUSY, FIFO flush busy, FIFO empty/full/count), the
    // conversion-done and data-valid-level interrupt flags, and SET/CLR/TGL aliases.
    //
    // What it does not: conversion timing (a write converts on the spot - emlib's busy-waits
    // all wait for bits to CLEAR, so instant completion cannot hang it), the refresh timer, sine
    // mode, the ABUS pin allocation check, and the output-pin routing. The observable here is
    // the latched sample - `Channel0Data` (12-bit) and
    // `Channel0Millivolts` against the selected reference - not a waveform on a pad.
    //
    // One thing a model downstream of the DAC may need as a signal is whether the output is
    // above some level at all, for example when the DAC drives a transistor gate. So each
    // channel also has a GPIO output - `Connections[0]`,
    // `Connections[1]` - that is high while the latched sample converts to at least
    // `GateThresholdMillivolts`. Wire it to whatever models the driven circuit:
    //
    //     vdac0:
    //         0 -> load@0
    //
    // Firmware may feed the FIFO with halfword LDMA transfers, so
    // narrower accesses are widened to the 32-bit registers rather than dropped.
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class SiLabs_VDAC_1 : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public SiLabs_VDAC_1(IMachine machine, uint supplyMillivolts = 3300, uint externalReferenceMillivolts = 1250,
                               uint gateThresholdMillivolts = 500)
        {
            this.machine = machine;
            this.supplyMillivolts = supplyMillivolts;
            this.externalReferenceMillivolts = externalReferenceMillivolts;
            GateThresholdMillivolts = gateThresholdMillivolts;
            IRQ = new GPIO();
            gateActive = new GPIO[ChannelCount];
            var connections = new Dictionary<int, IGPIO>();
            for(var i = 0; i < ChannelCount; i++)
            {
                gateActive[i] = new GPIO();
                connections[i] = gateActive[i];
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            channelEnabled = new bool[ChannelCount];
            channelData = new uint[ChannelCount];
            channelWrites = new ulong[ChannelCount];
            channelPeak = new uint[ChannelCount];
            registers = BuildRegisters();
            Reset();
        }

        public void Reset()
        {
            registers.Reset();
            enabled = false;
            for(var i = 0; i < ChannelCount; i++)
            {
                channelEnabled[i] = false;
                channelData[i] = 0;
                channelWrites[i] = 0;
                channelPeak[i] = 0;
                gateActive[i].Unset();
            }
            interruptFlags = 0;
            interruptEnable = 0;
            IRQ.Unset();
        }

        public uint ReadDoubleWord(long offset)
        {
            var (baseOffset, _) = Alias(offset);
            return registers.Read(baseOffset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            var (baseOffset, mode) = Alias(offset);
            switch(mode)
            {
                case AliasMode.Set:
                    value = registers.Read(baseOffset) | value;
                    break;
                case AliasMode.Clear:
                    value = registers.Read(baseOffset) & ~value;
                    break;
                case AliasMode.Toggle:
                    value = registers.Read(baseOffset) ^ value;
                    break;
            }
            registers.Write(baseOffset, value);
        }

        public long Size => 0x4000;

        public GPIO IRQ { get; }

        // Per-channel "gate above threshold" outputs; see the class comment.
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // The output level at which a channel's Connections[n] goes high. 500 mV by default,
        // below the turn-on threshold of a typical MOSFET gate.
        public uint GateThresholdMillivolts { get; set; }

        public bool Channel0GateActive => gateActive[0].IsSet;
        public bool Channel1GateActive => gateActive[1].IsSet;

        // -- observation -------------------------------------------------------------------

        public bool Enabled => enabled;
        public bool Channel0Enabled => channelEnabled[0];
        public bool Channel1Enabled => channelEnabled[1];
        public uint Channel0Data => channelData[0];
        public uint Channel1Data => channelData[1];
        public ulong Channel0Writes => channelWrites[0];
        public ulong Channel1Writes => channelWrites[1];
        public uint Channel0Millivolts => ToMillivolts(channelData[0]);
        public uint Channel1Millivolts => ToMillivolts(channelData[1]);

        // The largest sample written to a channel since the previous call, in millivolts, then
        // starts over. A DMA-fed ramp lasts less than a Monitor round trip, so the latched sample
        // is almost always the idle value by the time anyone reads it; the peak is not.
        public uint TakeChannelPeakMillivolts(int channel)
        {
            var peak = channelPeak[channel];
            channelPeak[channel] = 0;
            return ToMillivolts(peak);
        }
        public uint ReferenceMillivolts => Reference();

        private uint ToMillivolts(uint data)
        {
            return (uint)((ulong)data * Reference() / 4095UL);
        }

        private uint Reference()
        {
            // CFG.REFRSEL: 0 V1V25, 1 V2V5, 2 VDD, 3 EXT (efm32pg23_vdac.h _VDAC_CFG_REFRSEL_*).
            switch(referenceSelect.Value)
            {
                case 0: return 1250;
                case 1: return 2500;
                case 2: return supplyMillivolts;
                default: return externalReferenceMillivolts;
            }
        }

        // -- behavior ----------------------------------------------------------------------

        private void WriteChannel(int channel, uint value)
        {
            if(!enabled)
            {
                this.Log(LogLevel.Warning, "CH{0}F written while the VDAC is disabled; ignored", channel);
                return;
            }
            if(!channelEnabled[channel])
            {
                // Silicon queues into the FIFO and converts when the channel is enabled; the
                // firmware here enables before writing, so a write in this state is a bug
                // worth seeing rather than silently absorbing.
                this.Log(LogLevel.Warning, "CH{0}F written while channel {0} is disabled; ignored", channel);
                return;
            }
            channelData[channel] = value & 0xFFF;
            channelWrites[channel]++;
            channelPeak[channel] = System.Math.Max(channelPeak[channel], channelData[channel]);
            gateActive[channel].Set(ToMillivolts(channelData[channel]) >= GateThresholdMillivolts);
            // Conversion done, and the FIFO is below its data-valid level again (it is empty).
            interruptFlags |= (channel == 0 ? IfCh0ConversionDone : IfCh1ConversionDone)
                            | (channel == 0 ? IfCh0DataValidLevel : IfCh1DataValidLevel);
            UpdateInterrupts();
        }

        private void HandleCommand(uint value)
        {
            if((value & CmdCh0Enable) != 0) channelEnabled[0] = true;
            if((value & CmdCh0Disable) != 0) channelEnabled[0] = false;
            if((value & CmdCh1Enable) != 0) channelEnabled[1] = true;
            if((value & CmdCh1Disable) != 0) channelEnabled[1] = false;
            // FIFO flushes complete instantly: the FIFO is a latch here.
        }

        private void UpdateInterrupts()
        {
            IRQ.Set((interruptFlags & interruptEnable) != 0);
        }

        private uint Status()
        {
            uint status = 0;
            if(channelEnabled[0]) status |= StatusCh0Enabled | StatusCh0Warm;
            if(channelEnabled[1]) status |= StatusCh1Enabled | StatusCh1Warm;
            // The FIFO is always empty from the firmware's point of view: a write converts at once.
            status |= StatusCh0FifoEmpty | StatusCh1FifoEmpty;
            return status;
        }

        private DoubleWordRegisterCollection BuildRegisters()
        {
            return new DoubleWordRegisterCollection(this, new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.IpVersion, new DoubleWordRegister(this, 0x1)
                    .WithValueField(0, 32, FieldMode.Read, name: "IPVERSION")},
                {(long)Registers.Enable, new DoubleWordRegister(this)
                    .WithFlag(0, writeCallback: (_, v) =>
                    {
                        if(!v && enabled)
                        {
                            channelEnabled[0] = false;
                            channelEnabled[1] = false;
                        }
                        enabled = v;
                    }, valueProviderCallback: _ => enabled, name: "EN")
                    // DISABLING reads 0: the disable handshake is instantaneous.
                    .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => false, name: "DISABLING")
                    .WithReservedBits(2, 30)},
                {(long)Registers.SoftwareReset, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Write, writeCallback: (_, v) => { if(v) Reset(); }, name: "SWRST")
                    .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => false, name: "RESETTING")
                    .WithReservedBits(2, 30)},
                {(long)Registers.Config, new DoubleWordRegister(this)
                    .WithFlag(0, name: "DIFF")
                    .WithFlag(1, name: "SINEMODE")
                    .WithFlag(2, name: "SINERESET")
                    .WithReservedBits(3, 1)
                    .WithValueField(4, 3, out referenceSelect, name: "REFRSEL")
                    .WithValueField(7, 7, name: "PRESC")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "TIMEROVRFLOWPERIOD")
                    .WithValueField(20, 4, name: "REFRESHPERIOD")
                    .WithFlag(24, name: "BIASKEEPWARM")
                    .WithFlag(25, name: "DMAWU")
                    .WithFlag(26, name: "ONDEMANDCLK")
                    .WithFlag(27, name: "DBGHALT")
                    .WithValueField(28, 3, name: "WARMUPTIME")
                    .WithReservedBits(31, 1)},
                {(long)Registers.Status, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => Status(), name: "STATUS")},
                {(long)Registers.Channel0Config, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "CH0CFG")},
                {(long)Registers.Channel1Config, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "CH1CFG")},
                {(long)Registers.Command, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, v) => HandleCommand((uint)v), name: "CMD")},
                {(long)Registers.InterruptFlags, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, v) => { interruptFlags = (uint)v; UpdateInterrupts(); },
                                    valueProviderCallback: _ => interruptFlags, name: "IF")},
                {(long)Registers.InterruptEnable, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, v) => { interruptEnable = (uint)v; UpdateInterrupts(); },
                                    valueProviderCallback: _ => interruptEnable, name: "IEN")},
                {(long)Registers.Channel0Fifo, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, v) => WriteChannel(0, (uint)v), name: "CH0F")},
                {(long)Registers.Channel1Fifo, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, v) => WriteChannel(1, (uint)v), name: "CH1F")},
                {(long)Registers.OutputControl, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "OUTCTRL")},
                {(long)Registers.OutputTimerConfig, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "OUTTIMERCFG")},
            });
        }

        private static (long, AliasMode) Alias(long offset)
        {
            if(offset >= 0x3000) return (offset - 0x3000, AliasMode.Toggle);
            if(offset >= 0x2000) return (offset - 0x2000, AliasMode.Clear);
            if(offset >= 0x1000) return (offset - 0x1000, AliasMode.Set);
            return (offset, AliasMode.None);
        }

        private readonly IMachine machine;
        private readonly uint supplyMillivolts;
        private readonly uint externalReferenceMillivolts;
        private readonly DoubleWordRegisterCollection registers;
        private readonly bool[] channelEnabled;
        private readonly uint[] channelData;
        private readonly ulong[] channelWrites;
        private readonly uint[] channelPeak;
        private readonly GPIO[] gateActive;
        private IValueRegisterField referenceSelect;
        private bool enabled;
        private uint interruptFlags;
        private uint interruptEnable;

        private const int ChannelCount = 2;

        private const uint CmdCh0Enable = 1u << 0;
        private const uint CmdCh0Disable = 1u << 1;
        private const uint CmdCh1Enable = 1u << 4;
        private const uint CmdCh1Disable = 1u << 5;

        private const uint StatusCh0Enabled = 1u << 0;
        private const uint StatusCh1Enabled = 1u << 1;
        private const uint StatusCh0Warm = 1u << 4;
        private const uint StatusCh1Warm = 1u << 5;
        private const uint StatusCh0FifoEmpty = 1u << 22;
        private const uint StatusCh1FifoEmpty = 1u << 23;

        private const uint IfCh0ConversionDone = 1u << 0;
        private const uint IfCh1ConversionDone = 1u << 1;
        private const uint IfCh0DataValidLevel = 1u << 20;
        private const uint IfCh1DataValidLevel = 1u << 21;

        private enum AliasMode
        {
            None,
            Set,
            Clear,
            Toggle,
        }

        private enum Registers : long
        {
            IpVersion = 0x00,
            Enable = 0x04,
            SoftwareReset = 0x08,
            Config = 0x0C,
            Status = 0x10,
            Channel0Config = 0x14,
            Channel1Config = 0x18,
            Command = 0x1C,
            InterruptFlags = 0x20,
            InterruptEnable = 0x24,
            Channel0Fifo = 0x28,
            Channel1Fifo = 0x2C,
            OutputControl = 0x30,
            OutputTimerConfig = 0x34,
        }
    }
}
