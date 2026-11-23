//
// Copyright (c) 2010-2026 Antmicro
// Copyright (c) 2022-2026 Silicon Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Bus.Wrappers;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous.SiLabs;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Wireless
{
    public class SiLabs_xG24_LPW : IBusPeripheral, IRadio, ISiLabs_PacketTraceSniffer
    {
        public SiLabs_xG24_LPW(Machine machine, MappedMemory ram, CortexM sequencer, SiLabs_BUFC_4 bufferController, SiLabs_SYSRTC_1 sysrtc = null, bool pa0DbmSupport = false, bool pa10DbmSupport = false, bool pa20DbmSupport = false)
        {
            random = EmulationManager.Instance.CurrentEmulation.RandomGenerator;
            this.machine = machine;
            this.ram = ram;
            this.sequencer = sequencer;
            this.RAC_0DbmSupport = pa0DbmSupport;
            this.RAC_10DbmSupport = pa10DbmSupport;
            this.RAC_20DbmSupport = pa20DbmSupport;
            this.bufferController = bufferController;

            var initialTimerLimit = 0xFFFFUL;

            seqTimer = new LimitTimer(machine.ClockSource, MicrosecondFrequency, this, "seqtimer", initialTimerLimit, direction: Direction.Ascending,
                                      enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            seqTimer.LimitReached += RAC_SeqTimerHandleLimitReached;

            proTimer = new LimitTimer(machine.ClockSource, MicrosecondFrequency, this, "protimer", initialTimerLimit, direction: Direction.Ascending,
                                      enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            proTimer.LimitReached += PROTIMER_HandleTimerLimitReached;

            paRampingTimer = new LimitTimer(machine.ClockSource, MicrosecondFrequency, this, "parampingtimer", initialTimerLimit, direction: Direction.Ascending,
                                            enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            paRampingTimer.LimitReached += RAC_PaRampingTimerHandleLimitReached;

            rssiUpdateTimer = new LimitTimer(machine.ClockSource, MicrosecondFrequency, this, "rssiupdatetimer", initialTimerLimit, direction: Direction.Ascending,
                                             enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            rssiUpdateTimer.LimitReached += AGC_RssiUpdateTimerHandleLimitReached;

            txTimer = new LimitTimer(machine.ClockSource, MicrosecondFrequency, this, "txtimer", initialTimerLimit, direction: Direction.Ascending,
                                             enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            txTimer.LimitReached += RAC_TxTimerLimitReached;

            rxTimer = new LimitTimer(machine.ClockSource, MicrosecondFrequency, this, "rxtimer", initialTimerLimit, direction: Direction.Ascending,
                                             enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            rxTimer.LimitReached += RAC_RxTimerLimitReached;

            rxWarmSettleTimer = new LimitTimer(machine.ClockSource, MicrosecondFrequency, this, "rxwarmsettle", initialTimerLimit, direction: Direction.Ascending,
                                               enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            rxWarmSettleTimer.LimitReached += RAC_RxWarmSettleTimerLimitReached;

            FrameControllerPrioritizedIRQ = new GPIO();
            FrameControllerIRQ = new GPIO();
            ModulatorAndDemodulatorIRQ = new GPIO();
            RadioControllerSequencerIRQ = new GPIO();
            RadioControllerRadioStateMachineIRQ = new GPIO();
            ProtocolTimerIRQ = new GPIO();
            SynthesizerIRQ = new GPIO();
            AutomaticGainControlIRQ = new GPIO();
            HostMailboxIRQ = new GPIO();
            SeqOffIRQ = new GPIO();
            SeqRxWarmIRQ = new GPIO();
            SeqRxSearchIRQ = new GPIO();
            SeqRxFrameIRQ = new GPIO();
            SeqRxPoweringDownIRQ = new GPIO();
            SeqRx2RxIRQ = new GPIO();
            SeqRxOverflowIRQ = new GPIO();
            SeqRx2TxIRQ = new GPIO();
            SeqTxWarmIRQ = new GPIO();
            SeqTxIRQ = new GPIO();
            SeqTxPoweringDownIRQ = new GPIO();
            SeqTx2RxIRQ = new GPIO();
            SeqTx2TxIRQ = new GPIO();
            SeqShutdownIRQ = new GPIO();
            SeqRadioControllerIRQ = new GPIO();
            SeqFrameControllerIRQ = new GPIO();
            SeqFrameControllerPriorityIRQ = new GPIO();
            SeqModulatorAndDemodulatorIRQ = new GPIO();
            SeqAutomaticGainControlIRQ = new GPIO();
            SeqProtocolTimerIRQ = new GPIO();
            SeqSynthesizerIRQ = new GPIO();
            SeqRfMailboxIRQ = new GPIO();
            SeqSoftware0IRQ = new GPIO();
            SeqSoftware1IRQ = new GPIO();

            sequencer.CpuWaitSignal.Set(true);

            // Protocol Timer stuff
            PROTIMER_timeoutCounter = new PROTIMER_TimeoutCounter[PROTIMER_NumberOfTimeoutCounters];
            for(var idx = 0; idx < PROTIMER_NumberOfTimeoutCounters; ++idx)
            {
                var i = idx;
                PROTIMER_timeoutCounter[i] = new PROTIMER_TimeoutCounter(this, (uint)i);
            }
            PROTIMER_timeoutCounter[0].Synchronized += () => PROTIMER_TimeoutCounter0HandleSynchronize();
            PROTIMER_timeoutCounter[0].Underflowed += () => PROTIMER_TimeoutCounter0HandleUnderflow();
            PROTIMER_timeoutCounter[0].Finished += () => PROTIMER_TimeoutCounter0HandleFinish();

            PROTIMER_captureCompareChannel = new PROTIMER_CaptureCompareChannel[PROTIMER_NumberOfCaptureCompareChannels];
            for(var idx = 0; idx < PROTIMER_NumberOfCaptureCompareChannels; ++idx)
            {
                var i = idx;
                PROTIMER_captureCompareChannel[i] = new PROTIMER_CaptureCompareChannel(this, (uint)i);
            }

            if(sysrtc != null)
            {
                sysrtc.CompareMatchGroup1Channel0 += () => PROTIMER_RtcCapture(0);
                sysrtc.CompareMatchGroup1Channel1 += () => PROTIMER_RtcCapture(1);
            }

            // FRC stuff
            FRC_frameDescriptor = new FRC_FrameDescriptor[FRC_NumberOfFrameDescriptors];
            for(var idx = 0u; idx < FRC_NumberOfFrameDescriptors; ++idx)
            {
                FRC_frameDescriptor[idx] = new FRC_FrameDescriptor();
            }
            FRC_packetBufferCapture = new byte[FRC_PacketBufferCaptureSize];

            // AGC stuff
            AGC_FrameRssiIntegerPart = -100;

            frameControllerRegistersCollection = BuildFrameControllerRegistersCollection();
            cyclicRedundancyCheckRegistersCollection = BuildCyclicRedundancyCheckRegistersCollection();
            synthesizerRegistersCollection = BuildSynthesizerRegistersCollection();
            radioControllerRegistersCollection = BuildRadioControllerRegistersCollection();
            protocolTimerRegistersCollection = BuildProtocolTimerRegistersCollection();
            modulatorAndDemodulatorRegistersCollection = BuildModulatorAndDemodulatorRegistersCollection();
            automaticGainControlRegistersCollection = BuildAutomaticGainControlRegistersCollection();
            hostMailboxRegistersCollection = BuildHostMailboxRegistersCollection();
            radioMailboxRegistersCollection = BuildRadioMailboxRegistersCollection();

            sequencer.AddHookAtWfiStateChange(isInWfi =>
            {
                RAC_sequencerInWfi = isInWfi;
                RAC_UpdateEm1pRequest();
            });
            sequencer.CpuWaitSignal.AddStateChangedHook(_ => RAC_UpdateEm1pRequest());

            InterferenceQueue.InterferenceQueueChanged += InteferenceQueueChangedCallback;
        }

        [ConnectionRegion("frc")]
        public uint ReadDoubleWordFromFrameController(long offset)
        {
            return Read<FrameControllerRegisters>(frameControllerRegistersCollection, "Frame Controller (FRC)", offset);
        }

        [ConnectionRegion("rfmailbox")]
        public uint ReadDoubleWordFromRadioMailbox(long offset)
        {
            return Read<RadioMailboxRegisters>(radioMailboxRegistersCollection, "Radio Mailbox (RFMAILBOX)", offset);
        }

        [ConnectionRegion("rac_ns")]
#pragma warning disable IDE0060
        public void WriteByteToRadioControllerNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("rac_ns")]
        public void WriteDoubleWordToRadioControllerNonSecure(long offset, uint value)
        {
            Write<RadioControllerRegisters>(radioControllerRegistersCollection, "Radio Controller (RAC_NS)", offset, value);
        }

        [ConnectionRegion("rac_ns")]
        public byte ReadByteFromRadioControllerNonSecure(long offset)
        {
            return ReadByte<RadioControllerRegisters>(radioControllerRegistersCollection, "Radio Controller (RAC_NS)", offset);
        }

        [ConnectionRegion("rac_ns")]
        public uint ReadDoubleWordFromRadioControllerNonSecure(long offset)
        {
            return Read<RadioControllerRegisters>(radioControllerRegistersCollection, "Radio Controller (RAC_NS)", offset);
        }

        [ConnectionRegion("rac")]
#pragma warning disable IDE0060
        public void WriteByteToRadioController(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("rac")]
        public void WriteDoubleWordToRadioController(long offset, uint value)
        {
            Write<RadioControllerRegisters>(radioControllerRegistersCollection, "Radio Controller (RAC)", offset, value);
        }

        [ConnectionRegion("rac")]
        public byte ReadByteFromRadioController(long offset)
        {
            return ReadByte<RadioControllerRegisters>(radioControllerRegistersCollection, "Radio Controller (RAC)", offset);
        }

        [ConnectionRegion("rac")]
        public uint ReadDoubleWordFromRadioController(long offset)
        {
            return Read<RadioControllerRegisters>(radioControllerRegistersCollection, "Radio Controller (RAC)", offset);
        }

        [ConnectionRegion("protimer_ns")]
#pragma warning disable IDE0060
        public void WriteByteToProtocolTimerNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("protimer_ns")]
        public void WriteDoubleWordToProtocolTimerNonSecure(long offset, uint value)
        {
            Write<ProtocolTimerRegisters>(protocolTimerRegistersCollection, "Protocol Timer (PROTIMER_NS)", offset, value);
        }

        [ConnectionRegion("protimer_ns")]
        public uint ReadDoubleWordFromProtocolTimerNonSecure(long offset)
        {
            return Read<ProtocolTimerRegisters>(protocolTimerRegistersCollection, "Protocol Timer (PROTIMER_NS)", offset);
        }

        [ConnectionRegion("protimer")]
#pragma warning disable IDE0060
        public void WriteByteToProtocolTimer(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("protimer")]
        public void WriteDoubleWordToProtocolTimer(long offset, uint value)
        {
            Write<ProtocolTimerRegisters>(protocolTimerRegistersCollection, "Protocol Timer (PROTIMER)", offset, value);
        }

        [ConnectionRegion("protimer")]
        public byte ReadByteFromProtocolTimer(long offset)
        {
            return ReadByte<ProtocolTimerRegisters>(protocolTimerRegistersCollection, "Protocol Timer (PROTIMER)", offset);
        }

        [ConnectionRegion("rfmailbox")]
        public byte ReadByteFromRadioMailbox(long offset)
        {
            return ReadByte<RadioMailboxRegisters>(radioMailboxRegistersCollection, "Radio Mailbox (RFMAILBOX)", offset);
        }

        [ConnectionRegion("rfmailbox")]
        public void WriteDoubleWordToRadioMailbox(long offset, uint value)
        {
            Write<RadioMailboxRegisters>(radioMailboxRegistersCollection, "Radio Mailbox (RFMAILBOX)", offset, value);
        }

        [ConnectionRegion("rfmailbox")]
#pragma warning disable IDE0060
        public void WriteByteToRadioMailbox(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("rfmailbox_ns")]
        public uint ReadDoubleWordFromRadioMailboxNonSecure(long offset)
        {
            return Read<RadioMailboxRegisters>(radioMailboxRegistersCollection, "Radio Mailbox (RFMAILBOX_NS)", offset);
        }

        [ConnectionRegion("hostmailbox_ns")]
#pragma warning disable IDE0060
        public void WriteByteToHostMailboxNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("hostmailbox_ns")]
        public void WriteDoubleWordToHostMailboxNonSecure(long offset, uint value)
        {
            Write<HostMailboxRegisters>(hostMailboxRegistersCollection, "Host Mailbox (HOSTMAILBOX_NS)", offset, value);
        }

        [ConnectionRegion("protimer")]
        public uint ReadDoubleWordFromProtocolTimer(long offset)
        {
            return Read<ProtocolTimerRegisters>(protocolTimerRegistersCollection, "Protocol Timer (PROTIMER)", offset);
        }

        [ConnectionRegion("hostmailbox_ns")]
        public byte ReadByteFromHostMailboxNonSecure(long offset)
        {
            return ReadByte<HostMailboxRegisters>(hostMailboxRegistersCollection, "Host Mailbox (HOSTMAILBOX_NS)", offset);
        }

        [ConnectionRegion("hostmailbox")]
#pragma warning disable IDE0060
        public void WriteByteToHostMailbox(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("hostmailbox")]
        public void WriteDoubleWordToHostMailbox(long offset, uint value)
        {
            Write<HostMailboxRegisters>(hostMailboxRegistersCollection, "Host Mailbox (HOSTMAILBOX)", offset, value);
        }

        [ConnectionRegion("hostmailbox")]
        public byte ReadByteFromHostMailbox(long offset)
        {
            return ReadByte<HostMailboxRegisters>(hostMailboxRegistersCollection, "Host Mailbox (HOSTMAILBOX)", offset);
        }

        [ConnectionRegion("hostmailbox")]
        public uint ReadDoubleWordFromHostMailbox(long offset)
        {
            return Read<HostMailboxRegisters>(hostMailboxRegistersCollection, "Host Mailbox (HOSTMAILBOX)", offset);
        }

        [ConnectionRegion("rfmailbox_ns")]
#pragma warning disable IDE0060
        public void WriteByteToRadioMailboxNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("rfmailbox_ns")]
        public void WriteDoubleWordToRadioMailboxNonSecure(long offset, uint value)
        {
            Write<RadioMailboxRegisters>(radioMailboxRegistersCollection, "Radio Mailbox (RFMAILBOX_NS)", offset, value);
        }

        [ConnectionRegion("rfmailbox_ns")]
        public byte ReadByteFromRadioMailboxNonSecure(long offset)
        {
            return ReadByte<RadioMailboxRegisters>(radioMailboxRegistersCollection, "Radio Mailbox (RFMAILBOX_NS)", offset);
        }

        [ConnectionRegion("hostmailbox_ns")]
        public uint ReadDoubleWordFromHostMailboxNonSecure(long offset)
        {
            return Read<HostMailboxRegisters>(hostMailboxRegistersCollection, "Host Mailbox (HOSTMAILBOX_NS)", offset);
        }

        [ConnectionRegion("synth_ns")]
#pragma warning disable IDE0060
        public void WriteByteToSynthesizerNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("protimer_ns")]
        public byte ReadByteFromProtocolTimerNonSecure(long offset)
        {
            return ReadByte<ProtocolTimerRegisters>(protocolTimerRegistersCollection, "Protocol Timer (PROTIMER_NS)", offset);
        }

        [ConnectionRegion("synth_ns")]
        public byte ReadByteFromSynthesizerNonSecure(long offset)
        {
            return ReadByte<SynthesizerRegisters>(synthesizerRegistersCollection, "Synthesizer (SYNTH_NS)", offset);
        }

        [ConnectionRegion("crc")]
        public uint ReadDoubleWordFromCyclicRedundancyCheck(long offset)
        {
            return Read<CyclicRedundancyCheckRegisters>(cyclicRedundancyCheckRegistersCollection, "Cyclic Redundancy Check (CRC)", offset);
        }

        [ConnectionRegion("agc_ns")]
#pragma warning disable IDE0060
        public void WriteByteToAutomaticGainControllerNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("agc_ns")]
        public void WriteDoubleWordToAutomaticGainControllerNonSecure(long offset, uint value)
        {
            Write<AutomaticGainControlRegisters>(automaticGainControlRegistersCollection, "Automatic Gain Control (AGC_NS)", offset, value);
        }

        [ConnectionRegion("synth_ns")]
        public void WriteDoubleWordToSynthesizerNonSecure(long offset, uint value)
        {
            Write<SynthesizerRegisters>(synthesizerRegistersCollection, "Synthesizer (SYNTH_NS)", offset, value);
        }

        [ConnectionRegion("agc_ns")]
        public uint ReadDoubleWordFromAutomaticGainControllerNonSecure(long offset)
        {
            return Read<AutomaticGainControlRegisters>(automaticGainControlRegistersCollection, "Automatic Gain Control (AGC_NS)", offset);
        }

        [ConnectionRegion("agc")]
#pragma warning disable IDE0060
        public void WriteByteToAutomaticGainController(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("agc")]
        public void WriteDoubleWordToAutomaticGainController(long offset, uint value)
        {
            Write<AutomaticGainControlRegisters>(automaticGainControlRegistersCollection, "Automatic Gain Control (AGC)", offset, value);
        }

        [ConnectionRegion("crc")]
        public byte ReadByteFromCyclicRedundancyCheck(long offset)
        {
            return ReadByte<CyclicRedundancyCheckRegisters>(cyclicRedundancyCheckRegistersCollection, "Cyclic Redundancy Check (CRC)", offset);
        }

        [ConnectionRegion("agc")]
        public byte ReadByteFromAutomaticGainController(long offset)
        {
            return ReadByte<AutomaticGainControlRegisters>(automaticGainControlRegistersCollection, "Automatic Gain Control (AGC)", offset);
        }

        [ConnectionRegion("frc_ns")]
#pragma warning disable IDE0060
        public void WriteByteToFrameControllerNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("frc_ns")]
        public void WriteDoubleWordToFrameControllerNonSecure(long offset, uint value)
        {
            Write<FrameControllerRegisters>(frameControllerRegistersCollection, "Frame Controller (FRC_NS)", offset, value);
        }

        [ConnectionRegion("frc_ns")]
        public byte ReadByteFromFrameControllerNonSecure(long offset)
        {
            return ReadByte<FrameControllerRegisters>(frameControllerRegistersCollection, "Frame Controller (FRC_NS)", offset);
        }

        [ConnectionRegion("frc_ns")]
        public uint ReadDoubleWordFromFrameControllerNonSecure(long offset)
        {
            return Read<FrameControllerRegisters>(frameControllerRegistersCollection, "Frame Controller (FRC_NS)", offset);
        }

        [ConnectionRegion("frc")]
#pragma warning disable IDE0060
        public void WriteByteToFrameController(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("frc")]
        public void WriteDoubleWordToFrameController(long offset, uint value)
        {
            Write<FrameControllerRegisters>(frameControllerRegistersCollection, "Frame Controller (FRC)", offset, value);
        }

        [ConnectionRegion("frc")]
        public byte ReadByteFromFrameController(long offset)
        {
            return ReadByte<FrameControllerRegisters>(frameControllerRegistersCollection, "Frame Controller (FRC)", offset);
        }

        [ConnectionRegion("agc")]
        public uint ReadDoubleWordFromAutomaticGainController(long offset)
        {
            return Read<AutomaticGainControlRegisters>(automaticGainControlRegistersCollection, "Automatic Gain Control (AGC)", offset);
        }

        [ConnectionRegion("crc")]
        public void WriteDoubleWordToCyclicRedundancyCheck(long offset, uint value)
        {
            Write<CyclicRedundancyCheckRegisters>(cyclicRedundancyCheckRegistersCollection, "Cyclic Redundancy Check (CRC)", offset, value);
        }

        [ConnectionRegion("agc_ns")]
        public byte ReadByteFromAutomaticGainControllerNonSecure(long offset)
        {
            return ReadByte<AutomaticGainControlRegisters>(automaticGainControlRegistersCollection, "Automatic Gain Control (AGC)", offset);
        }

        [ConnectionRegion("crc_ns")]
        public uint ReadDoubleWordFromCyclicRedundancyCheckNonSecure(long offset)
        {
            return Read<CyclicRedundancyCheckRegisters>(cyclicRedundancyCheckRegistersCollection, "Cyclic Redundancy Check (CRC_NS)", offset);
        }

        [ConnectionRegion("synth_ns")]
        public uint ReadDoubleWordFromSynthesizerNonSecure(long offset)
        {
            return Read<SynthesizerRegisters>(synthesizerRegistersCollection, "Synthesizer (SYNTH_NS)", offset);
        }

        [ConnectionRegion("synth")]
#pragma warning disable IDE0060
        public void WriteByteToSynthesizer(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("synth")]
        public void WriteDoubleWordToSynthesizer(long offset, uint value)
        {
            Write<SynthesizerRegisters>(synthesizerRegistersCollection, "Synthesizer (SYNTH)", offset, value);
        }

        [ConnectionRegion("crc")]
#pragma warning disable IDE0060
        public void WriteByteToCyclicRedundancyCheck(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("synth")]
        public uint ReadDoubleWordFromSynthesizer(long offset)
        {
            return Read<SynthesizerRegisters>(synthesizerRegistersCollection, "Synthesizer (SYNTH)", offset);
        }

        [ConnectionRegion("modem_ns")]
#pragma warning disable IDE0060
        public void WriteByteToModulatorAndDemodulatorNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("modem_ns")]
        public void WriteDoubleWordToModulatorAndDemodulatorNonSecure(long offset, uint value)
        {
            Write<ModulatorAndDemodulatorRegisters>(modulatorAndDemodulatorRegistersCollection, "Modulator And Demodulator (MODEM_NS)", offset, value);
        }

        [ConnectionRegion("modem_ns")]
        public byte ReadByteFromModulatorAndDemodulatorNonSecure(long offset)
        {
            return ReadByte<ModulatorAndDemodulatorRegisters>(modulatorAndDemodulatorRegistersCollection, "Modulator And Demodulator (MODEM_NS)", offset);
        }

        [ConnectionRegion("synth")]
        public byte ReadByteFromSynthesizer(long offset)
        {
            return ReadByte<SynthesizerRegisters>(synthesizerRegistersCollection, "Synthesizer (SYNTH)", offset);
        }

        [ConnectionRegion("modem")]
#pragma warning disable IDE0060
        public void WriteByteToModulatorAndDemodulator(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("modem")]
        public void WriteDoubleWordToModulatorAndDemodulator(long offset, uint value)
        {
            Write<ModulatorAndDemodulatorRegisters>(modulatorAndDemodulatorRegistersCollection, "Modulator And Demodulator (MODEM)", offset, value);
        }

        [ConnectionRegion("modem")]
        public byte ReadByteFromModulatorAndDemodulator(long offset)
        {
            return ReadByte<ModulatorAndDemodulatorRegisters>(modulatorAndDemodulatorRegistersCollection, "Modulator And Demodulator (MODEM)", offset);
        }

        [ConnectionRegion("modem")]
        public uint ReadDoubleWordFromModulatorAndDemodulator(long offset)
        {
            return Read<ModulatorAndDemodulatorRegisters>(modulatorAndDemodulatorRegistersCollection, "Modulator And Demodulator (MODEM)", offset);
        }

        [ConnectionRegion("crc_ns")]
#pragma warning disable IDE0060
        public void WriteByteToCyclicRedundancyCheckNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // TODO: Single byte writes not implemented for now
        }

        [ConnectionRegion("crc_ns")]
        public void WriteDoubleWordToCyclicRedundancyCheckNonSecure(long offset, uint value)
        {
            Write<CyclicRedundancyCheckRegisters>(cyclicRedundancyCheckRegistersCollection, "Cyclic Redundancy Check (CRC_NS)", offset, value);
        }

        [ConnectionRegion("crc_ns")]
        public byte ReadByteFromCyclicRedundancyCheckNonSecure(long offset)
        {
            return ReadByte<CyclicRedundancyCheckRegisters>(cyclicRedundancyCheckRegistersCollection, "Cyclic Redundancy Check (CRC_NS)", offset);
        }

        [ConnectionRegion("modem_ns")]
        public uint ReadDoubleWordFromModulatorAndDemodulatorNonSecure(long offset)
        {
            return Read<ModulatorAndDemodulatorRegisters>(modulatorAndDemodulatorRegistersCollection, "Modulator And Demodulator (MODEM_NS)", offset);
        }

        public void Reset()
        {
            // Reset frame and channel variables
            currentFrame = null;
            currentFrameOffset = 0;
            currentDisassembleSubFrame = 0;
            currentDisassembleSubFrameStartWord = 0;
            currentDisassembleSubFrameBytes = 0;
            currentFrameByteOverTheAirTimeUs = 0;
            currentFrameElapsedAirtimeUs = 0;
            currentFrameSender = null;
            currentFrameTxStart = TimeInterval.Empty;
            currentFrameChannel = 0;
            currentFrameCodedSymbolsPerInfoBit = 0;

            // Reset FRC variables
            FRC_rxDonePending = false;
            FRC_rxFrameIrqClearedPending = false;
            FRC_rxAbortInProgress = false;
            FRC_frameOk = false;
            FRC_frameLengthError = false;
            FRC_rxFrameDiscarded = false;
            FRC_crcErrorTolerated = false;

            // Reset RAC variables
            RAC_rxFailReason = RAC_RxFailReason.None;
            RAC_previous1RadioState = RAC_RadioState.Off;
            RAC_previous2RadioState = RAC_RadioState.Off;
            RAC_previous3RadioState = RAC_RadioState.Off;
            RAC_paOutputLevelRamping = false;
            RAC_seqTimerLimit = 0xFFFF;

            // Reset PROTIMER variables
            PROTIMER_preCounterSourcedBitmask = 0;
            PROTIMER_baseCounterValue = 0;
            PROTIMER_wrapCounterValue = 0;
            PROTIMER_rtcWait = false;
            PROTIMER_txEnable = false;
            PROTIMER_txRequestState = PROTIMER_TxRxRequestState.Idle;
            PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.Idle;
            PROTIMER_listenBeforeTalkState = PROTIMER_ListenBeforeTalkState.Idle;
            PROTIMER_listenBeforeTalkPending = false;

            // Reset MODEM variables
            MODEM_txRampingDoneInterrupt = true;

            // Reset AGC variables
            AGC_rssiFirstRead = true;
            AGC_rssiStartCommandOngoing = false;
            AGC_rssiStartCommandFromProtimer = false;

            // Disable all timers
            seqTimer.Enabled = false;
            paRampingTimer.Enabled = false;
            rssiUpdateTimer.Enabled = false;
            proTimer.Enabled = false;
            rxTimer.Enabled = false;
            txTimer.Enabled = false;
            rxWarmSettleTimer.Enabled = false;
            SYNTH_startValid = false;

            // Reset the sequencer and put it back in halted state.
            sequencer.CpuWaitSignal.Set(true);
            sequencer.Reset();
            RAC_sequencerInWfi = false;
            RAC_sequencerResetPending = false;

            // TODO: clean up interference queue?

            RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.Reset);

            // Reset registers
            frameControllerRegistersCollection.Reset();
            cyclicRedundancyCheckRegistersCollection.Reset();
            synthesizerRegistersCollection.Reset();
            radioControllerRegistersCollection.Reset();
            protocolTimerRegistersCollection.Reset();
            modulatorAndDemodulatorRegistersCollection.Reset();
            automaticGainControlRegistersCollection.Reset();
            hostMailboxRegistersCollection.Reset();
            radioMailboxRegistersCollection.Reset();

            RAC_UpdateEm1pRequest();
            UpdateInterrupts();
        }

        public void InteferenceQueueChangedCallback(TimeInterval changeTime)
        {
            if(!EmulationManager.Instance.CurrentEmulation.TryGetMachineName(machine, out _))
            {
                return;
            }

            // This is called on the thread of whoever changed the queue, which may be another machine's CPU
            // thread running in parallel with ours, and which holds its own radio lock: do not take ours here.
            // Hand the update to our own time source instead, which runs it when our CPU is not running, the same
            // way frames are delivered. The RSSI is sampled at the time of the change rather than at the time we
            // get to run, so that a transmission that is already over by then still latches its threshold
            // interrupts. The RSSI register itself does not depend on this: it reads the queue at read time.
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
            {
                machine.ClockSource.ExecuteInLock(delegate
                {
                    if(RAC_currentRadioState == RAC_RadioState.RxSearch || RAC_currentRadioState == RAC_RadioState.RxFrame)
                    {
                        AGC_UpdateRssi(changeTime);
                    }
                    CheckOngoingRx();
                });
            });
        }

        public UInt64 PacketTraceRadioTimestamp()
        {
            TrySyncTime();
            return (UInt64)GetTime().TotalMicroseconds * 1000;
        }

        public void UpdateInterrupts()
        {
            machine.ClockSource.ExecuteInLock(delegate
            {
                FRC_CheckPacketCaptureBufferThreshold();

                //-------------------------------
                // Main core interrupts
                //-------------------------------

                // RAC_RSM interrupts
                var irq = ((RAC_radioStateChangeInterrupt.Value && RAC_radioStateChangeInterruptEnable.Value)
                           || (RAC_stimerCompareEventInterrupt.Value && RAC_stimerCompareEventInterruptEnable.Value));
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: IRQ RAC_RSM set (IF=0x{1:X}, IEN=0x{2:X})",
                             this.GetTime(),
                             (uint)(RAC_radioStateChangeInterrupt.Value
                                    | RAC_stimerCompareEventInterrupt.Value ? 0x2 : 0),
                             (uint)(RAC_radioStateChangeInterruptEnable.Value
                                    | RAC_stimerCompareEventInterruptEnable.Value ? 0x2 : 0));
                }
                RadioControllerRadioStateMachineIRQ.Set(irq);

                irq = ((RAC_mainCoreSeqInterrupts.Value & RAC_mainCoreSeqInterruptsEnable.Value) > 0);
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: IRQ RAC_SEQ set (IF=0x{1:X}, IEN=0x{2:X})",
                             this.GetTime(),
                             (uint)(RAC_mainCoreSeqInterrupts.Value << 16),
                             (uint)(RAC_mainCoreSeqInterruptsEnable.Value << 16));
                }
                RadioControllerSequencerIRQ.Set(irq);

                // FRC interrupt
                irq = ((FRC_txDoneInterrupt.Value && FRC_txDoneInterruptEnable.Value)
                       || (FRC_txAfterFrameDoneInterrupt.Value && FRC_txAfterFrameDoneInterruptEnable.Value)
                       || (FRC_txAbortedInterrupt.Value && FRC_txAbortedInterruptEnable.Value)
                       || (FRC_txUnderflowInterrupt.Value && FRC_txUnderflowInterruptEnable.Value)
                       || (FRC_rxDoneInterrupt.Value && FRC_rxDoneInterruptEnable.Value)
                       || (FRC_rxAbortedInterrupt.Value && FRC_rxAbortedInterruptEnable.Value)
                       || (FRC_frameErrorInterrupt.Value && FRC_frameErrorInterruptEnable.Value)
                       || (FRC_rxOverflowInterrupt.Value && FRC_rxOverflowInterruptEnable.Value)
                       || (FRC_rxRawEventInterrupt.Value && FRC_rxRawEventInterruptEnable.Value)
                       || (FRC_txRawEventInterrupt.Value && FRC_txRawEventInterruptEnable.Value)
                       || (FRC_packetBufferStartInterrupt.Value && FRC_packetBufferStartInterruptEnable.Value)
                       || (FRC_packetBufferThresholdInterrupt.Value && FRC_packetBufferThresholdInterruptEnable.Value));
                for(var channel = 0; channel < FRC_NumberOfWordCounterCompareChannels; ++channel)
                {
                    irq |= (FRC_wordCounterCompareInterrupt[channel].Value && FRC_wordCounterCompareInterruptEnable[channel].Value);
                }
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    frameControllerRegistersCollection.TryRead((long)FrameControllerRegisters.InterruptFlags, out interruptFlag);
                    frameControllerRegistersCollection.TryRead((long)FrameControllerRegisters.InterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: IRQ FRC set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                FrameControllerIRQ.Set(irq);

                // MODEM interrupt
                irq = ((MODEM_txFrameSentInterrupt.Value && MODEM_txFrameSentInterruptEnable.Value)
                       || (MODEM_txSyncSentInterrupt.Value && MODEM_txSyncSentInterruptEnable.Value)
                       || (MODEM_txPreambleSentInterrupt.Value && MODEM_txPreambleSentInterruptEnable.Value)
                       || (MODEM_TxRampingDoneInterrupt && MODEM_txRampingDoneInterruptEnable.Value)
                       || (MODEM_rxPreambleDetectedInterrupt.Value && MODEM_rxPreambleDetectedInterruptEnable.Value)
                       || (MODEM_rxFrameWithSyncWord0DetectedInterrupt.Value && MODEM_rxFrameWithSyncWord0DetectedInterruptEnable.Value)
                       || (MODEM_rxFrameWithSyncWord1DetectedInterrupt.Value && MODEM_rxFrameWithSyncWord1DetectedInterruptEnable.Value)
                       || (MODEM_rxPreambleLostInterrupt.Value && MODEM_rxPreambleLostInterruptEnable.Value));
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    modulatorAndDemodulatorRegistersCollection.TryRead((long)ModulatorAndDemodulatorRegisters.InterruptFlags, out interruptFlag);
                    modulatorAndDemodulatorRegistersCollection.TryRead((long)ModulatorAndDemodulatorRegisters.InterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: IRQ MODEM set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                ModulatorAndDemodulatorIRQ.Set(irq);

                // PROTIMER interrupt
                irq = (PROTIMER_preCounterOverflowInterrupt.Value && PROTIMER_preCounterOverflowInterruptEnable.Value)
                       || (PROTIMER_baseCounterOverflowInterrupt.Value && PROTIMER_baseCounterOverflowInterruptEnable.Value)
                       || (PROTIMER_wrapCounterOverflowInterrupt.Value && PROTIMER_wrapCounterOverflowInterruptEnable.Value)
                       || (PROTIMER_listenBeforeTalkSuccessInterrupt.Value && PROTIMER_listenBeforeTalkSuccessInterruptEnable.Value)
                       || (PROTIMER_listenBeforeTalkFailureInterrupt.Value && PROTIMER_listenBeforeTalkFailureInterruptEnable.Value)
                       || (PROTIMER_listenBeforeTalkRetryInterrupt.Value && PROTIMER_listenBeforeTalkRetryInterruptEnable.Value)
                       || (PROTIMER_listenBeforeTalkTimeoutCounterMatchInterrupt.Value && PROTIMER_listenBeforeTalkTimeoutCounterMatchInterruptEnable.Value)
                       || (PROTIMER_rtccSynchedInterrupt.Value && PROTIMER_rtccSynchedInterruptEnable.Value);
                Array.ForEach(PROTIMER_timeoutCounter, x => irq |= x.Interrupt);
                Array.ForEach(PROTIMER_captureCompareChannel, x => irq |= x.Interrupt);
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    protocolTimerRegistersCollection.TryRead((long)ProtocolTimerRegisters.InterruptFlags, out interruptFlag);
                    protocolTimerRegistersCollection.TryRead((long)ProtocolTimerRegisters.InterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: IRQ PROTIMER set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                ProtocolTimerIRQ.Set(irq);

                // AGC interrupt
                irq = ((AGC_rssiValidInterrupt.Value && AGC_rssiValidInterruptEnable.Value)
                       || (AGC_ccaInterrupt.Value && AGC_ccaInterruptEnable.Value)
                       || (AGC_rssiDoneInterrupt.Value && AGC_rssiDoneInterruptEnable.Value)
                       || (AGC_rssiHighInterrupt.Value && AGC_rssiHighInterruptEnable.Value)
                       || (AGC_rssiLowInterrupt.Value && AGC_rssiLowInterruptEnable.Value)
                       || (AGC_ccaNotDetectedInterrupt.Value && AGC_ccaNotDetectedInterruptEnable.Value));
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    automaticGainControlRegistersCollection.TryRead((long)AutomaticGainControlRegisters.InterruptFlags, out interruptFlag);
                    automaticGainControlRegistersCollection.TryRead((long)AutomaticGainControlRegisters.InterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: IRQ AGC set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                AutomaticGainControlIRQ.Set(irq);

                // HOSTMAILBOX interrupt
                int index;
                irq = false;
                for(index = 0; index < MailboxMessageNumber; index++)
                {
                    if(HOSTMAILBOX_messageInterrupt[index].Value && HOSTMAILBOX_messageInterruptEnable[index].Value)
                    {
                        irq = true;
                        break;
                    }
                }
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    hostMailboxRegistersCollection.TryRead((long)HostMailboxRegisters.InterruptFlags, out interruptFlag);
                    hostMailboxRegistersCollection.TryRead((long)HostMailboxRegisters.InterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: IRQ HOSTMAILBOX set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                HostMailboxIRQ.Set(irq);

                //-------------------------------
                // Sequencer interrupts
                //-------------------------------

                // RAC interrupt
                irq = ((RAC_seqRadioStateChangeInterrupt.Value && RAC_seqRadioStateChangeInterruptEnable.Value)
                       || (RAC_seqStimerCompareEventInterrupt.Value && RAC_seqStimerCompareEventInterruptEnable.Value));
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ RAC set (IF=0x{1:X}, IEN=0x{2:X})",
                             this.GetTime(),
                             ((RAC_seqRadioStateChangeInterrupt.Value ? 0x1 : 0) | (RAC_seqStimerCompareEventInterrupt.Value ? 0x2 : 0)),
                             ((RAC_seqRadioStateChangeInterruptEnable.Value ? 0x1 : 0) | (RAC_seqStimerCompareEventInterruptEnable.Value ? 0x2 : 0)));
                }
                SeqRadioControllerIRQ.Set(irq);

                // FRC interrupt
                irq = ((FRC_seqTxDoneInterrupt.Value && FRC_seqTxDoneInterruptEnable.Value)
                       || (FRC_seqTxAfterFrameDoneInterrupt.Value && FRC_seqTxAfterFrameDoneInterruptEnable.Value)
                       || (FRC_seqTxAbortedInterrupt.Value && FRC_seqTxAbortedInterruptEnable.Value)
                       || (FRC_seqTxUnderflowInterrupt.Value && FRC_seqTxUnderflowInterruptEnable.Value)
                       || (FRC_seqRxDoneInterrupt.Value && FRC_seqRxDoneInterruptEnable.Value)
                       || (FRC_seqRxAbortedInterrupt.Value && FRC_seqRxAbortedInterruptEnable.Value)
                       || (FRC_seqFrameErrorInterrupt.Value && FRC_seqFrameErrorInterruptEnable.Value)
                       || (FRC_seqRxOverflowInterrupt.Value && FRC_seqRxOverflowInterruptEnable.Value)
                       || (FRC_seqRxRawEventInterrupt.Value && FRC_seqRxRawEventInterruptEnable.Value)
                       || (FRC_seqTxRawEventInterrupt.Value && FRC_seqTxRawEventInterruptEnable.Value)
                       || (FRC_seqPacketBufferStartInterrupt.Value && FRC_seqPacketBufferStartInterruptEnable.Value)
                       || (FRC_seqPacketBufferThresholdInterrupt.Value && FRC_seqPacketBufferThresholdInterruptEnable.Value));
                for(var channel = 0; channel < FRC_NumberOfWordCounterCompareChannels; ++channel)
                {
                    irq |= (FRC_seqWordCounterCompareInterrupt[channel].Value && FRC_seqWordCounterCompareInterruptEnable[channel].Value);
                }
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    frameControllerRegistersCollection.TryRead((long)FrameControllerRegisters.SequencerInterruptFlags, out interruptFlag);
                    frameControllerRegistersCollection.TryRead((long)FrameControllerRegisters.SequencerInterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ FRC set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                SeqFrameControllerIRQ.Set(irq);

                // MODEM interrupt
                irq = ((MODEM_seqTxFrameSentInterrupt.Value && MODEM_seqTxFrameSentInterruptEnable.Value)
                       || (MODEM_seqTxSyncSentInterrupt.Value && MODEM_seqTxSyncSentInterruptEnable.Value)
                       || (MODEM_seqTxPreambleSentInterrupt.Value && MODEM_seqTxPreambleSentInterruptEnable.Value)
                       || (MODEM_TxRampingDoneInterrupt && MODEM_seqTxRampingDoneInterruptEnable.Value)
                       || (MODEM_seqRxPreambleDetectedInterrupt.Value && MODEM_seqRxPreambleDetectedInterruptEnable.Value)
                       || (MODEM_seqRxFrameWithSyncWord0DetectedInterrupt.Value && MODEM_seqRxFrameWithSyncWord0DetectedInterruptEnable.Value)
                       || (MODEM_seqRxFrameWithSyncWord1DetectedInterrupt.Value && MODEM_seqRxFrameWithSyncWord1DetectedInterruptEnable.Value)
                       || (MODEM_seqRxPreambleLostInterrupt.Value && MODEM_seqRxPreambleLostInterruptEnable.Value));
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    modulatorAndDemodulatorRegistersCollection.TryRead((long)ModulatorAndDemodulatorRegisters.SequencerInterruptFlags, out interruptFlag);
                    modulatorAndDemodulatorRegistersCollection.TryRead((long)ModulatorAndDemodulatorRegisters.SequencerInterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ MODEM set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                SeqModulatorAndDemodulatorIRQ.Set(irq);

                // PROTIMER interrupt
                irq = (PROTIMER_seqPreCounterOverflowInterrupt.Value && PROTIMER_seqPreCounterOverflowInterruptEnable.Value)
                       || (PROTIMER_seqBaseCounterOverflowInterrupt.Value && PROTIMER_seqBaseCounterOverflowInterruptEnable.Value)
                       || (PROTIMER_seqWrapCounterOverflowInterrupt.Value && PROTIMER_seqWrapCounterOverflowInterruptEnable.Value)
                       || (PROTIMER_seqListenBeforeTalkSuccessInterrupt.Value && PROTIMER_seqListenBeforeTalkSuccessInterruptEnable.Value)
                       || (PROTIMER_seqListenBeforeTalkFailureInterrupt.Value && PROTIMER_seqListenBeforeTalkFailureInterruptEnable.Value)
                       || (PROTIMER_seqListenBeforeTalkRetryInterrupt.Value && PROTIMER_seqListenBeforeTalkRetryInterruptEnable.Value)
                       || (PROTIMER_seqListenBeforeTalkTimeoutCounterMatchInterrupt.Value && PROTIMER_seqListenBeforeTalkTimeoutCounterMatchInterruptEnable.Value)
                       || (PROTIMER_seqRtccSynchedInterrupt.Value && PROTIMER_seqRtccSynchedInterruptEnable.Value);
                Array.ForEach(PROTIMER_timeoutCounter, x => irq |= x.SeqInterrupt);
                Array.ForEach(PROTIMER_captureCompareChannel, x => irq |= x.SeqInterrupt);
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    protocolTimerRegistersCollection.TryRead((long)ProtocolTimerRegisters.SequencerInterruptFlags, out interruptFlag);
                    protocolTimerRegistersCollection.TryRead((long)ProtocolTimerRegisters.SequencerInterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ PROTIMER set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                SeqProtocolTimerIRQ.Set(irq);

                // AGC interrupt
                irq = ((AGC_seqRssiValidInterrupt.Value && AGC_seqRssiValidInterruptEnable.Value)
                       || (AGC_seqCcaInterrupt.Value && AGC_seqCcaInterruptEnable.Value)
                       || (AGC_seqRssiDoneInterrupt.Value && AGC_seqRssiDoneInterruptEnable.Value)
                       || (AGC_seqRssiHighInterrupt.Value && AGC_seqRssiHighInterruptEnable.Value)
                       || (AGC_seqRssiLowInterrupt.Value && AGC_seqRssiLowInterruptEnable.Value)
                       || (AGC_seqCcaNotDetectedInterrupt.Value && AGC_seqCcaNotDetectedInterruptEnable.Value));
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    automaticGainControlRegistersCollection.TryRead((long)AutomaticGainControlRegisters.SequencerInterruptFlags, out interruptFlag);
                    automaticGainControlRegistersCollection.TryRead((long)AutomaticGainControlRegisters.SequencerInterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ AGC set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                SeqAutomaticGainControlIRQ.Set(irq);

                // RFMAILBOX interrupt
                irq = false;
                for(index = 0; index < MailboxMessageNumber; index++)
                {
                    if(RFMAILBOX_messageInterrupt[index].Value && RFMAILBOX_messageInterruptEnable[index].Value)
                    {
                        irq = true;
                        break;
                    }
                }
                if(irq)
                {
                    var interruptFlag = 0U;
                    var interruptEnable = 0U;
                    radioMailboxRegistersCollection.TryRead((long)RadioMailboxRegisters.InterruptFlags, out interruptFlag);
                    radioMailboxRegistersCollection.TryRead((long)RadioMailboxRegisters.InterruptEnable, out interruptEnable);
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ RFMAILBOX set (IF=0x{1:X}, IEN=0x{2:X})", this.GetTime(), interruptFlag, interruptEnable);
                }
                SeqRfMailboxIRQ.Set(irq);

                // SW0 interrupt
                irq = ((RAC_softwareIrq.Value & 0x1) > 0);
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ SW0 set", this.GetTime());
                }
                SeqSoftware0IRQ.Set(irq);

                // SW1 interrupt
                irq = ((RAC_softwareIrq.Value & 0x2) > 0);
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ SW1 set", this.GetTime());
                }
                SeqSoftware1IRQ.Set(irq);

                // Sequencer Radio State Machine interrupts
                irq = RAC_seqStateOffInterrupt.Value && RAC_seqStateOffInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ OFF set", this.GetTime());
                }
                SeqOffIRQ.Set(irq);

                irq = RAC_seqStateRxWarmInterrupt.Value && RAC_seqStateRxWarmInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ RX_WARM set", this.GetTime());
                }
                SeqRxWarmIRQ.Set(irq);

                irq = RAC_seqStateRxSearchInterrupt.Value && RAC_seqStateRxSearchInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ RX_SEARCH set", this.GetTime());
                }
                SeqRxSearchIRQ.Set(irq);

                irq = RAC_seqStateRxFrameInterrupt.Value && RAC_seqStateRxFrameInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ RX_FRAME set", this.GetTime());
                }
                SeqRxFrameIRQ.Set(irq);

                irq = RAC_seqStateRxPoweringDownInterrupt.Value && RAC_seqStateRxPoweringDownInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ RX_PD set", this.GetTime());
                }
                SeqRxPoweringDownIRQ.Set(irq);

                irq = RAC_seqStateRx2RxInterrupt.Value && RAC_seqStateRx2RxInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ RX_2_RX set", this.GetTime());
                }
                SeqRx2RxIRQ.Set(irq);

                irq = RAC_seqStateRxOverflowInterrupt.Value && RAC_seqStateRxOverflowInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ RX_OVERFLOW set", this.GetTime());
                }
                SeqRxOverflowIRQ.Set(irq);

                irq = RAC_seqStateRx2TxInterrupt.Value && RAC_seqStateRx2TxInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ RX_2_TX set", this.GetTime());
                }
                SeqRx2TxIRQ.Set(irq);

                irq = RAC_seqStateTxWarmInterrupt.Value && RAC_seqStateTxWarmInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ TX_WARM set", this.GetTime());
                }
                SeqTxWarmIRQ.Set(irq);

                irq = RAC_seqStateTxInterrupt.Value && RAC_seqStateTxInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ TX set", this.GetTime());
                }
                SeqTxIRQ.Set(irq);

                irq = RAC_seqStateTxPoweringDownInterrupt.Value && RAC_seqStateTxPoweringDownInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ TX_PD set", this.GetTime());
                }
                SeqTxPoweringDownIRQ.Set(irq);

                irq = RAC_seqStateTx2RxInterrupt.Value && RAC_seqStateTx2RxInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ TX_2_RX set", this.GetTime());
                }
                SeqTx2RxIRQ.Set(irq);

                irq = RAC_seqStateTx2TxInterrupt.Value && RAC_seqStateTx2TxInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ TX_2_TX set", this.GetTime());
                }
                SeqTx2TxIRQ.Set(irq);

                irq = RAC_seqStateShutDownInterrupt.Value && RAC_seqStateShutDownInterruptEnable.Value;
                if(irq)
                {
                    this.Log(LogLevel.Debug, "{0}: SEQ_IRQ SHUTDOWN set", this.GetTime());
                }
                SeqShutdownIRQ.Set(irq);
            });
        }

        public void ReceiveFrame(byte[] frame, IRadio sender)
        {
            // Sync time before taking the radio lock, not inside it, so that this path never holds the
            // time source lock and the radio lock at the same time.
            TrySyncTime();
            machine.ClockSource.ExecuteInLock(() => TryReceiveFrame(frame, sender, rxStartedMidPacket: false, syncTime: false));
        }

        public void PROTIMER_PrsRtcTrigger()
        {
            // Ignore the trigger if we are not in the RTCWAIT state
            if(PROTIMER_rtcWait && PROTIMER_rtccTriggerFromPrsEnable.Value)
            {
                this.Log(LogLevel.Debug, "PROTIMER RTC trigger at {0}, restarting PROTIMER", GetTime());
                PROTIMER_rtccSynchedInterrupt.Value = true;
                PROTIMER_seqRtccSynchedInterrupt.Value = true;
                PROTIMER_rtcWait = false;
                PROTIMER_Enabled = true;
                UpdateInterrupts();
            }
        }

        public void PROTIMER_PrsLbtStart()
        {
            if(PROTIMER_listenBeforeTalkPrsStartEnable.Value)
            {
                this.Log(LogLevel.Debug, "PROTIMER PRS LBT start at {0}", GetTime());
                PROTIMER_ListenBeforeTalkStartCommand();
            }
        }

        public void PROTIMER_PrsLbtStop()
        {
            if(PROTIMER_listenBeforeTalkPrsStopEnable.Value)
            {
                this.Log(LogLevel.Debug, "PROTIMER PRS LBT stop at {0}", GetTime());
                PROTIMER_ListenBeforeTalkStopCommand();
            }
        }

        public void PROTIMER_PrsLbtPause()
        {
            if(PROTIMER_listenBeforeTalkPrsPauseEnable.Value)
            {
                this.Log(LogLevel.Debug, "PROTIMER PRS LBT pause at {0}", GetTime());
                PROTIMER_ListenBeforeTalkPauseCommand();
            }
        }

        public void PROTIMER_TriggerEvent(PROTIMER_Event ev)
        {
            if(ev < PROTIMER_Event.PreCounterOverflow || ev > PROTIMER_Event.InternalTrigger)
            {
                this.Log(LogLevel.Error, "Unreachable. Invalid event value for PROTIMER_TriggerEvent.");
                return;
            }

            if(ev >= PROTIMER_Event.CaptureCompareChannel0Event && ev <= PROTIMER_Event.CaptureCompareChannel4Event)
            {
                uint cc = (uint)ev - (uint)PROTIMER_Event.CaptureCompareChannel0Event;
                this.Log(LogLevel.Noisy, "PROTIMER Triggering PRS Capture Compare {0} event at {1}", cc, GetTime());
                PROTIMER_CaptureComparePrsEvent[cc]?.Invoke();
            }

            // if a Timeout Counter 0 match occurs during LBT, we change the event accordingly.
            if(ev == PROTIMER_Event.TimeoutCounter0Match && PROTIMER_listenBeforeTalkRunning.Value)
            {
                ev = PROTIMER_Event.TimeoutCounter0MatchListenBeforeTalk;
                PROTIMER_listenBeforeTalkTimeoutCounterMatchInterrupt.Value = true;
                PROTIMER_seqListenBeforeTalkTimeoutCounterMatchInterrupt.Value = true;
                UpdateInterrupts();
            }

            switch(PROTIMER_rxRequestState)
            {
            case PROTIMER_TxRxRequestState.Idle:
            {
                if(PROTIMER_rxSetEvent1.Value == PROTIMER_Event.Always)
                {
                    PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.SetEvent1;
                    goto case PROTIMER_TxRxRequestState.SetEvent1;
                }
                if(PROTIMER_rxSetEvent1.Value == ev)
                {
                    PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.SetEvent1;
                }
                break;
            }
            case PROTIMER_TxRxRequestState.SetEvent1:
            {
                if(PROTIMER_rxSetEvent2.Value == PROTIMER_Event.Always || PROTIMER_rxSetEvent2.Value == ev)
                {
                    PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.Set;
                    RAC_UpdateRadioStateMachine();
                }
                break;
            }
            case PROTIMER_TxRxRequestState.Set:
            {
                if(PROTIMER_rxClearEvent1.Value == PROTIMER_Event.Always)
                {
                    PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.ClearEvent1;
                    goto case PROTIMER_TxRxRequestState.ClearEvent1;
                }
                if(PROTIMER_rxClearEvent1.Value == ev)
                {
                    PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.ClearEvent1;
                }
                break;
            }
            case PROTIMER_TxRxRequestState.ClearEvent1:
            {
                if(PROTIMER_rxClearEvent2.Value == ev)
                {
                    PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.Idle;
                    RAC_UpdateRadioStateMachine();
                }
                break;
            }
            default:
                this.Log(LogLevel.Error, "Unreachable. Invalid PROTIMER RX Request state.");
                return;
            }

            switch(PROTIMER_txRequestState)
            {
            case PROTIMER_TxRxRequestState.Idle:
            {
                if(PROTIMER_txSetEvent1.Value == PROTIMER_Event.Always)
                {
                    PROTIMER_txRequestState = PROTIMER_TxRxRequestState.SetEvent1;
                    goto case PROTIMER_TxRxRequestState.SetEvent1;
                }
                if(PROTIMER_txSetEvent1.Value == ev)
                {
                    PROTIMER_txRequestState = PROTIMER_TxRxRequestState.SetEvent1;
                }
                break;
            }
            case PROTIMER_TxRxRequestState.SetEvent1:
            {
                if(PROTIMER_txSetEvent2.Value == ev)
                {
                    PROTIMER_txRequestState = PROTIMER_TxRxRequestState.Set;
                    PROTIMER_TxEnable = true;
                    goto case PROTIMER_TxRxRequestState.Set;
                }
                break;
            }
            case PROTIMER_TxRxRequestState.Set:
            {
                PROTIMER_TxEnable = false;
                PROTIMER_txRequestState = PROTIMER_TxRxRequestState.Idle;
                break;
            }
            default:
                this.Log(LogLevel.Error, "Unreachable. Invalid PROTIMER TX Request state.");
                return;
            }
        }

        public void PROTIMER_HandleChangedParams()
        {
            // Timer is not running, nothing to do
            if(!PROTIMER_Enabled)
            {
                return;
            }

            TrySyncTime();
            uint currentIncrement = (uint)proTimer.Value;
            proTimer.Enabled = false;

            // First handle the current increment
            if(currentIncrement > 0)
            {
                PROTIMER_HandlePreCntOverflows(currentIncrement);
            }

            // Then restart the protimer
            proTimer.Value = 0;
            proTimer.Limit = PROTIMER_ComputeTimerLimit();
            proTimer.Enabled = true;
        }

        // Main core IRQs
        public GPIO FrameControllerPrioritizedIRQ { get; }

        public GPIO FrameControllerIRQ { get; }

        public GPIO ModulatorAndDemodulatorIRQ { get; }

        public GPIO RadioControllerSequencerIRQ { get; }

        public GPIO RadioControllerRadioStateMachineIRQ { get; }

        public GPIO SeqTxPoweringDownIRQ { get; }

        public GPIO ProtocolTimerIRQ { get; }

        public GPIO SynthesizerIRQ { get; }

        // Sequencer core IRQs
        public GPIO SeqShutdownIRQ { get; }

        public GPIO SeqRadioControllerIRQ { get; }

        public GPIO SeqFrameControllerIRQ { get; }

        public GPIO SeqFrameControllerPriorityIRQ { get; }

        public GPIO SeqModulatorAndDemodulatorIRQ { get; }

        public GPIO SeqAutomaticGainControlIRQ { get; }

        public GPIO SeqSynthesizerIRQ { get; }

        public GPIO SeqRfMailboxIRQ { get; }

        public GPIO SeqSoftware0IRQ { get; }

        public GPIO SeqSoftware1IRQ { get; }

        public GPIO SeqTx2TxIRQ { get; }

        public GPIO SeqProtocolTimerIRQ { get; }

        public GPIO SeqTx2RxIRQ { get; }

        public GPIO AutomaticGainControlIRQ { get; }

        public GPIO SeqTxIRQ { get; }

        public GPIO SeqRx2RxIRQ { get; }

        public GPIO SeqOffIRQ { get; }

        public GPIO SeqTxWarmIRQ { get; }

        public GPIO SeqRxPoweringDownIRQ { get; }

        public GPIO SeqRxWarmIRQ { get; }

        public GPIO HostMailboxIRQ { get; }

        public GPIO SeqRxOverflowIRQ { get; }

        public GPIO SeqRxSearchIRQ { get; }

        public GPIO SeqRx2TxIRQ { get; }

        public GPIO SeqRxFrameIRQ { get; }

        // Routing equality key used by WirelessMedium and InterferenceQueue. Derived from
        // the LPW SYNTH/RAC registers (FCW + LO divider) and quantized to 10 kHz so that
        // two radios tuned to the same RF report the same Channel id, even if the FCW
        // is rounded slightly differently.
        public int Channel
        {
            get { return (int)(SYNTH_GetQuantizedRfFrequency() / 10_000UL); }

            set { }
        }

        public bool ForceBusyRssi
        {
            set
            {
                InterferenceQueue.ForceBusyRssi = value;
            }
        }

        public uint PROTIMER_WrapCounterValue
        {
            get
            {
                ulong ret = PROTIMER_wrapCounterValue;
                if(PROTIMER_wrapCounterSource.Value == PROTIMER_WrapCounterSource.PreCounterOverflow
                    && proTimer.Enabled)
                {
                    TrySyncTime();
                    ret += proTimer.Value;
                }
                return (uint)ret;
            }

            set
            {
                PROTIMER_wrapCounterValue = value;
            }
        }

        public ushort PROTIMER_BaseCounterValue
        {
            get
            {
                ulong ret = PROTIMER_baseCounterValue;
                if(PROTIMER_baseCounterSource.Value == PROTIMER_BaseCounterSource.PreCounterOverflow
                    && proTimer.Enabled)
                {
                    TrySyncTime();
                    ret += proTimer.Value;
                }
                return (ushort)ret;
            }

            set
            {
                PROTIMER_baseCounterValue = value;
            }
        }

        public event Action<IRadio, byte[]> FrameSent;

        public event Action<byte[]> PtiDataOut;

        public event Action<SiLabs_PacketTraceFrameType> PtiFrameStart;

        public event Action PtiFrameComplete;

        public Action[] PROTIMER_CaptureComparePrsEvent = new Action[PROTIMER_NumberOfCaptureCompareChannelPrsEvents];

        public bool LogBasicRadioActivityAsError = false;

        private TimeInterval GetTime() => machine.LocalTimeSource.ElapsedVirtualTime;

        // #region agent log
        private static readonly Dictionary<string, string> DbgNewRegs = System.IO.File.Exists("/tmp/new_regs.txt")
            ? System.IO.File.ReadAllLines("/tmp/new_regs.txt").Select(l => l.Split(' ')).ToDictionary(p => p[0], p => p.Length > 1 && p[1] != "" ? p[1] : "0x0")
            : new Dictionary<string, string>();
        private readonly HashSet<string> dbgSeen = new HashSet<string>();
        private static readonly HashSet<string> DbgZeroRegs = new HashSet<string>((Environment.GetEnvironmentVariable("DBG_ZERO_REGS") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        private static readonly string DbgRunId = Environment.GetEnvironmentVariable("DBG_RUN_ID") ?? "pre-fix";
        private void DbgLog<T>(string hyp, string kind, long offset, uint value)
        {
            var key = typeof(T).Name + "." + Enum.GetName(typeof(T), offset);
            if(!DbgNewRegs.TryGetValue(key, out var reset)) { return; }
            if(kind == "read" && value == 0) { return; }
            var cpuName = "?"; ulong pc = 0;
            if(machine.SystemBus.TryGetCurrentCPU(out var c)) { cpuName = c.GetName(); pc = c.PC.RawValue; }
            var dedup = kind + "|" + key + "|" + value + "|" + cpuName;
            if(!dbgSeen.Add(dedup)) { return; }
            EmulationManager.Instance.CurrentEmulation.TryGetMachineName(machine, out var mname);
            var line = $"{{\"sessionId\":\"11fa6e\",\"runId\":\"{DbgRunId}\",\"hypothesisId\":\"{hyp}\",\"location\":\"SiLabs_xG24_LPW.cs:{kind}\",\"message\":\"new-reg {kind}\",\"data\":{{\"node\":\"{mname}\",\"cpu\":\"{cpuName}\",\"pc\":\"0x{pc:X}\",\"reg\":\"{key}\",\"value\":\"0x{value:X8}\",\"reset\":\"{reset}\",\"vtime\":\"{GetTime()}\"}},\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
            lock(DbgNewRegs) { System.IO.File.AppendAllText("/Users/mananni/SilabsWorkspace/renode_work/.cursor/debug-11fa6e.log", line + "\n"); }
        }
        // #endregion

        private bool TrySyncTime()
        {
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu))
            {
                cpu.SyncTime();
                return true;
            }
            return false;
        }

        private bool SequencerIsRunning()
        {
            return (SeqOffIRQ.IsSet || SeqRxWarmIRQ.IsSet || SeqRxSearchIRQ.IsSet || SeqRxFrameIRQ.IsSet
                    || SeqRxPoweringDownIRQ.IsSet || SeqRx2RxIRQ.IsSet || SeqRxOverflowIRQ.IsSet || SeqRx2TxIRQ.IsSet
                    || SeqTxWarmIRQ.IsSet || SeqTxIRQ.IsSet || SeqTxPoweringDownIRQ.IsSet || SeqTx2RxIRQ.IsSet
                    || SeqTx2TxIRQ.IsSet || SeqShutdownIRQ.IsSet);
        }

        private void Write<T>(DoubleWordRegisterCollection registersCollection, string regionName, long offset, uint value)
        where T : struct, IComparable, IFormattable
        {
            machine.ClockSource.ExecuteInLock(delegate
            {
                var internal_offset = offset;
                var internal_value = value;

                if(offset >= SetRegisterOffset && offset < ClearRegisterOffset)
                {
                    // Set register
                    internal_offset = offset - SetRegisterOffset;
                    var old_value = Read<T>(registersCollection, regionName, internal_offset, true);
                    internal_value = old_value | value;
                    this.Log(LogLevel.Noisy, "SET Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}, SET_value=0x{3:X}, old_value=0x{4:X}, new_value=0x{5:X}", Enum.Format(typeof(T), internal_offset, "G"), offset, internal_offset, value, old_value, internal_value);
                }
                else if(offset >= ClearRegisterOffset && offset < ToggleRegisterOffset)
                {
                    // Clear register
                    internal_offset = offset - ClearRegisterOffset;
                    var old_value = Read<T>(registersCollection, regionName, internal_offset, true);
                    internal_value = old_value & ~value;
                    this.Log(LogLevel.Noisy, "CLEAR Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}, CLEAR_value=0x{3:X}, old_value=0x{4:X}, new_value=0x{5:X}", Enum.Format(typeof(T), internal_offset, "G"), offset, internal_offset, value, old_value, internal_value);
                }
                else if(offset >= ToggleRegisterOffset)
                {
                    // Toggle register
                    internal_offset = offset - ToggleRegisterOffset;
                    var old_value = Read<T>(registersCollection, regionName, internal_offset, true);
                    internal_value = old_value ^ value;
                    this.Log(LogLevel.Noisy, "TOGGLE Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}, TOGGLE_value=0x{3:X}, old_value=0x{4:X}, new_value=0x{5:X}", Enum.Format(typeof(T), internal_offset, "G"), offset, internal_offset, value, old_value, internal_value);
                }

                this.Log(LogLevel.Debug, "{0}: Write to {1} at offset 0x{2:X} ({3}), value 0x{4:X}",
                        this.GetTime(), regionName, internal_offset, Enum.Format(typeof(T), internal_offset, "G"), internal_value);
                // #region agent log
                DbgLog<T>("B", "write", internal_offset, internal_value);
                // #endregion

                if(!registersCollection.TryWrite(internal_offset, internal_value))
                {
                    this.Log(LogLevel.Warning, "Unhandled write to {0} at offset 0x{1:X} ({2}), value 0x{3:X}.", regionName, internal_offset, Enum.Format(typeof(T), internal_offset, "G"), internal_value);
                    return;
                }
            });
        }

        private uint Read<T>(DoubleWordRegisterCollection registersCollection, string regionName, long offset, bool internal_read = false)
        where T : struct, IComparable, IFormattable
        {
            var result = 0U;
            machine.ClockSource.ExecuteInLock(() => result = ReadInner<T>(registersCollection, regionName, offset, internal_read));
            return result;
        }

        private uint ReadInner<T>(DoubleWordRegisterCollection registersCollection, string regionName, long offset, bool internal_read)
        where T : struct, IComparable, IFormattable
        {
            var result = 0U;
            long internal_offset = offset;

            // Set, Clear, Toggle registers should only be used for write operations. But just in case we convert here as well.
            if(offset >= SetRegisterOffset && offset < ClearRegisterOffset)
            {
                // Set register
                internal_offset = offset - SetRegisterOffset;
                if(!internal_read)
                {
                    this.Log(LogLevel.Noisy, "SET Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}", Enum.Format(typeof(T), internal_offset, "G"), offset, internal_offset);
                }
            }
            else if(offset >= ClearRegisterOffset && offset < ToggleRegisterOffset)
            {
                // Clear register
                internal_offset = offset - ClearRegisterOffset;
                if(!internal_read)
                {
                    this.Log(LogLevel.Noisy, "CLEAR Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}", Enum.Format(typeof(T), internal_offset, "G"), offset, internal_offset);
                }
            }
            else if(offset >= ToggleRegisterOffset)
            {
                // Toggle register
                internal_offset = offset - ToggleRegisterOffset;
                if(!internal_read)
                {
                    this.Log(LogLevel.Noisy, "TOGGLE Operation on {0}, offset=0x{1:X}, internal_offset=0x{2:X}", Enum.Format(typeof(T), internal_offset, "G"), offset, internal_offset);
                }
            }

            if(!registersCollection.TryRead(internal_offset, out result))
            {
                if(!internal_read)
                {
                    this.Log(LogLevel.Warning, "Unhandled read from {0} at offset 0x{1:X} ({2}).", regionName, internal_offset, Enum.Format(typeof(T), internal_offset, "G"));
                }
            }
            else
            {
                if(!internal_read)
                {
                    this.Log(LogLevel.Debug, "{0}: Read from {1} at offset 0x{2:X} ({3}), returned 0x{4:X}",
                             this.GetTime(), regionName, internal_offset, Enum.Format(typeof(T), internal_offset, "G"), result);
                    // #region agent log
                    DbgLog<T>("A", "read", internal_offset, result);
                    if(DbgZeroRegs.Contains(typeof(T).Name + "." + Enum.GetName(typeof(T), internal_offset))) { result = 0; }
                    // #endregion
                }
            }

            return result;
        }

        private byte ReadByte<T>(DoubleWordRegisterCollection registersCollection, string regionName, long offset)
        where T : struct, IComparable, IFormattable
        {
            var byteOffset = (int)(offset & 0x3);
            // TODO: single byte reads are treated as internal reads for now to avoid flooding the log during debugging.
            var registerValue = Read<T>(registersCollection, regionName, offset - byteOffset, true);
            var result = (byte)((registerValue >> byteOffset*8) & 0xFF);
            return result;
        }

        private DoubleWordRegisterCollection BuildHostMailboxRegistersCollection()
        {
            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            {
                {(long)HostMailboxRegisters.MessagePointer0, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out HOSTMAILBOX_messagePointer[0], name: "MSGPTR0")
                },
                {(long)HostMailboxRegisters.MessagePointer1, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out HOSTMAILBOX_messagePointer[1], name: "MSGPTR1")
                },
                {(long)HostMailboxRegisters.MessagePointer2, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out HOSTMAILBOX_messagePointer[2], name: "MSGPTR2")
                },
                {(long)HostMailboxRegisters.MessagePointer3, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out HOSTMAILBOX_messagePointer[3], name: "MSGPTR3")
                },
                {(long)HostMailboxRegisters.InterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out HOSTMAILBOX_messageInterrupt[0], name: "MBOXIF0")
                    .WithFlag(1, out HOSTMAILBOX_messageInterrupt[1], name: "MBOXIF1")
                    .WithFlag(2, out HOSTMAILBOX_messageInterrupt[2], name: "MBOXIF2")
                    .WithFlag(3, out HOSTMAILBOX_messageInterrupt[3], name: "MBOXIF3")
                    .WithReservedBits(4, 28)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)HostMailboxRegisters.InterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out HOSTMAILBOX_messageInterruptEnable[0], name: "MBOXIEN0")
                    .WithFlag(1, out HOSTMAILBOX_messageInterruptEnable[1], name: "MBOXIEN1")
                    .WithFlag(2, out HOSTMAILBOX_messageInterruptEnable[2], name: "MBOXIEN2")
                    .WithFlag(3, out HOSTMAILBOX_messageInterruptEnable[3], name: "MBOXIEN3")
                    .WithReservedBits(4, 28)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
            };

            return new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private DoubleWordRegisterCollection BuildAutomaticGainControlRegistersCollection()
        {
            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            {
                {(long)AutomaticGainControlRegisters.IpVersion, new DoubleWordRegister(this, 0x00000003)
                    .WithValueField(0, 32, FieldMode.Read, name: "IPVERSION")
                },
                {(long)AutomaticGainControlRegisters.Enable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "EN")
                    .WithReservedBits(1, 31)
                },
                {(long)AutomaticGainControlRegisters.Status0, new DoubleWordRegister(this)
                    .WithValueField(0, 6, name: "GAININDEX")
                    .WithFlag(6, name: "RFPKDLOWLAT")
                    .WithFlag(7, name: "RFPKDHILAT")
                    .WithFlag(8, name: "IFPKDLOLAT")
                    .WithFlag(9, name: "IFPKDHILAT")
                    .WithFlag(10, out AGC_cca, FieldMode.Read, name: "CCA")
                    .WithFlag(11, name: "GAINOK")
                    .WithValueField(12, 4, name: "PGAINDEX")
                    .WithValueField(16, 4, name: "LNAINDEX")
                    .WithValueField(20, 5, name: "PNIINDEX")
                    .WithValueField(25, 2, name: "ADCINDEX")
                    .WithReservedBits(27, 5)
                },
                {(long)AutomaticGainControlRegisters.Status1, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "CHPWR")
                    .WithReservedBits(8, 1)
                    .WithValueField(9, 4, name: "FASTLOOPSTATE")
                    .WithValueField(13, 2, name: "CFLOOPSTATE")
                    .WithEnumField<DoubleWordRegister, AGC_RssiState>(15, 3, out AGC_rssiState, name: "RSSISTATE")
                    .WithValueField(18, 12, name: "CFLOOPSTATE")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.InterruptFlags, new DoubleWordRegister(this)
                  .WithFlag(0, out AGC_rssiValidInterrupt, name: "RSSIVALIDIF")
                  .WithReservedBits(1, 1)
                  .WithFlag(2, out AGC_ccaInterrupt, name: "CCAIF")
                  .WithFlag(3, name: "RSSIPOSSTEPIF")
                  .WithFlag(4, name: "RSSINEGSTEPIF")
                  .WithFlag(5, out AGC_rssiDoneInterrupt, name: "RSSIDONEIF")
                  .WithFlag(6, name: "SHORTRSSIPOSSTEPIF")
                  .WithReservedBits(7, 1)
                  .WithFlag(8, name: "RFPKDPRDDONEIF")
                  .WithFlag(9, name: "RFPKDCNTDONEIF")
                  .WithFlag(10, out AGC_rssiHighInterrupt, name: "RSSIHIGHIF")
                  .WithFlag(11, out AGC_rssiLowInterrupt, name: "RSSILOWIF")
                  .WithFlag(12, out AGC_ccaNotDetectedInterrupt, name: "CCANODETIF")
                  .WithFlag(13, name: "GAINBELOWGAINTHDIF")
                  .WithFlag(14, name: "GAINUPDATEFRZIF")
                  .WithReservedBits(15, 17)
                },
                {(long)AutomaticGainControlRegisters.InterruptEnable, new DoubleWordRegister(this)
                  .WithFlag(0, out AGC_rssiValidInterruptEnable, name: "RSSIVALIDIEN")
                  .WithReservedBits(1, 1)
                  .WithFlag(2, out AGC_ccaInterruptEnable, name: "CCAIEN")
                  .WithFlag(3, name: "RSSIPOSSTEPIEN")
                  .WithFlag(4, name: "RSSINEGSTEPIEN")
                  .WithFlag(5, out AGC_rssiDoneInterruptEnable, name: "RSSIDONEIEN")
                  .WithFlag(6, name: "SHORTRSSIPOSSTEPIEN")
                  .WithReservedBits(7, 1)
                  .WithFlag(8, name: "RFPKDPRDDONEIEN")
                  .WithFlag(9, name: "RFPKDCNTDONEIEN")
                  .WithFlag(10, out AGC_rssiHighInterruptEnable, name: "RSSIHIGHIEN")
                  .WithFlag(11, out AGC_rssiLowInterruptEnable, name: "RSSILOWIEN")
                  .WithFlag(12, out AGC_ccaNotDetectedInterruptEnable, name: "CCANODETIEN")
                  .WithFlag(13, name: "GAINBELOWGAINTHDIEN")
                  .WithFlag(14, name: "GAINUPDATEFRZIEN")
                  .WithReservedBits(15, 17)
                },
                {(long)AutomaticGainControlRegisters.SequencerInterruptFlags, new DoubleWordRegister(this)
                  .WithFlag(0, out AGC_seqRssiValidInterrupt, name: "RSSIVALIDSEQIF")
                  .WithReservedBits(1, 1)
                  .WithFlag(2, out AGC_seqCcaInterrupt, name: "CCASEQIF")
                  .WithFlag(3, name: "RSSIPOSSTEPSEQIF")
                  .WithFlag(4, name: "RSSINEGSTEPSEQIF")
                  .WithFlag(5, out AGC_seqRssiDoneInterrupt, name: "RSSIDONESEQIF")
                  .WithFlag(6, name: "SHORTRSSIPOSSTEPSEQIF")
                  .WithReservedBits(7, 1)
                  .WithFlag(8, name: "RFPKDPRDDONESEQIF")
                  .WithFlag(9, name: "RFPKDCNTDONESEQIF")
                  .WithFlag(10, out AGC_seqRssiHighInterrupt, name: "RSSIHIGHSEQIF")
                  .WithFlag(11, out AGC_seqRssiLowInterrupt, name: "RSSILOWSEQIF")
                  .WithFlag(12, out AGC_seqCcaNotDetectedInterrupt, name: "CCANODETSEQIF")
                  .WithFlag(13, name: "GAINBELOWGAINTHDSEQIF")
                  .WithFlag(14, name: "GAINUPDATEFRZSEQIF")
                  .WithReservedBits(15, 17)
                },
                {(long)AutomaticGainControlRegisters.SequencerInterruptEnable, new DoubleWordRegister(this)
                  .WithFlag(0, out AGC_seqRssiValidInterruptEnable, name: "RSSIVALIDSEQIEN")
                  .WithReservedBits(1, 1)
                  .WithFlag(2, out AGC_seqCcaInterruptEnable, name: "CCASEQIEN")
                  .WithFlag(3, name: "RSSIPOSSTEPSEQIEN")
                  .WithFlag(4, name: "RSSINEGSTEPSEQIEN")
                  .WithFlag(5, out AGC_seqRssiDoneInterruptEnable, name: "RSSIDONESEQIEN")
                  .WithFlag(6, name: "SHORTRSSIPOSSTEPSEQIEN")
                  .WithReservedBits(7, 1)
                  .WithFlag(8, name: "RFPKDPRDDONESEQIEN")
                  .WithFlag(9, name: "RFPKDCNTDONESEQIEN")
                  .WithFlag(10, out AGC_seqRssiHighInterruptEnable, name: "RSSIHIGHSEQIEN")
                  .WithFlag(11, out AGC_seqRssiLowInterruptEnable, name: "RSSILOWSEQIEN")
                  .WithFlag(12, out AGC_seqCcaNotDetectedInterruptEnable, name: "CCANODETSEQIEN")
                  .WithFlag(13, name: "GAINBELOWGAINTHDSEQIEN")
                  .WithFlag(14, name: "GAINUPDATEFRZSEQIEN")
                  .WithReservedBits(15, 17)
                },
                {(long)AutomaticGainControlRegisters.ListenBeforeTalkConfiguration, new DoubleWordRegister(this)
                  .WithValueField(0, 4, out AGC_ccaRssiPeriod, name: "CCARSSIPERIOD")
                  .WithFlag(4, out AGC_ccaRssiPeriodEnable, name: "ENCCARSSIPERIOD")
                  .WithFlag(5, name: "ENCCAGAINREDUCED")
                  .WithFlag(6, name: "ENCCARSSIMAX")
                  .WithReservedBits(7, 25)
                },
                {(long)AutomaticGainControlRegisters.Control0, new DoubleWordRegister(this, 0x2132727F)
                    .WithValueField(0, 8, name: "PWRTARGET")
                    .WithValueField(8, 3, name: "MODE")
                    .WithValueField(11, 8, out AGC_rssiShift, name: "RSSISHIFT")
                    .WithFlag(19, name: "DISCFLOOPADJ")
                    .WithFlag(20, name: "CFLOOPNFADJ")
                    .WithFlag(21, name: "CFLOOPNEWCALC")
                    .WithFlag(22, name: "DISRESETCHPWR")
                    .WithFlag(23, name: "ADCATTENMODE")
                    .WithFlag(24, name: "FENOTCHMODESEL")
                    .WithValueField(25, 2, name: "ADCATTENCODE")
                    .WithFlag(27, name: "ENRSSIRESET")
                    .WithFlag(28, name: "DSADISCFLOOP")
                    .WithFlag(29, name: "DISPNGAINUP")
                    .WithFlag(30, name: "DISPNDWNCOMP")
                    .WithFlag(31, name: "AGCRST")
                },
                {(long)AutomaticGainControlRegisters.Control1, new DoubleWordRegister(this, 0x00001300)
                  .WithValueField(0, 8, out AGC_ccaThreshold, name: "CCATHRSH")
                  .WithValueField(8, 4, out AGC_rssiMeasurePeriod, name: "RSSIPERIOD")
                  .WithValueField(12, 3, out AGC_powerMeasurePeriod, name: "PWRPERIOD")
                  .WithEnumField<DoubleWordRegister, AGC_CcaMode>(15, 2, out AGC_ccaMode, name: "CCAMODE")
                  .WithEnumField<DoubleWordRegister, AGC_CcaMode3Logic>(17, 1, out AGC_ccaMode3Logic, name: "CCAMODE3LOGIC")
                  .WithFlag(18, out AGC_ccaSoftwareControl, name: "CCASWCTRL")
                  .WithReservedBits(19, 13)
                },
                {(long)AutomaticGainControlRegisters.Control7, new DoubleWordRegister(this)
                  .WithValueField(0, 8, name: "SUBDEN")
                  .WithValueField(8, 8, out AGC_subPeriodInteger, name: "SUBINT")
                  .WithValueField(16, 8, name: "SUBNUM")
                  .WithFlag(24, out AGC_subPeriod, name: "SUBPERIOD")
                  .WithReservedBits(25, 7)
                },
                {(long)AutomaticGainControlRegisters.ReceivedSignalStrengthIndicator, new DoubleWordRegister(this, 0x00008000)
                  .WithReservedBits(0, 6)
                  .WithValueField(6, 2, FieldMode.Read, valueProviderCallback: _ => (uint)AGC_RssiFractionalPart, name: "RSSIFRAC")
                  .WithValueField(8, 8, FieldMode.Read, valueProviderCallback: _ => (uint)AGC_LiveRssiIntegerPartAdjusted, name: "RSSIINT")
                  .WithReservedBits(16, 16)
                },
                {(long)AutomaticGainControlRegisters.FrameReceivedSignalStrengthIndicator, new DoubleWordRegister(this, 0x00008000)
                  .WithReservedBits(0, 6)
                  .WithValueField(6, 2, FieldMode.Read, valueProviderCallback: _ => (uint)AGC_FrameRssiFractionalPart, name: "FRAMERSSIFRAC")
                  .WithValueField(8, 8, FieldMode.Read, valueProviderCallback: _ => (uint)AGC_FrameRssiIntegerPartAdjusted, name: "FRAMERSSIINT")
                  .WithReservedBits(16, 16)
                },
                {(long)AutomaticGainControlRegisters.Command, new DoubleWordRegister(this)
                  .WithFlag(0, FieldMode.Set, writeCallback: (_, value) => { if (value) {AGC_RssiStartCommand();} }, name: "RSSISTART")
                  .WithReservedBits(1, 31)
                },
                {(long)AutomaticGainControlRegisters.ReceivedSignalStrengthIndicatorAbsoluteThreshold, new DoubleWordRegister(this)
                  .WithValueField(0, 8, out AGC_rssiHighThreshold, name: "RSSIHIGHTHRSH")
                  .WithValueField(8, 8, out AGC_rssiLowThreshold, name: "RSSILOWTHRSH")
                  .WithValueField(16, 8, name: "SIRSSIHIGHTHR")
                  .WithValueField(24, 8, name: "SIRSSINEGSTEPTHR")
                },
                {(long)AutomaticGainControlRegisters.Status2, new DoubleWordRegister(this)
                    .WithValueField(0, 12, FieldMode.Read, name: "RFPKDHILATCNT")
                    .WithReservedBits(12, 2)
                    .WithFlag(14, FieldMode.Read, name: "PNDWNUP")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 16, FieldMode.Read, name: "RFPKDPRDCNT")
                },
                {(long)AutomaticGainControlRegisters.Control2, new DoubleWordRegister(this, 0x0000610A)
                    .WithFlag(0, name: "DMASEL")
                    .WithFlag(1, name: "SAFEMODE")
                    .WithValueField(2, 3, name: "SAFEMODETHD")
                    .WithValueField(5, 8, name: "REHICNTTHD")
                    .WithValueField(13, 3, name: "RELOTHD")
                    .WithValueField(16, 2, name: "RELBYCHPWR")
                    .WithValueField(18, 8, name: "RELTARGETPWR")
                    .WithValueField(26, 3, name: "RSSICCASUB")
                    .WithFlag(29, name: "DEBCNTRST")
                    .WithFlag(30, name: "PRSDEBUGEN")
                    .WithFlag(31, name: "DISRFPKD")
                },
                {(long)AutomaticGainControlRegisters.Control3, new DoubleWordRegister(this, 0x5140A800)
                    .WithFlag(0, name: "IFPKDDEB")
                    .WithValueField(1, 2, name: "IFPKDDEBTHD")
                    .WithValueField(3, 6, name: "IFPKDDEBPRD")
                    .WithValueField(9, 4, name: "IFPKDDEBRST")
                    .WithFlag(13, name: "RFPKDDEB")
                    .WithValueField(14, 5, name: "RFPKDDEBTHD")
                    .WithValueField(19, 8, name: "RFPKDDEBPRD")
                    .WithValueField(27, 5, name: "RFPKDDEBRST")
                },
                {(long)AutomaticGainControlRegisters.Control4, new DoubleWordRegister(this, 0x0000000E)
                    .WithValueField(0, 16, name: "PERIODRFPKD")
                    .WithReservedBits(16, 7)
                    .WithFlag(23, name: "DISRFPKDCNTRST")
                    .WithFlag(24, name: "DISRSTCONDI")
                    .WithValueField(25, 3, name: "RFPKDPRDGEAR")
                    .WithFlag(28, name: "RFPKDSYNCSEL")
                    .WithFlag(29, name: "RFPKDSEL")
                    .WithFlag(30, name: "FRZPKDEN")
                    .WithFlag(31, name: "RFPKDCNTEN")
                },
                {(long)AutomaticGainControlRegisters.Control5, new DoubleWordRegister(this)
                    .WithValueField(0, 12, name: "PNUPDISTHD")
                    .WithValueField(12, 12, name: "PNUPRELTHD")
                    .WithReservedBits(24, 6)
                    .WithFlag(30, name: "SEQPNUPALLOW")
                    .WithFlag(31, name: "SEQRFPKDEN")
                },
                {(long)AutomaticGainControlRegisters.Control6, new DoubleWordRegister(this, 0x0003AAA8)
                    .WithValueField(0, 18, name: "DUALRFPKDDEC")
                    .WithFlag(18, name: "ENDUALRFPKD")
                    .WithValueField(19, 12, name: "GAINDETTHD")
                    .WithReservedBits(31, 1)
                },
                {(long)AutomaticGainControlRegisters.ReceivedSignalStrengthIndicatorStepThreshold, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "POSSTEPTHR")
                    .WithValueField(8, 8, name: "NEGSTEPTHR")
                    .WithFlag(16, name: "STEPPER")
                    .WithValueField(17, 4, name: "DEMODRESTARTPER")
                    .WithValueField(21, 8, name: "DEMODRESTARTTHR")
                    .WithFlag(29, name: "RSSIFAST")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.ManualGain, new DoubleWordRegister(this)
                    .WithFlag(0, name: "MANGAINEN")
                    .WithValueField(1, 4, name: "MANGAINIFPGA")
                    .WithValueField(5, 4, name: "MANGAINLNA")
                    .WithValueField(9, 5, name: "MANGAINPN")
                    .WithFlag(14, name: "MANRFLATRST")
                    .WithFlag(15, name: "MANIFLOLATRST")
                    .WithFlag(16, name: "MANIFHILATRST")
                    .WithReservedBits(17, 15)
                },
                {(long)AutomaticGainControlRegisters.RxGainRange, new DoubleWordRegister(this, 0x00813187)
                    .WithValueField(0, 4, name: "LNAINDEXBORDER")
                    .WithValueField(4, 4, name: "PGAINDEXBORDER")
                    .WithValueField(8, 4, name: "GAININCSTEP")
                    .WithValueField(12, 4, name: "PNGAINSTEP")
                    .WithValueField(16, 4, name: "LATCHEDHISTEP")
                    .WithValueField(20, 6, name: "HIPWRTHD")
                    .WithReservedBits(26, 6)
                },
                {(long)AutomaticGainControlRegisters.AutomaticGainControlPeriod0, new DoubleWordRegister(this, 0xD607000E)
                    .WithValueField(0, 9, name: "PERIODHI")
                    .WithReservedBits(9, 7)
                    .WithValueField(16, 8, name: "MAXHICNTTHD")
                    .WithValueField(24, 4, name: "SETTLETIMEIF")
                    .WithValueField(28, 4, name: "SETTLETIMERF")
                },
                {(long)AutomaticGainControlRegisters.AutomaticGainControlPeriod1, new DoubleWordRegister(this, 0x00000037)
                    .WithValueField(0, 32, name: "PERIODLOW")
                },
                {(long)AutomaticGainControlRegisters.HiCounterRegion0, new DoubleWordRegister(this, 0x06050403)
                    .WithValueField(0, 8, name: "HICNTREGION0")
                    .WithValueField(8, 8, name: "HICNTREGION1")
                    .WithValueField(16, 8, name: "HICNTREGION2")
                    .WithValueField(24, 8, name: "HICNTREGION3")
                },
                {(long)AutomaticGainControlRegisters.HiCounterRegion1, new DoubleWordRegister(this, 0x00000008)
                    .WithValueField(0, 8, name: "HICNTREGION4")
                    .WithReservedBits(8, 24)
                },
                {(long)AutomaticGainControlRegisters.HiCounterRegion2, new DoubleWordRegister(this, 0x00036D11)
                    .WithValueField(0, 3, name: "STEPDWN0")
                    .WithValueField(3, 3, name: "STEPDWN1")
                    .WithValueField(6, 3, name: "STEPDWN2")
                    .WithValueField(9, 3, name: "STEPDWN3")
                    .WithValueField(12, 3, name: "STEPDWN4")
                    .WithValueField(15, 3, name: "STEPDWN5")
                    .WithReservedBits(18, 14)
                },
                {(long)AutomaticGainControlRegisters.GainStepsLimits0, new DoubleWordRegister(this, 0x00003144)
                    .WithValueField(0, 5, name: "CFLOOPSTEPMAX")
                    .WithValueField(5, 7, name: "CFLOOPDEL")
                    .WithValueField(12, 4, name: "HYST")
                    .WithValueField(16, 8, name: "MAXPWRVAR")
                    .WithFlag(24, name: "TRANRSTAGC")
                    .WithReservedBits(25, 7)
                },
                {(long)AutomaticGainControlRegisters.GainStepsLimits1, new DoubleWordRegister(this, 0x000011BA)
                    .WithValueField(0, 4, name: "LNAINDEXMAX")
                    .WithValueField(4, 4, name: "PGAINDEXMAX")
                    .WithValueField(8, 5, name: "PNINDEXMAX")
                    .WithReservedBits(13, 19)
                },
                {(long)AutomaticGainControlRegisters.PnRfAttenuationCodeGroup0, new DoubleWordRegister(this, 0x00200400)
                    .WithValueField(0, 10, name: "LNAMIXRFATT1")
                    .WithValueField(10, 10, name: "LNAMIXRFATT2")
                    .WithValueField(20, 10, name: "LNAMIXRFATT3")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfAttenuationCodeGroup1, new DoubleWordRegister(this, 0x00801804)
                    .WithValueField(0, 10, name: "LNAMIXRFATT4")
                    .WithValueField(10, 10, name: "LNAMIXRFATT5")
                    .WithValueField(20, 10, name: "LNAMIXRFATT6")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfAttenuationCodeGroup2, new DoubleWordRegister(this, 0x01203C0B)
                    .WithValueField(0, 10, name: "LNAMIXRFATT7")
                    .WithValueField(10, 10, name: "LNAMIXRFATT8")
                    .WithValueField(20, 10, name: "LNAMIXRFATT9")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfAttenuationCodeGroup3, new DoubleWordRegister(this, 0x02107C18)
                    .WithValueField(0, 10, name: "LNAMIXRFATT10")
                    .WithValueField(10, 10, name: "LNAMIXRFATT11")
                    .WithValueField(20, 10, name: "LNAMIXRFATT12")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfAttenuationCodeGroup4, new DoubleWordRegister(this, 0x06E0FC2F)
                    .WithValueField(0, 10, name: "LNAMIXRFATT13")
                    .WithValueField(10, 10, name: "LNAMIXRFATT14")
                    .WithValueField(20, 10, name: "LNAMIXRFATT15")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfAttenuationCodeGroup5, new DoubleWordRegister(this, 0x0180480F)
                    .WithValueField(0, 10, name: "LNAMIXRFATT16")
                    .WithValueField(10, 10, name: "LNAMIXRFATT17")
                    .WithValueField(20, 10, name: "LNAMIXRFATT18")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfAttenuationCodeGroup6, new DoubleWordRegister(this, 0x02F0841F)
                    .WithValueField(0, 10, name: "LNAMIXRFATT19")
                    .WithValueField(10, 10, name: "LNAMIXRFATT20")
                    .WithValueField(20, 10, name: "LNAMIXRFATT21")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfAttenuationCodeGroup7, new DoubleWordRegister(this, 0x07F1B83F)
                    .WithValueField(0, 10, name: "LNAMIXRFATT22")
                    .WithValueField(10, 10, name: "LNAMIXRFATT23")
                    .WithValueField(20, 10, name: "LNAMIXRFATT24")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfAttenuationCodeGroupAlternate, new DoubleWordRegister(this, 0x0000007F)
                    .WithValueField(0, 10, name: "LNAMIXRFATTALT")
                    .WithReservedBits(10, 22)
                },
                {(long)AutomaticGainControlRegisters.LnaMixSliceCodeGroup0, new DoubleWordRegister(this, 0x15724BBD)
                    .WithValueField(0, 6, name: "LNAMIXSLICE1")
                    .WithValueField(6, 6, name: "LNAMIXSLICE2")
                    .WithValueField(12, 6, name: "LNAMIXSLICE3")
                    .WithValueField(18, 6, name: "LNAMIXSLICE4")
                    .WithValueField(24, 6, name: "LNAMIXSLICE5")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.LnaMixSliceCodeGroup1, new DoubleWordRegister(this, 0x0518A311)
                    .WithValueField(0, 6, name: "LNAMIXSLICE6")
                    .WithValueField(6, 6, name: "LNAMIXSLICE7")
                    .WithValueField(12, 6, name: "LNAMIXSLICE8")
                    .WithValueField(18, 6, name: "LNAMIXSLICE9")
                    .WithValueField(24, 6, name: "LNAMIXSLICE10")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.ProgrammableGainAmplifierGainCodeGroup0, new DoubleWordRegister(this, 0x76543210)
                    .WithValueField(0, 4, name: "PGAGAIN1")
                    .WithValueField(4, 4, name: "PGAGAIN2")
                    .WithValueField(8, 4, name: "PGAGAIN3")
                    .WithValueField(12, 4, name: "PGAGAIN4")
                    .WithValueField(16, 4, name: "PGAGAIN5")
                    .WithValueField(20, 4, name: "PGAGAIN6")
                    .WithValueField(24, 4, name: "PGAGAIN7")
                    .WithValueField(28, 4, name: "PGAGAIN8")
                },
                {(long)AutomaticGainControlRegisters.ProgrammableGainAmplifierGainCodeGroup1, new DoubleWordRegister(this, 0x00000A98)
                    .WithValueField(0, 4, name: "PGAGAIN9")
                    .WithValueField(4, 4, name: "PGAGAIN10")
                    .WithValueField(8, 4, name: "PGAGAIN11")
                    .WithReservedBits(12, 20)
                },
                {(long)AutomaticGainControlRegisters.MirrorInterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, name: "RSSIPOSSTEPM")
                    .WithFlag(1, name: "RSSINEGSTEPM")
                    .WithFlag(2, name: "SHORTRSSIPOSSTEPM")
                    .WithFlag(3, name: "IFMIRRORCLEAR")
                    .WithReservedBits(4, 28)
                },
                {(long)AutomaticGainControlRegisters.LnaBoostControl, new DoubleWordRegister(this, 0x000001FE)
                    .WithFlag(0, name: "BOOSTLNA")
                    .WithValueField(1, 4, name: "LNABWADJ")
                    .WithValueField(5, 4, name: "LNABWADJBOOST")
                    .WithReservedBits(9, 23)
                },
                {(long)AutomaticGainControlRegisters.AntennaDiversity, new DoubleWordRegister(this)
                    .WithValueField(0, 2, name: "GAINMODE")
                    .WithValueField(2, 7, name: "DEBOUNCECNTTHD")
                    .WithValueField(9, 2, name: "DISRSSIANTDIVFIX")
                    .WithReservedBits(11, 21)
                },
                {(long)AutomaticGainControlRegisters.DualRfpkdThreshold0, new DoubleWordRegister(this, 0x000A0001)
                    .WithValueField(0, 12, name: "RFPKDLOWTHD0")
                    .WithReservedBits(12, 4)
                    .WithValueField(16, 12, name: "RFPKDLOWTHD1")
                    .WithReservedBits(28, 4)
                },
                {(long)AutomaticGainControlRegisters.DualRfpkdThreshold1, new DoubleWordRegister(this, 0x00280001)
                    .WithValueField(0, 12, name: "RFPKDHITHD0")
                    .WithReservedBits(12, 4)
                    .WithValueField(16, 12, name: "RFPKDHITHD1")
                    .WithReservedBits(28, 4)
                },
                {(long)AutomaticGainControlRegisters.Spare, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "SPAREREG")
                    .WithReservedBits(8, 24)
                },
                {(long)AutomaticGainControlRegisters.PnRfFilterCodeGroup0, new DoubleWordRegister(this, 0x00200400)
                    .WithValueField(0, 10, name: "LNAMIXRFATT1")
                    .WithValueField(10, 10, name: "LNAMIXRFATT2")
                    .WithValueField(20, 10, name: "LNAMIXRFATT3")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfFilterCodeGroup1, new DoubleWordRegister(this, 0x00801804)
                    .WithValueField(0, 10, name: "LNAMIXRFATT4")
                    .WithValueField(10, 10, name: "LNAMIXRFATT5")
                    .WithValueField(20, 10, name: "LNAMIXRFATT6")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfFilterCodeGroup2, new DoubleWordRegister(this, 0x01203C0B)
                    .WithValueField(0, 10, name: "LNAMIXRFATT7")
                    .WithValueField(10, 10, name: "LNAMIXRFATT8")
                    .WithValueField(20, 10, name: "LNAMIXRFATT9")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfFilterCodeGroup3, new DoubleWordRegister(this, 0x02107C18)
                    .WithValueField(0, 10, name: "LNAMIXRFATT10")
                    .WithValueField(10, 10, name: "LNAMIXRFATT11")
                    .WithValueField(20, 10, name: "LNAMIXRFATT12")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfFilterCodeGroup4, new DoubleWordRegister(this, 0x06E0FC2F)
                    .WithValueField(0, 10, name: "LNAMIXRFATT13")
                    .WithValueField(10, 10, name: "LNAMIXRFATT14")
                    .WithValueField(20, 10, name: "LNAMIXRFATT15")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfFilterCodeGroup5, new DoubleWordRegister(this, 0x0180480F)
                    .WithValueField(0, 10, name: "LNAMIXRFATT16")
                    .WithValueField(10, 10, name: "LNAMIXRFATT17")
                    .WithValueField(20, 10, name: "LNAMIXRFATT18")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfFilterCodeGroup6, new DoubleWordRegister(this, 0x02F0841F)
                    .WithValueField(0, 10, name: "LNAMIXRFATT19")
                    .WithValueField(10, 10, name: "LNAMIXRFATT20")
                    .WithValueField(20, 10, name: "LNAMIXRFATT21")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.PnRfFilterCodeGroup7, new DoubleWordRegister(this, 0x07F1B83F)
                    .WithValueField(0, 10, name: "LNAMIXRFATT22")
                    .WithValueField(10, 10, name: "LNAMIXRFATT23")
                    .WithValueField(20, 10, name: "LNAMIXRFATT24")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup0, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL1")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE1")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE1")
                    .WithFlag(12, name: "FENOTCHRATTNEN1")
                    .WithFlag(13, name: "FENOTCHEN1")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL2")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE2")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE2")
                    .WithFlag(28, name: "FENOTCHRATTNEN2")
                    .WithFlag(29, name: "FENOTCHEN2")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup1, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL3")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE3")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE3")
                    .WithFlag(12, name: "FENOTCHRATTNEN3")
                    .WithFlag(13, name: "FENOTCHEN3")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL4")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE4")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE4")
                    .WithFlag(28, name: "FENOTCHRATTNEN4")
                    .WithFlag(29, name: "FENOTCHEN4")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup2, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL5")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE5")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE5")
                    .WithFlag(12, name: "FENOTCHRATTNEN5")
                    .WithFlag(13, name: "FENOTCHEN5")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL6")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE6")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE6")
                    .WithFlag(28, name: "FENOTCHRATTNEN6")
                    .WithFlag(29, name: "FENOTCHEN6")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup3, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL7")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE7")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE7")
                    .WithFlag(12, name: "FENOTCHRATTNEN7")
                    .WithFlag(13, name: "FENOTCHEN7")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL8")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE8")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE8")
                    .WithFlag(28, name: "FENOTCHRATTNEN8")
                    .WithFlag(29, name: "FENOTCHEN8")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup4, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL9")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE9")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE9")
                    .WithFlag(12, name: "FENOTCHRATTNEN9")
                    .WithFlag(13, name: "FENOTCHEN9")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL10")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE10")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE10")
                    .WithFlag(28, name: "FENOTCHRATTNEN10")
                    .WithFlag(29, name: "FENOTCHEN10")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup5, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL11")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE11")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE11")
                    .WithFlag(12, name: "FENOTCHRATTNEN11")
                    .WithFlag(13, name: "FENOTCHEN11")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL12")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE12")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE12")
                    .WithFlag(28, name: "FENOTCHRATTNEN12")
                    .WithFlag(29, name: "FENOTCHEN12")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup6, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL13")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE13")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE13")
                    .WithFlag(12, name: "FENOTCHRATTNEN13")
                    .WithFlag(13, name: "FENOTCHEN13")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL14")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE14")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE14")
                    .WithFlag(28, name: "FENOTCHRATTNEN14")
                    .WithFlag(29, name: "FENOTCHEN14")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup7, new DoubleWordRegister(this, 0x20080000)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL15")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE15")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE15")
                    .WithFlag(12, name: "FENOTCHRATTNEN15")
                    .WithFlag(13, name: "FENOTCHEN15")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL16")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE16")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE16")
                    .WithFlag(28, name: "FENOTCHRATTNEN16")
                    .WithFlag(29, name: "FENOTCHEN16")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup8, new DoubleWordRegister(this, 0x200B200A)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL17")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE17")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE17")
                    .WithFlag(12, name: "FENOTCHRATTNEN17")
                    .WithFlag(13, name: "FENOTCHEN17")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL18")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE18")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE18")
                    .WithFlag(28, name: "FENOTCHRATTNEN18")
                    .WithFlag(29, name: "FENOTCHEN18")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup9, new DoubleWordRegister(this, 0x200B200B)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL19")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE19")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE19")
                    .WithFlag(12, name: "FENOTCHRATTNEN19")
                    .WithFlag(13, name: "FENOTCHEN19")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL20")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE20")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE20")
                    .WithFlag(28, name: "FENOTCHRATTNEN20")
                    .WithFlag(29, name: "FENOTCHEN20")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup10, new DoubleWordRegister(this, 0x200B200B)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL21")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE21")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE21")
                    .WithFlag(12, name: "FENOTCHRATTNEN21")
                    .WithFlag(13, name: "FENOTCHEN21")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL22")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE22")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE22")
                    .WithFlag(28, name: "FENOTCHRATTNEN22")
                    .WithFlag(29, name: "FENOTCHEN22")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchAttenuationCodeGroup11, new DoubleWordRegister(this, 0x200B200B)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL23")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE23")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE23")
                    .WithFlag(12, name: "FENOTCHRATTNEN23")
                    .WithFlag(13, name: "FENOTCHEN23")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL24")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE24")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE24")
                    .WithFlag(28, name: "FENOTCHRATTNEN24")
                    .WithFlag(29, name: "FENOTCHEN24")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup0, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL1")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE1")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE1")
                    .WithFlag(12, name: "FENOTCHRATTNEN1")
                    .WithFlag(13, name: "FENOTCHEN1")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL2")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE2")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE2")
                    .WithFlag(28, name: "FENOTCHRATTNEN2")
                    .WithFlag(29, name: "FENOTCHEN2")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup1, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL3")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE3")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE3")
                    .WithFlag(12, name: "FENOTCHRATTNEN3")
                    .WithFlag(13, name: "FENOTCHEN3")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL4")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE4")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE4")
                    .WithFlag(28, name: "FENOTCHRATTNEN4")
                    .WithFlag(29, name: "FENOTCHEN4")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup2, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL5")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE5")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE5")
                    .WithFlag(12, name: "FENOTCHRATTNEN5")
                    .WithFlag(13, name: "FENOTCHEN5")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL6")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE6")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE6")
                    .WithFlag(28, name: "FENOTCHRATTNEN6")
                    .WithFlag(29, name: "FENOTCHEN6")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup3, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL7")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE7")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE7")
                    .WithFlag(12, name: "FENOTCHRATTNEN7")
                    .WithFlag(13, name: "FENOTCHEN7")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL8")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE8")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE8")
                    .WithFlag(28, name: "FENOTCHRATTNEN8")
                    .WithFlag(29, name: "FENOTCHEN8")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup4, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL9")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE9")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE9")
                    .WithFlag(12, name: "FENOTCHRATTNEN9")
                    .WithFlag(13, name: "FENOTCHEN9")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL10")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE10")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE10")
                    .WithFlag(28, name: "FENOTCHRATTNEN10")
                    .WithFlag(29, name: "FENOTCHEN10")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup5, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL11")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE11")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE11")
                    .WithFlag(12, name: "FENOTCHRATTNEN11")
                    .WithFlag(13, name: "FENOTCHEN11")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL12")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE12")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE12")
                    .WithFlag(28, name: "FENOTCHRATTNEN12")
                    .WithFlag(29, name: "FENOTCHEN12")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup6, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL13")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE13")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE13")
                    .WithFlag(12, name: "FENOTCHRATTNEN13")
                    .WithFlag(13, name: "FENOTCHEN13")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL14")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE14")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE14")
                    .WithFlag(28, name: "FENOTCHRATTNEN14")
                    .WithFlag(29, name: "FENOTCHEN14")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup7, new DoubleWordRegister(this, 0x20080000)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL15")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE15")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE15")
                    .WithFlag(12, name: "FENOTCHRATTNEN15")
                    .WithFlag(13, name: "FENOTCHEN15")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL16")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE16")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE16")
                    .WithFlag(28, name: "FENOTCHRATTNEN16")
                    .WithFlag(29, name: "FENOTCHEN16")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup8, new DoubleWordRegister(this, 0x200B200A)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL17")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE17")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE17")
                    .WithFlag(12, name: "FENOTCHRATTNEN17")
                    .WithFlag(13, name: "FENOTCHEN17")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL18")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE18")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE18")
                    .WithFlag(28, name: "FENOTCHRATTNEN18")
                    .WithFlag(29, name: "FENOTCHEN18")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup9, new DoubleWordRegister(this, 0x200B200B)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL19")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE19")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE19")
                    .WithFlag(12, name: "FENOTCHRATTNEN19")
                    .WithFlag(13, name: "FENOTCHEN19")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL20")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE20")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE20")
                    .WithFlag(28, name: "FENOTCHRATTNEN20")
                    .WithFlag(29, name: "FENOTCHEN20")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup10, new DoubleWordRegister(this, 0x200B200B)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL21")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE21")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE21")
                    .WithFlag(12, name: "FENOTCHRATTNEN21")
                    .WithFlag(13, name: "FENOTCHEN21")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL22")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE22")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE22")
                    .WithFlag(28, name: "FENOTCHRATTNEN22")
                    .WithFlag(29, name: "FENOTCHEN22")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.FeNotchFilterCodeGroup11, new DoubleWordRegister(this, 0x200B200B)
                    .WithValueField(0, 4, name: "FENOTCHATTNSEL23")
                    .WithValueField(4, 4, name: "FENOTCHCAPCRSE23")
                    .WithValueField(8, 4, name: "FENOTCHCAPFINE23")
                    .WithFlag(12, name: "FENOTCHRATTNEN23")
                    .WithFlag(13, name: "FENOTCHEN23")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "FENOTCHATTNSEL24")
                    .WithValueField(20, 4, name: "FENOTCHCAPCRSE24")
                    .WithValueField(24, 4, name: "FENOTCHCAPFINE24")
                    .WithFlag(28, name: "FENOTCHRATTNEN24")
                    .WithFlag(29, name: "FENOTCHEN24")
                    .WithReservedBits(30, 2)
                },
                {(long)AutomaticGainControlRegisters.ClearChannelAssessmentDebug, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "DEBUGCCARSSI")
                    .WithFlag(8, FieldMode.Read, name: "DEBUGCCAM1")
                    .WithFlag(9, FieldMode.Read, name: "DEBUGCCASIGDET")
                    .WithReservedBits(10, 22)
                },
            };
            return new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private DoubleWordRegisterCollection BuildModulatorAndDemodulatorRegistersCollection()
        {
            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            {
                {(long)ModulatorAndDemodulatorRegisters.IpVersion, new DoubleWordRegister(this, 0x00000003)
                    .WithValueField(0, 32, FieldMode.Read, name: "IPVERSION")
                },
                {(long)ModulatorAndDemodulatorRegisters.Enable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "EN")
                    .WithReservedBits(1, 31)
                },
                {(long)ModulatorAndDemodulatorRegisters.InterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out MODEM_txFrameSentInterrupt, name: "TXFRAMESENTIF")
                    .WithFlag(1, out MODEM_txSyncSentInterrupt, name: "TXSYNCSENTIF")
                    .WithFlag(2, out MODEM_txPreambleSentInterrupt, name: "TXPRESENTIF")
                    .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => MODEM_TxRampingDoneInterrupt, name: "TXRAMPDONEIF")
                    .WithFlag(4, name: "LDTNOARRIF")
                    .WithFlag(5, name: "PHDSADETIF")
                    .WithFlag(6, name: "PHYUNCODEDETIF")
                    .WithFlag(7, name: "PHYCODEDETIF")
                    .WithFlag(8, name: "RXTIMDETIF")
                    .WithFlag(9, out MODEM_rxPreambleDetectedInterrupt, name: "RXPREDETIF")
                    .WithFlag(10, out MODEM_rxFrameWithSyncWord0DetectedInterrupt, name: "RXFRAMEDET0IF")
                    .WithFlag(11, out MODEM_rxFrameWithSyncWord1DetectedInterrupt, name: "RXFRAMEDET1IF")
                    .WithFlag(12, name: "RXTIMLOSTIF")
                    .WithFlag(13, out MODEM_rxPreambleLostInterrupt, name: "RXPRELOSTIF")
                    .WithFlag(14, name: "RXFRAMEDETOFIF")
                    .WithFlag(15, name: "RXTIMNFIF")
                    .WithFlag(16, name: "FRCTIMOUTIF")
                    .WithFlag(17, name: "ETSIF")
                    .WithFlag(18, name: "CFGANTPATTRDIF")
                    .WithFlag(19, name: "RXRESTARTRSSIMAPREIF")
                    .WithFlag(20, name: "RXRESTARTRSSIMASYNCIF")
                    .WithFlag(21, name: "SQDETIF")
                    .WithFlag(22, name: "SQNOTDETIF")
                    .WithFlag(23, name: "ANTDIVRDYIF")
                    .WithFlag(24, name: "SOFTRESETDONEIF")
                    .WithFlag(25, name: "SQPRENOTDETIF")
                    .WithFlag(26, name: "SQFRAMENOTDETIF")
                    .WithFlag(27, name: "SQAFCOUTOFBANDIF")
                    .WithFlag(28, name: "SIDETIF")
                    .WithFlag(29, name: "SIRESETIF")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)ModulatorAndDemodulatorRegisters.InterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out MODEM_txFrameSentInterruptEnable, name: "TXFRAMESENTIEN")
                    .WithFlag(1, out MODEM_txSyncSentInterruptEnable, name: "TXSYNCSENTIEN")
                    .WithFlag(2, out MODEM_txPreambleSentInterruptEnable, name: "TXPRESENTIEN")
                    .WithFlag(3, out MODEM_txRampingDoneInterruptEnable, name: "TXRAMPDONEIEN")
                    .WithFlag(4, name: "LDTNOARRIEN")
                    .WithFlag(5, name: "PHDSADETIEN")
                    .WithFlag(6, name: "PHYUNCODEDETIEN")
                    .WithFlag(7, name: "PHYCODEDETIEN")
                    .WithFlag(8, name: "RXTIMDETIEN")
                    .WithFlag(9, out MODEM_rxPreambleDetectedInterruptEnable, name: "RXPREDETIEN")
                    .WithFlag(10, out MODEM_rxFrameWithSyncWord0DetectedInterruptEnable, name: "RXFRAMEDET0IEN")
                    .WithFlag(11, out MODEM_rxFrameWithSyncWord1DetectedInterruptEnable, name: "RXFRAMEDET1IEN")
                    .WithFlag(12, name: "RXTIMLOSTIEN")
                    .WithFlag(13, out MODEM_rxPreambleLostInterruptEnable, name: "RXPRELOSTIEN")
                    .WithFlag(14, name: "RXFRAMEDETOFIEN")
                    .WithFlag(15, name: "RXTIMNFIEN")
                    .WithFlag(16, name: "FRCTIMOUTIEN")
                    .WithFlag(17, name: "ETSIEN")
                    .WithFlag(18, name: "CFGANTPATTRDIEN")
                    .WithFlag(19, name: "RXRESTARTRSSIMAPREIEN")
                    .WithFlag(20, name: "RXRESTARTRSSIMASYNCIEN")
                    .WithFlag(21, name: "SQDETIEN")
                    .WithFlag(22, name: "SQNOTDETIEN")
                    .WithFlag(23, name: "ANTDIVRDYIEN")
                    .WithFlag(24, name: "SOFTRESETDONEIEN")
                    .WithFlag(25, name: "SQPRENOTDETIEN")
                    .WithFlag(26, name: "SQFRAMENOTDETIEN")
                    .WithFlag(27, name: "SQAFCOUTOFBANDIEN")
                    .WithFlag(28, name: "SIDETIEN")
                    .WithFlag(29, name: "SIRESETIEN")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)ModulatorAndDemodulatorRegisters.SequencerInterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out MODEM_seqTxFrameSentInterrupt, name: "TXFRAMESENTSEQIF")
                    .WithFlag(1, out MODEM_seqTxSyncSentInterrupt, name: "TXSYNCSENTSEQIF")
                    .WithFlag(2, out MODEM_seqTxPreambleSentInterrupt, name: "TXPRESENTSEQIF")
                    .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => MODEM_TxRampingDoneInterrupt, name: "TXRAMPDONESEQIF")
                    .WithFlag(4, name: "LDTNOARRSEQIF")
                    .WithFlag(5, name: "PHDSADETSEQIF")
                    .WithFlag(6, name: "PHYUNCODEDETSEQIF")
                    .WithFlag(7, name: "PHYCODEDETSEQIF")
                    .WithFlag(8, name: "RXTIMDETSEQIF")
                    .WithFlag(9, out MODEM_seqRxPreambleDetectedInterrupt, name: "RXPREDETSEQIF")
                    .WithFlag(10, out MODEM_seqRxFrameWithSyncWord0DetectedInterrupt, name: "RXFRAMEDET0SEQIF")
                    .WithFlag(11, out MODEM_seqRxFrameWithSyncWord1DetectedInterrupt, name: "RXFRAMEDET1SEQIF")
                    .WithFlag(12, name: "RXTIMLOSTSEQIF")
                    .WithFlag(13, out MODEM_seqRxPreambleLostInterrupt, name: "RXPRELOSTSEQIF")
                    .WithFlag(14, name: "RXFRAMEDETOFSEQIF")
                    .WithFlag(15, name: "RXTIMNFSEQIF")
                    .WithFlag(16, name: "FRCTIMOUTSEQIF")
                    .WithFlag(17, name: "ETSSEQIF")
                    .WithFlag(18, name: "CFGANTPATTRDSEQIF")
                    .WithFlag(19, name: "RXRESTARTRSSIMAPRESEQIF")
                    .WithFlag(20, name: "RXRESTARTRSSIMASYNCSEQIF")
                    .WithFlag(21, name: "SQDETSEQIF")
                    .WithFlag(22, name: "SQNOTDETSEQIF")
                    .WithFlag(23, name: "ANTDIVRDYSEQIF")
                    .WithFlag(24, name: "SOFTRESETDONESEQIF")
                    .WithFlag(25, name: "SQPRENOTDETSEQIF")
                    .WithFlag(26, name: "SQFRAMENOTDETSEQIF")
                    .WithFlag(27, name: "SQAFCOUTOFBANDSEQIF")
                    .WithFlag(28, name: "SIDETSEQIF")
                    .WithFlag(29, name: "SIRESETSEQIF")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)ModulatorAndDemodulatorRegisters.SequencerInterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out MODEM_seqTxFrameSentInterruptEnable, name: "TXFRAMESENTSEQIEN")
                    .WithFlag(1, out MODEM_seqTxSyncSentInterruptEnable, name: "TXSYNCSENTSEQIEN")
                    .WithFlag(2, out MODEM_seqTxPreambleSentInterruptEnable, name: "TXPRESENTSEQIEN")
                    .WithFlag(3, out MODEM_seqTxRampingDoneInterruptEnable, name: "TXRAMPDONESEQIEN")
                    .WithFlag(4, name: "LDTNOARRSEQIEN")
                    .WithFlag(5, name: "PHDSADETSEQIEN")
                    .WithFlag(6, name: "PHYUNCODEDETSEQIEN")
                    .WithFlag(7, name: "PHYCODEDETSEQIEN")
                    .WithFlag(8, name: "RXTIMDETSEQIEN")
                    .WithFlag(9, out MODEM_seqRxPreambleDetectedInterruptEnable, name: "RXPREDETSEQIEN")
                    .WithFlag(10, out MODEM_seqRxFrameWithSyncWord0DetectedInterruptEnable, name: "RXFRAMEDET0SEQIEN")
                    .WithFlag(11, out MODEM_seqRxFrameWithSyncWord1DetectedInterruptEnable, name: "RXFRAMEDET1SEQIEN")
                    .WithFlag(12, name: "RXTIMLOSTSEQIEN")
                    .WithFlag(13, out MODEM_seqRxPreambleLostInterruptEnable, name: "RXPRELOSTSEQIEN")
                    .WithFlag(14, name: "RXFRAMEDETOFSEQIEN")
                    .WithFlag(15, name: "RXTIMNFSEQIEN")
                    .WithFlag(16, name: "FRCTIMOUTSEQIEN")
                    .WithFlag(17, name: "ETSSEQIEN")
                    .WithFlag(18, name: "CFGANTPATTRDSEQIEN")
                    .WithFlag(19, name: "RXRESTARTRSSIMAPRESEQIEN")
                    .WithFlag(20, name: "RXRESTARTRSSIMASYNCSEQIEN")
                    .WithFlag(21, name: "SQDETSEQIEN")
                    .WithFlag(22, name: "SQNOTDETSEQIEN")
                    .WithFlag(23, name: "ANTDIVRDYSEQIEN")
                    .WithFlag(24, name: "SOFTRESETDONESEQIEN")
                    .WithFlag(25, name: "SQPRENOTDETSEQIEN")
                    .WithFlag(26, name: "SQFRAMENOTDETSEQIEN")
                    .WithFlag(27, name: "SQAFCOUTOFBANDSEQIEN")
                    .WithFlag(28, name: "SIDETSEQIEN")
                    .WithFlag(29, name: "SIRESETSEQIEN")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)ModulatorAndDemodulatorRegisters.Control0, new DoubleWordRegister(this)
                    .WithFlag(0, name: "FDM0DIFFDIS")
                    .WithValueField(1, 3, name: "MAPFSK")
                    .WithEnumField<DoubleWordRegister, MODEM_SymbolCoding>(4, 2, out MODEM_symbolCoding, name: "CODING")
                    .WithEnumField<DoubleWordRegister, MODEM_ModulationFormat>(6, 3, out MODEM_modulationFormat, name: "MODFORMAT")
                    .WithFlag(9, name: "DUALCORROPTDIS")
                    .WithFlag(10, name: "OOKASYNCPIN")
                    .WithValueField(11, 5, out MODEM_dsssLength, name: "DSSSLEN")
                    .WithValueField(16, 3, out MODEM_dsssShifts, name: "DSSSSHIFTS")
                    .WithEnumField<DoubleWordRegister, MODEM_DsssDoublingMode>(19, 2, out MODEM_dsssDoublingMode, name: "DSSSDOUBLE")
                    .WithFlag(21, name: "DETDIS")
                    .WithValueField(22, 3, name: "DIFFENCMODE")
                    .WithValueField(25, 2, name: "SHAPING")
                    .WithValueField(27, 3, name: "DEMODRAWDATASEL")
                    .WithValueField(30, 2, name: "FRAMEDETDEL")
                },
                {(long)ModulatorAndDemodulatorRegisters.Control1, new DoubleWordRegister(this)
                    .WithValueField(0, 5, out MODEM_syncBits, name: "SYNCBITS")
                    .WithValueField(5, 4, name: "SYNCERRORS")
                    .WithFlag(9, out MODEM_dualSync, name: "DUALSYNC")
                    .WithFlag(10, out MODEM_txSync, name: "TXSYNC")
                    .WithFlag(11, out MODEM_syncData, name: "SYNCDATA")
                    .WithFlag(12, name: "SYNC1INV")
                    .WithReservedBits(13, 1)
                    .WithValueField(14, 2, name: "COMPMODE")
                    .WithValueField(16, 4, name: "RESYNCPER")
                    .WithValueField(20, 2, name: "PHASEDEMOD")
                    .WithValueField(22, 3, name: "FREQOFFESTPER")
                    .WithValueField(25, 7, name: "FREQOFFESTLIM")
                },
                {(long)ModulatorAndDemodulatorRegisters.Control2, new DoubleWordRegister(this, 0x00001000)
                    .WithValueField(0, 8, name: "SQITHRESH")
                    .WithValueField(8, 1, name: "RXFRCDIS")
                    .WithValueField(9, 1, name: "RXPINMODE")
                    .WithValueField(10, 2, name: "TXPINMODE")
                    .WithValueField(12, 3, name: "DATAFILTER")
                    .WithValueField(15, 4, out MODEM_baudrateDivisionFactorA, name: "BRDIVA")
                    .WithValueField(19, 4, out MODEM_baudrateDivisionFactorB, name: "BRDIVB")
                    .WithValueField(23, 2, name: "DEVMULA")
                    .WithValueField(25, 2, name: "DEVMULB")
                    .WithEnumField<DoubleWordRegister, MODEM_RateSelectMode>(27, 2, out MODEM_rateSelectMode, name: "RATESELMODE")
                    .WithValueField(29, 1, name: "DEVWEIGHTDIS")
                    .WithValueField(30, 2, name: "DMASEL")
                },
                 {(long)ModulatorAndDemodulatorRegisters.TxBaudrate, new DoubleWordRegister(this)
                    .WithValueField(0, 16, out MODEM_txBaudrateNumerator, name: "TXBRNUM")
                    .WithValueField(16, 8, out MODEM_txBaudrateDenominator, name: "TXBRDEN")
                    .WithReservedBits(24, 8)
                },
                {(long)ModulatorAndDemodulatorRegisters.Preamble, new DoubleWordRegister(this)
                    .WithValueField(16, 16, out MODEM_txBases, name: "TXBASES")
                    .WithValueField(14, 2, name: "PREWNDERRORS")
                    .WithFlag(13, name: "PREAMBDETEN")
                    .WithFlag(12, name: "SYNCSYMB4FSK")
                    .WithFlag(11, name: "DSSSPRE")
                    .WithValueField(7, 4, name: "PREERRORS")
                    .WithFlag(6, name: "PRESYMB4FSK")
                    .WithValueField(4, 2, out MODEM_baseBits, name: "BASEBITS")
                    .WithValueField(0, 4, name: "BASE")
                },
                {(long)ModulatorAndDemodulatorRegisters.SyncWord0, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out MODEM_syncWord0, name: "SYNC0")
                },
                {(long)ModulatorAndDemodulatorRegisters.SyncWord1, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out MODEM_syncWord1, name: "SYNC1")
                },
                {(long)ModulatorAndDemodulatorRegisters.Command, new DoubleWordRegister(this)
                    .WithFlag(0, name: "PRESTOP")
                    .WithFlag(1, name: "CHPWRACCUCLR")
                    .WithReservedBits(2, 1)
                    .WithFlag(3, name: "AFCTXLOCK")
                    .WithFlag(4, name: "AFCTXCLEAR")
                    .WithFlag(5, name: "AFCRXCLEAR")
                    .WithReservedBits(6, 26)
                },
                {(long)ModulatorAndDemodulatorRegisters.Status, new DoubleWordRegister(this)
                    .WithEnumField<DoubleWordRegister, MODEM_DemodulatorState>(0, 3, out MODEM_demodulatorState, FieldMode.Read, name: "DEMODSTATE")
                    .WithFlag(3, name: "BCRCFEDSADET")
                    .WithFlag(4, out MODEM_frameDetectedId, FieldMode.Read, name: "FRAMEDETID")
                    .WithFlag(5, name: "ANTSEL")
                    .WithFlag(6, name: "TIMSEQINV")
                    .WithFlag(7, name: "TIMLOSTCAUSE")
                    .WithFlag(8, name: "DSADETECTED")
                    .WithFlag(9, name: "DSAFREQESTDONE")
                    .WithFlag(10, name: "VITERBIDEMODTIMDET")
                    .WithFlag(11, name: "VITERBIDEMODFRAMEDET")
                    .WithValueField(12, 3, name: "STAMPSTATE")
                    .WithFlag(15, name: "TRECSDSAADET")
                    .WithValueField(16, 8, name: "CORR")
                    .WithValueField(24, 8, name: "WEAKSYMBOLS")
                },
                {(long)ModulatorAndDemodulatorRegisters.Status2, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "CHPWRACCUMUX")
                    .WithValueField(8, 4, FieldMode.Read, name: "BBSSMUX")
                    // (S=2 => 500k => 1, S=8 => 125k => 0)
                    .WithValueField(12, 2, FieldMode.Read, valueProviderCallback: _ => MODEM_GetCodedSymbolsPerInfoBit() == 2u ? 1u : 0u, name: "LRBLECI")
                    .WithFlag(14, FieldMode.Read, valueProviderCallback: _ => MODEM_symbolCoding.Value != MODEM_SymbolCoding.Linecode, name: "UNCODEDPHY")
                    .WithFlag(15, FieldMode.Read, valueProviderCallback: _ => MODEM_symbolCoding.Value == MODEM_SymbolCoding.Linecode, name: "CODEDPHY")
                    .WithReservedBits(16, 2)
                    .WithValueField(18, 14, FieldMode.Read, name: "RTCOST")
                },
                {(long)ModulatorAndDemodulatorRegisters.BleLongRange, new DoubleWordRegister(this, 0x00FA53E8)
                    .WithValueField(0, 32, name: "LONGRANGE")
                },
                {(long)ModulatorAndDemodulatorRegisters.RampingControl, new DoubleWordRegister(this, 0x00000555)
                  .WithValueField(0, 4, out MODEM_rampRate0, name: "RAMPRATE0")
                  .WithValueField(4, 4, out MODEM_rampRate1, name: "RAMPRATE1")
                  .WithValueField(8, 4, out MODEM_rampRate2, name: "RAMPRATE2")
                  .WithFlag(12, out MODEM_rampDisable, name: "RAMPDIS")
                  .WithReservedBits(13, 3)
                  .WithValueField(16, 8, out MODEM_rampValue, name: "RAMPVAL")
                  .WithReservedBits(24, 8)
                },
                {(long)ModulatorAndDemodulatorRegisters.ViterbiDemodulator, new DoubleWordRegister(this, 0x00206100)
                    .WithFlag(0, out MODEM_viterbiDemodulatorEnable, name: "VTDEMODEN")
                    .WithFlag(1, name: "HARDDECISION")
                    .WithValueField(2, 7, name: "VITERBIKSI1")
                    .WithValueField(9, 7, name: "VITERBIKSI2")
                    .WithValueField(16, 7, name: "VITERBIKSI3")
                    .WithFlag(23, name: "SYNTHAFC")
                    .WithValueField(24, 4, name: "CORRCYCLE")
                    .WithValueField(28, 4, name: "CORRSTPSIZE")
                },
                {(long)ModulatorAndDemodulatorRegisters.Status3, new DoubleWordRegister(this)
                    .WithValueField(0, 11, FieldMode.Read, name: "BBPFOUTABS1")
                    .WithValueField(11, 11, FieldMode.Read, name: "BBPFOUTABS")
                    .WithFlag(22, FieldMode.Read, name: "LRDSALIVE")
                    .WithFlag(23, FieldMode.Read, name: "COHDSALIVE")
                    .WithFlag(24, FieldMode.Read, name: "LRDSADET")
                    .WithFlag(25, FieldMode.Read, name: "COHDSADET")
                    .WithFlag(26, FieldMode.Read, name: "SYNCSECPEAKABTH")
                    .WithFlag(27, FieldMode.Read, name: "SOFTRSTDONE")
                    .WithReservedBits(28, 4)
                },
                {(long)ModulatorAndDemodulatorRegisters.Status4, new DoubleWordRegister(this)
                    .WithValueField(0, 9, FieldMode.Read, name: "ANT0RSSI")
                    .WithReservedBits(9, 7)
                    .WithValueField(16, 9, FieldMode.Read, name: "ANT1RSSI")
                    .WithReservedBits(25, 7)
                },
                {(long)ModulatorAndDemodulatorRegisters.Status5, new DoubleWordRegister(this)
                    .WithValueField(0, 9, FieldMode.Read, name: "RXRESTARTMAFLTDOUT")
                    .WithReservedBits(9, 23)
                },
                {(long)ModulatorAndDemodulatorRegisters.Status6, new DoubleWordRegister(this)
                    .WithValueField(0, 10, FieldMode.Read, name: "ANT0CORR")
                    .WithValueField(10, 10, FieldMode.Read, name: "ANT1CORR")
                    .WithReservedBits(20, 10)
                    .WithFlag(30, FieldMode.Read, name: "ANT0OUT")
                    .WithFlag(31, FieldMode.Read, name: "ANT1OUT")
                },
                {(long)ModulatorAndDemodulatorRegisters.Status7, new DoubleWordRegister(this)
                    .WithValueField(0, 6, FieldMode.Read, name: "FDEVEST")
                    .WithValueField(6, 13, FieldMode.Read, name: "DEMODSOFT")
                    .WithValueField(19, 10, FieldMode.Read, name: "CFEPHDIFF")
                    .WithFlag(29, FieldMode.Read, name: "MINCOSTPASS")
                    .WithReservedBits(30, 1)
                    .WithFlag(31, FieldMode.Read, valueProviderCallback: _ => true, name: "CFEDSADET")
                },
                {(long)ModulatorAndDemodulatorRegisters.TimingDetectionStatus, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "TIMDETCORR")
                    .WithValueField(8, 8, FieldMode.Read, name: "TIMDETFREQOFFEST")
                    .WithValueField(16, 4, FieldMode.Read, name: "TIMDETPREERRORS")
                    .WithReservedBits(20, 4)
                    .WithFlag(24, FieldMode.Read, name: "TIMDETPASS")
                    .WithValueField(25, 4, FieldMode.Read, name: "TIMDETINDEX")
                    .WithReservedBits(29, 3)
                },
                {(long)ModulatorAndDemodulatorRegisters.DemodulatorFSMStatus, new DoubleWordRegister(this)
                    .WithValueField(0, 7, FieldMode.Read, name: "DETSTATE")
                    .WithValueField(7, 3, FieldMode.Read, name: "DSASTATE")
                    .WithValueField(10, 5, FieldMode.Read, name: "LRBLESTATE")
                    .WithValueField(15, 5, FieldMode.Read, name: "NBBLESTATE")
                    .WithValueField(20, 4, FieldMode.Read, name: "ANTDIVSTATE")
                    .WithReservedBits(24, 8)
                },
                {(long)ModulatorAndDemodulatorRegisters.FrequencyOffsetEstimate, new DoubleWordRegister(this)
                    .WithValueField(0, 13, FieldMode.Read, name: "FREQOFFEST")
                    .WithValueField(13, 11, FieldMode.Read, name: "CORRVAL")
                    .WithValueField(24, 8, FieldMode.Read, name: "SOFTVAL")
                },
                {(long)ModulatorAndDemodulatorRegisters.AutomaticFrequencyControlAdjustmentRx, new DoubleWordRegister(this)
                    .WithValueField(0, 19, name: "AFCADJRX")
                    .WithReservedBits(19, 1)
                    .WithValueField(20, 5, name: "AFCSCALEM")
                    .WithReservedBits(25, 3)
                    .WithValueField(28, 4, name: "AFCSCALEE")
                },
                {(long)ModulatorAndDemodulatorRegisters.AutomaticFrequencyControldAjustmentTx, new DoubleWordRegister(this)
                    .WithValueField(0, 19, name: "AFCADJTX")
                    .WithReservedBits(19, 1)
                    .WithValueField(20, 5, name: "AFCSCALEM")
                    .WithReservedBits(25, 3)
                    .WithValueField(28, 4, name: "AFCSCALEE")
                },
                {(long)ModulatorAndDemodulatorRegisters.AnalogMixerControl, new DoubleWordRegister(this)
                    .WithReservedBits(0, 4)
                    .WithFlag(4, name: "DIGIQSWAPEN")
                    .WithReservedBits(5, 27)
                },
                {(long)ModulatorAndDemodulatorRegisters.Control3, new DoubleWordRegister(this, 0x00008000)
                    .WithFlag(0, name: "PRSDINEN")
                    .WithValueField(1, 2, name: "TIMINGBASESGAIN")
                    .WithFlag(3, name: "DEVMULBCW")
                    .WithReservedBits(4, 3)
                    .WithFlag(7, name: "RAMTESTEN")
                    .WithValueField(8, 3, name: "ANTDIVMODE")
                    .WithFlag(11, name: "ANTDIVREPEATDIS")
                    .WithValueField(12, 2, name: "TSAMPMODE")
                    .WithValueField(14, 2, name: "TSAMPDEL")
                    .WithValueField(16, 16, name: "TSAMPLIM")
                },
                {(long)ModulatorAndDemodulatorRegisters.Control4, new DoubleWordRegister(this, 0x03000000)
                    .WithValueField(0, 4, name: "ISICOMP")
                    .WithFlag(4, name: "DEVOFFCOMP")
                    .WithValueField(5, 5, name: "PREDISTGAIN")
                    .WithValueField(10, 3, name: "PREDISTDEB")
                    .WithFlag(13, name: "PREDISTAVG")
                    .WithFlag(14, name: "PREDISTRST")
                    .WithValueField(15, 7, name: "PHASECLICKFILT")
                    .WithFlag(22, name: "SOFTDSSSMODE")
                    .WithValueField(23, 3, name: "ADCSATLEVEL")
                    .WithValueField(26, 2, name: "ADCSATDENS")
                    .WithFlag(28, name: "OFFSETPHASEMASKING")
                    .WithFlag(29, name: "OFFSETPHASESCALING")
                    .WithReservedBits(30, 1)
                    .WithFlag(31, name: "CLKUNDIVREQ")
                },
                {(long)ModulatorAndDemodulatorRegisters.Control5, new DoubleWordRegister(this)
                    .WithReservedBits(0, 1)
                    .WithFlag(1, name: "BRCALEN")
                    .WithValueField(2, 2, name: "BRCALMODE")
                    .WithValueField(4, 2, name: "BRCALAVG")
                    .WithValueField(6, 3, name: "DETDEL")
                    .WithFlag(9, name: "TDEDGE")
                    .WithFlag(10, name: "TREDGE")
                    .WithFlag(11, name: "DSSSCTD")
                    .WithValueField(12, 4, name: "BBSS")
                    .WithValueField(16, 4, name: "POEPER")
                    .WithValueField(20, 3, name: "DEMODRAWDATASEL2")
                    .WithReservedBits(23, 1)
                    .WithValueField(24, 3, name: "FOEPREAVG")
                    .WithFlag(27, name: "LINCORR")
                    .WithReservedBits(28, 1)
                    .WithFlag(29, name: "RESYNCBAUDTRANS")
                    .WithFlag(30, name: "RESYNCLIMIT")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.Control6, new DoubleWordRegister(this)
                    .WithValueField(0, 7, name: "TDREW")
                    .WithValueField(7, 4, name: "PREBASES")
                    .WithFlag(11, name: "PSTIMABORT0")
                    .WithFlag(12, name: "PSTIMABORT1")
                    .WithFlag(13, name: "PSTIMABORT2")
                    .WithFlag(14, name: "PSTIMABORT3")
                    .WithValueField(15, 2, name: "ARW")
                    .WithValueField(17, 3, name: "TIMTHRESHGAIN")
                    .WithFlag(20, name: "CPLXCORREN")
                    .WithFlag(21, name: "DSSS3SYMBOLSYNCEN")
                    .WithFlag(22, name: "TXDBPSKINV")
                    .WithFlag(23, name: "TXDBPSKRAMPEN")
                    .WithFlag(24, name: "IFADCDIGGAINCLKSEL")
                    .WithValueField(25, 2, name: "CODINGB")
                    .WithFlag(27, name: "IFADCDIGGAIN")
                    .WithFlag(28, name: "RXRESTARTUPONRSSI")
                    .WithFlag(29, name: "RXRESTARTUPONSHORTRSSI")
                    .WithFlag(30, name: "RXBRCALCDIS")
                    .WithFlag(31, name: "DEMODRESTARTALL")
                },
                {(long)ModulatorAndDemodulatorRegisters.RxBaudrate, new DoubleWordRegister(this)
                    .WithValueField(0, 5, name: "RXBRNUM")
                    .WithValueField(5, 5, name: "RXBRDEN")
                    .WithValueField(10, 3, name: "RXBRINT")
                    .WithReservedBits(13, 19)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilter, new DoubleWordRegister(this)
                    .WithValueField(0, 3, name: "DEC0")
                    .WithValueField(3, 14, name: "DEC1")
                    .WithValueField(17, 6, name: "DEC2")
                    .WithValueField(23, 3, name: "CFOSR")
                    .WithValueField(26, 2, name: "DEC1GAIN")
                    .WithReservedBits(28, 2)
                    .WithFlag(30, name: "ADCBITORDERI")
                    .WithFlag(31, name: "ADCBITORDERQ")
                },
                {(long)ModulatorAndDemodulatorRegisters.Timing, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "TIMTHRESH")
                    .WithValueField(8, 4, name: "TIMINGBASES")
                    .WithValueField(12, 4, name: "ADDTIMSEQ")
                    .WithFlag(16, name: "TIMSEQINVEN")
                    .WithFlag(17, name: "TIMSEQSYNC")
                    .WithValueField(18, 3, name: "FDM0THRESH")
                    .WithValueField(21, 4, name: "OFFSUBNUM")
                    .WithValueField(25, 4, name: "OFFSUBDEN")
                    .WithFlag(29, name: "TSAGCDEL")
                    .WithValueField(30, 2, name: "FASTRESYNC")
                },
                {(long)ModulatorAndDemodulatorRegisters.DirectSequenceSpreadSpectrumSymbol0, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "DSSS0")
                },
                {(long)ModulatorAndDemodulatorRegisters.ModulationIndex, new DoubleWordRegister(this)
                    .WithValueField(0, 5, name: "MODINDEXM")
                    .WithValueField(5, 5, name: "MODINDEXE")
                    .WithReservedBits(10, 6)
                    .WithValueField(16, 3, name: "FREQGAINE")
                    .WithValueField(19, 3, name: "FREQGAINM")
                    .WithReservedBits(22, 10)
                },
                {(long)ModulatorAndDemodulatorRegisters.AutomaticFrequencyControl, new DoubleWordRegister(this)
                    .WithReservedBits(0, 10)
                    .WithValueField(10, 3, name: "AFCRXMODE")
                    .WithValueField(13, 2, name: "AFCTXMODE")
                    .WithFlag(15, name: "AFCRXCLR")
                    .WithValueField(16, 5, name: "AFCDEL")
                    .WithValueField(21, 3, name: "AFCAVGPER")
                    .WithFlag(24, name: "AFCLIMRESET")
                    .WithFlag(25, name: "AFCONESHOT")
                    .WithFlag(26, name: "AFCENINTCOMP")
                    .WithFlag(27, name: "AFCDSAFREQOFFEST")
                    .WithFlag(28, name: "AFCDELDET")
                    .WithValueField(29, 2, name: "AFCGEAR")
                    .WithFlag(31, name: "DISAFCCTE")
                },
                {(long)ModulatorAndDemodulatorRegisters.AutomaticFrequencyControlAdjustmentLimit, new DoubleWordRegister(this)
                    .WithValueField(0, 18, name: "AFCADJLIM")
                    .WithReservedBits(18, 14)
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients0, new DoubleWordRegister(this, 0x22130A04)
                    .WithValueField(0, 8, name: "COEFF0")
                    .WithValueField(8, 8, name: "COEFF1")
                    .WithValueField(16, 8, name: "COEFF2")
                    .WithValueField(24, 8, name: "COEFF3")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients1, new DoubleWordRegister(this, 0x4F4A4132)
                    .WithValueField(0, 8, name: "COEFF4")
                    .WithValueField(8, 8, name: "COEFF5")
                    .WithValueField(16, 8, name: "COEFF6")
                    .WithValueField(24, 8, name: "COEFF7")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients2, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF8")
                    .WithValueField(8, 8, name: "COEFF9")
                    .WithValueField(16, 8, name: "COEFF10")
                    .WithValueField(24, 8, name: "COEFF11")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients3, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF12")
                    .WithValueField(8, 8, name: "COEFF13")
                    .WithValueField(16, 8, name: "COEFF14")
                    .WithValueField(24, 8, name: "COEFF15")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients4, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF16")
                    .WithValueField(8, 8, name: "COEFF17")
                    .WithValueField(16, 8, name: "COEFF18")
                    .WithValueField(24, 8, name: "COEFF19")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients5, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF20")
                    .WithValueField(8, 8, name: "COEFF21")
                    .WithValueField(16, 8, name: "COEFF22")
                    .WithValueField(24, 8, name: "COEFF23")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients6, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF24")
                    .WithValueField(8, 8, name: "COEFF25")
                    .WithValueField(16, 8, name: "COEFF26")
                    .WithValueField(24, 8, name: "COEFF27")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients7, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF28")
                    .WithValueField(8, 8, name: "COEFF29")
                    .WithValueField(16, 8, name: "COEFF30")
                    .WithValueField(24, 8, name: "COEFF31")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients8, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF32")
                    .WithValueField(8, 8, name: "COEFF33")
                    .WithValueField(16, 8, name: "COEFF34")
                    .WithValueField(24, 8, name: "COEFF35")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients9, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF36")
                    .WithValueField(8, 8, name: "COEFF37")
                    .WithValueField(16, 8, name: "COEFF38")
                    .WithValueField(24, 8, name: "COEFF39")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients10, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF40")
                    .WithValueField(8, 8, name: "COEFF41")
                    .WithValueField(16, 8, name: "COEFF42")
                    .WithValueField(24, 8, name: "COEFF43")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients11, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF44")
                    .WithValueField(8, 8, name: "COEFF45")
                    .WithValueField(16, 8, name: "COEFF46")
                    .WithValueField(24, 8, name: "COEFF47")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients12, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF48")
                    .WithValueField(8, 8, name: "COEFF49")
                    .WithValueField(16, 8, name: "COEFF50")
                    .WithValueField(24, 8, name: "COEFF51")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients13, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF52")
                    .WithValueField(8, 8, name: "COEFF53")
                    .WithValueField(16, 8, name: "COEFF54")
                    .WithValueField(24, 8, name: "COEFF55")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients14, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF56")
                    .WithValueField(8, 8, name: "COEFF57")
                    .WithValueField(16, 8, name: "COEFF58")
                    .WithValueField(24, 8, name: "COEFF59")
                },
                {(long)ModulatorAndDemodulatorRegisters.ShapingCoefficients15, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "COEFF60")
                    .WithValueField(8, 8, name: "COEFF61")
                    .WithValueField(16, 8, name: "COEFF62")
                    .WithValueField(24, 8, name: "COEFF63")
                },
                {(long)ModulatorAndDemodulatorRegisters.RampingLevels, new DoubleWordRegister(this, 0x009F9F9F)
                    .WithValueField(0, 8, name: "RAMPLEV0")
                    .WithValueField(8, 8, name: "RAMPLEV1")
                    .WithValueField(16, 8, name: "RAMPLEV2")
                    .WithReservedBits(24, 8)
                },
                {(long)ModulatorAndDemodulatorRegisters.AnalogRampingControl, new DoubleWordRegister(this, 0x00000A00)
                    .WithReservedBits(0, 1)
                    .WithFlag(1, name: "RAMPOVREN")
                    .WithFlag(2, name: "RAMPOVRUPD")
                    .WithReservedBits(3, 6)
                    .WithValueField(9, 2, name: "VMIDCTRL")
                    .WithValueField(11, 2, name: "MUTEDLY")
                    .WithReservedBits(13, 19)
                },
                {(long)ModulatorAndDemodulatorRegisters.DirectCurrentOffsetCompensationFilterSettings, new DoubleWordRegister(this, 0x00000030)
                    .WithFlag(0, name: "DCESTIEN")
                    .WithFlag(1, name: "DCCOMPEN")
                    .WithFlag(2, name: "DCRSTEN")
                    .WithFlag(3, name: "DCCOMPFREEZE")
                    .WithValueField(4, 3, name: "DCCOMPGEAR")
                    .WithValueField(7, 2, name: "DCLIMIT")
                    .WithFlag(9, name: "DCGAINGEAREN")
                    .WithValueField(10, 3, name: "DCGAINGEAR")
                    .WithValueField(13, 8, name: "DCGAINGEARSMPS")
                    .WithReservedBits(21, 11)
                },
                {(long)ModulatorAndDemodulatorRegisters.DirectCurrentOffsetCompensationFilterInitialization, new DoubleWordRegister(this)
                    .WithValueField(0, 15, name: "DCCOMPINITVALI")
                    .WithValueField(15, 15, name: "DCCOMPINITVALQ")
                    .WithFlag(30, name: "DCCOMPINIT")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.DirectCurrentOffsetEstimatedValue, new DoubleWordRegister(this)
                    .WithValueField(0, 15, FieldMode.Read, name: "DCCOMPESTIVALI")
                    .WithValueField(15, 15, FieldMode.Read, name: "DCCOMPESTIVALQ")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.SampleRateConverterRatioValuesAndChannelFilter, new DoubleWordRegister(this)
                    .WithReservedBits(0, 12)
                    .WithValueField(12, 15, name: "SRCRATIO2")
                    .WithFlag(27, name: "SRCENABLE2")
                    .WithReservedBits(28, 3)
                    .WithFlag(31, name: "INTOSR")
                },
                {(long)ModulatorAndDemodulatorRegisters.InternalAutomaticFrequencyControl, new DoubleWordRegister(this)
                    .WithValueField(0, 3, name: "FOEPREAVG0")
                    .WithValueField(3, 3, name: "FOEPREAVG1")
                    .WithValueField(6, 3, name: "FOEPREAVG2")
                    .WithValueField(9, 3, name: "FOEPREAVG3")
                    .WithValueField(12, 3, name: "FOEPREAVG4")
                    .WithValueField(15, 3, name: "FOEPREAVG5")
                    .WithValueField(18, 3, name: "FOEPREAVG6")
                    .WithValueField(21, 3, name: "FOEPREAVG7")
                    .WithReservedBits(24, 8)
                },
                {(long)ModulatorAndDemodulatorRegisters.DetectionOfSignalArrivalThreshold0, new DoubleWordRegister(this, 0x07830464)
                    .WithValueField(0, 8, name: "SPIKETHD")
                    .WithValueField(8, 6, name: "UNMODTHD")
                    .WithValueField(14, 6, name: "FDEVMINTHD")
                    .WithValueField(20, 12, name: "FDEVMAXTHD")
                },
                {(long)ModulatorAndDemodulatorRegisters.DetectionOfSignalArrivalThreshold1, new DoubleWordRegister(this, 0x3AC81388)
                    .WithValueField(0, 16, name: "POWABSTHD")
                    .WithValueField(16, 2, name: "POWRELTHD")
                    .WithValueField(18, 3, name: "DSARSTCNT")
                    .WithValueField(21, 4, name: "RSSIJMPTHD")
                    .WithValueField(25, 2, name: "FREQLATDLY")
                    .WithFlag(27, name: "PWRFLTBYP")
                    .WithFlag(28, name: "AMPFLTBYP")
                    .WithFlag(29, name: "PWRDETDIS")
                    .WithFlag(30, name: "FREQSCALE")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.DetectionOfSignalArrivalThreshold2, new DoubleWordRegister(this, 0x0C660664)
                    .WithValueField(0, 8, name: "POWABSTHDLOG")
                    .WithReservedBits(8, 1)
                    .WithFlag(9, name: "JUMPDETEN")
                    .WithValueField(10, 6, name: "FDADJTHD")
                    .WithValueField(16, 4, name: "PMDETPASSTHD")
                    .WithValueField(20, 5, name: "FREQESTTHD")
                    .WithValueField(25, 5, name: "INTERFERDET")
                    .WithFlag(30, name: "PMDETFORCE")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.DetectionOfSignalArrivalThreshold3, new DoubleWordRegister(this, 0x07830464)
                    .WithValueField(0, 8, name: "SPIKETHDLO")
                    .WithValueField(8, 6, name: "UNMODTHDLO")
                    .WithValueField(14, 6, name: "FDEVMINTHDLO")
                    .WithValueField(20, 12, name: "FDEVMAXTHDLO")
                },
                {(long)ModulatorAndDemodulatorRegisters.DetectionOfSignalArrivalThreshold4, new DoubleWordRegister(this, 0x00821388)
                    .WithValueField(0, 16, name: "POWABSTHDLO")
                    .WithValueField(16, 5, name: "ARRTOLERTHD0LO")
                    .WithValueField(21, 5, name: "ARRTOLERTHD1LO")
                    .WithFlag(26, name: "SWTHD")
                    .WithReservedBits(27, 5)
                },
                {(long)ModulatorAndDemodulatorRegisters.DetectionOfSignalArrivalMode, new DoubleWordRegister(this, 0x000A2090)
                    .WithValueField(0, 2, name: "DSAMODE")
                    .WithValueField(2, 4, name: "ARRTHD")
                    .WithValueField(6, 5, name: "ARRTOLERTHD0")
                    .WithValueField(11, 5, name: "ARRTOLERTHD1")
                    .WithFlag(16, name: "SCHPRD")
                    .WithFlag(17, name: "FREQAVGSYM")
                    .WithFlag(18, name: "TRANRSTDSA")
                    .WithFlag(19, name: "DSARSTON")
                    .WithReservedBits(20, 1)
                    .WithValueField(21, 2, name: "GAINREDUCDLY")
                    .WithValueField(23, 3, name: "LOWDUTY")
                    .WithFlag(26, name: "RESTORE")
                    .WithFlag(27, name: "AGCBAUDEN")
                    .WithValueField(28, 4, name: "AMPJUPTHD")
                },
                {(long)ModulatorAndDemodulatorRegisters.DigitalMixerControl, new DoubleWordRegister(this)
                    .WithValueField(0, 20, name: "DIGMIXFREQ")
                    .WithFlag(20, name: "DIGMIXMODE")
                    .WithFlag(21, name: "MIXERCONJ")
                    .WithFlag(22, name: "DIGMIXFB")
                    .WithReservedBits(23, 9)
                },
                {(long)ModulatorAndDemodulatorRegisters.ViterbiDemodulatorCorrelationConfiguration0, new DoubleWordRegister(this, 0x123556B7)
                    .WithValueField(0, 32, name: "EXPECTPATT")
                },
                {(long)ModulatorAndDemodulatorRegisters.ViterbiDemodulatorCorrelationConfiguration1, new DoubleWordRegister(this, 0x5020C000)
                    .WithValueField(0, 7, name: "VITERBIKSI3WB")
                    .WithFlag(7, name: "KSI3SWENABLE")
                    .WithValueField(8, 9, name: "VTFRQLIM")
                    .WithReservedBits(17, 1)
                    .WithValueField(18, 9, name: "EXPSYNCLEN")
                    .WithReservedBits(27, 1)
                    .WithValueField(28, 4, name: "EXPECTHT")
                },
                {(long)ModulatorAndDemodulatorRegisters.ViterbiDemodulatorTrackingLoop, new DoubleWordRegister(this, 0x0D803B88)
                    .WithValueField(0, 2, name: "FREQTRACKMODE")
                    .WithValueField(2, 4, name: "TIMTRACKTHD")
                    .WithValueField(6, 8, name: "TIMEACQUTHD")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 2, name: "TIMGEAR")
                    .WithValueField(18, 4, name: "FREQBIAS")
                    .WithValueField(22, 8, name: "HIPWRTHD")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.ViterbiBleTimingStampControl, new DoubleWordRegister(this)
                    .WithValueField(0, 2, name: "VTBLETIMINGSEL")
                    .WithFlag(2, name: "VTBLETIMINGCLKSEL")
                    .WithReservedBits(3, 1)
                    .WithValueField(4, 8, name: "TIMINGDELAY")
                    .WithValueField(12, 4, name: "FLENOFF")
                    .WithReservedBits(16, 15)
                    .WithFlag(31, name: "DISDEMODOF")
                },
                {(long)ModulatorAndDemodulatorRegisters.BaudrateEstimate, new DoubleWordRegister(this)
                    .WithValueField(0, 6, FieldMode.Read, name: "BRESTINT")
                    .WithValueField(6, 5, FieldMode.Read, name: "BRESTNUM")
                    .WithReservedBits(11, 21)
                },
                {(long)ModulatorAndDemodulatorRegisters.AutomaticClockGating, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "AUTOCGEN")
                    .WithReservedBits(16, 16)
                },
                {(long)ModulatorAndDemodulatorRegisters.AutomaticClockGatingClockStop, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "FORCEOFF")
                    .WithReservedBits(16, 16)
                },
                {(long)ModulatorAndDemodulatorRegisters.PhaseOffsetEstimate, new DoubleWordRegister(this)
                    .WithValueField(0, 10, FieldMode.Read, name: "POEI")
                    .WithReservedBits(10, 6)
                    .WithValueField(16, 10, FieldMode.Read, name: "POEQ")
                    .WithReservedBits(26, 6)
                },
                {(long)ModulatorAndDemodulatorRegisters.DirectModeControl, new DoubleWordRegister(this, 0x0000010C)
                    .WithFlag(0, name: "DMENABLE")
                    .WithFlag(1, name: "SYNCASYNC")
                    .WithValueField(2, 2, name: "SYNCPREAM")
                    .WithReservedBits(4, 4)
                    .WithValueField(8, 5, name: "CLKWIDTH")
                    .WithReservedBits(13, 19)
                },
                {(long)ModulatorAndDemodulatorRegisters.BleLongRangeSet1, new DoubleWordRegister(this, 0x40000000)
                    .WithValueField(0, 4, name: "LRSS")
                    .WithValueField(4, 11, name: "LRTIMEOUTTHD")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 2, name: "CHPWRACCUDEL")
                    .WithValueField(18, 3, name: "HYSVAL")
                    .WithValueField(21, 3, name: "AVGWIN")
                    .WithValueField(24, 4, name: "LRSPIKETHADD")
                    .WithFlag(28, name: "LOGICBASEDPUGATE")
                    .WithFlag(29, name: "LOGICBASEDLRDEMODGATE")
                    .WithValueField(30, 2, name: "PREFILTLEN")
                },
                {(long)ModulatorAndDemodulatorRegisters.BleLongRangeSet2, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "LRCHPWRTH1")
                    .WithValueField(8, 8, name: "LRCHPWRTH2")
                    .WithValueField(16, 8, name: "LRCHPWRTH3")
                    .WithValueField(24, 8, name: "LRCHPWRTH4")
                },
                {(long)ModulatorAndDemodulatorRegisters.BleLongRangeSet3, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "LRCHPWRTH5")
                    .WithValueField(8, 8, name: "LRCHPWRTH6")
                    .WithValueField(16, 8, name: "LRCHPWRTH7")
                    .WithValueField(24, 8, name: "LRCHPWRTH8")
                },
                {(long)ModulatorAndDemodulatorRegisters.BleLongRangeSet4, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "LRCHPWRTH9")
                    .WithValueField(8, 8, name: "LRCHPWRTH10")
                    .WithValueField(16, 4, name: "LRCHPWRSH1")
                    .WithValueField(20, 4, name: "LRCHPWRSH2")
                    .WithValueField(24, 4, name: "LRCHPWRSH3")
                    .WithValueField(28, 4, name: "LRCHPWRSH4")
                },
                {(long)ModulatorAndDemodulatorRegisters.BleLongRangeSet5, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "LRCHPWRSH5")
                    .WithValueField(4, 4, name: "LRCHPWRSH6")
                    .WithValueField(8, 4, name: "LRCHPWRSH7")
                    .WithValueField(12, 4, name: "LRCHPWRSH8")
                    .WithValueField(16, 4, name: "LRCHPWRSH9")
                    .WithValueField(20, 4, name: "LRCHPWRSH10")
                    .WithValueField(24, 4, name: "LRCHPWRSH11")
                    .WithReservedBits(28, 4)
                },
                {(long)ModulatorAndDemodulatorRegisters.BleLongRangeSet6, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "LRCHPWRSPIKETH")
                    .WithValueField(8, 11, name: "LRSPIKETHD")
                    .WithReservedBits(19, 1)
                    .WithValueField(20, 8, name: "LRCHPWRTH11")
                    .WithValueField(28, 4, name: "LRCHPWRSH12")
                },
                {(long)ModulatorAndDemodulatorRegisters.BleLongRangeFrameControllerInterface, new DoubleWordRegister(this, 0x00000101)
                    .WithValueField(0, 2, name: "CI500")
                    .WithValueField(2, 6, name: "FRCACKTIMETHD")
                    .WithFlag(8, name: "LRCORRMODE")
                    .WithReservedBits(9, 23)
                },
                {(long)ModulatorAndDemodulatorRegisters.CoherentDemodulatorSignals0, new DoubleWordRegister(this)
                    .WithFlag(0, name: "COHDYNAMICBBSSEN")
                    .WithFlag(1, name: "COHDYNAMICSYNCTHRESH")
                    .WithFlag(2, name: "COHDYNAMICPRETHRESH")
                    .WithFlag(3, name: "COHCHPWRLOCK")
                    .WithFlag(4, name: "COHCHPWRRESTART")
                    .WithValueField(5, 3, name: "COHDYNAMICPRETHRESHSEL")
                    .WithValueField(8, 8, name: "COHCHPWRTH0")
                    .WithValueField(16, 8, name: "COHCHPWRTH1")
                    .WithValueField(24, 8, name: "COHCHPWRTH2")
                },
                {(long)ModulatorAndDemodulatorRegisters.CoherentDemodulatorSignals1, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "SYNCTHRESH0")
                    .WithValueField(8, 8, name: "SYNCTHRESH1")
                    .WithValueField(16, 8, name: "SYNCTHRESH2")
                    .WithValueField(24, 8, name: "SYNCTHRESH3")
                },
                {(long)ModulatorAndDemodulatorRegisters.CoherentDemodulatorSignals2, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "SYNCTHRESHDELTA0")
                    .WithValueField(4, 4, name: "SYNCTHRESHDELTA1")
                    .WithValueField(8, 4, name: "SYNCTHRESHDELTA2")
                    .WithValueField(12, 4, name: "SYNCTHRESHDELTA3")
                    .WithValueField(16, 8, name: "DSAPEAKCHPWRTH")
                    .WithValueField(24, 8, name: "FIXEDCDTHFORIIR")
                },
                {(long)ModulatorAndDemodulatorRegisters.CoherentDemodulatorSignals3, new DoubleWordRegister(this)
                    .WithFlag(0, name: "COHDSAEN")
                    .WithValueField(1, 10, name: "COHDSAADDWNDSIZE")
                    .WithValueField(11, 3, name: "CDSS")
                    .WithFlag(14, name: "DSAPEAKCHKEN")
                    .WithValueField(15, 3, name: "DSAPEAKINDLEN")
                    .WithFlag(18, name: "DSAPEAKCHPWREN")
                    .WithFlag(19, name: "LOGICBASEDCOHDEMODGATE")
                    .WithValueField(20, 2, name: "DYNIIRCOEFOPTION")
                    .WithFlag(22, name: "ONEPEAKQUALEN")
                    .WithValueField(23, 5, name: "PEAKCHKTIMOUT")
                    .WithFlag(28, name: "COHDSADETDIS")
                    .WithFlag(29, name: "COHDSACMPLX")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.SyncWordProperties, new DoubleWordRegister(this)
                    .WithReservedBits(0, 8)
                    .WithFlag(8, name: "STATICSYNCTHRESHEN")
                    .WithValueField(9, 8, name: "STATICSYNCTHRESH")
                    .WithReservedBits(17, 15)
                },
                {(long)ModulatorAndDemodulatorRegisters.DigitalGainControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "DIGIGAINEN")
                    .WithValueField(1, 5, name: "DIGIGAINSEL")
                    .WithFlag(6, name: "DIGIGAINDOUBLE")
                    .WithFlag(7, name: "DIGIGAINHALF")
                    .WithFlag(8, name: "DEC0GAIN")
                    .WithReservedBits(9, 23)
                },
                {(long)ModulatorAndDemodulatorRegisters.PeripheralReflexSystemControl, new DoubleWordRegister(this)
                    .WithValueField(0, 2, name: "POSTPONESEL")
                    .WithValueField(2, 2, name: "ADVANCESEL")
                    .WithValueField(4, 2, name: "NEWWNDSEL")
                    .WithValueField(6, 2, name: "WEAKSEL")
                    .WithValueField(8, 2, name: "SYNCSENTSEL")
                    .WithValueField(10, 2, name: "PRESENTSEL")
                    .WithValueField(12, 2, name: "LOWCORRSEL")
                    .WithValueField(14, 2, name: "ANT0SEL")
                    .WithValueField(16, 2, name: "ANT1SEL")
                    .WithValueField(18, 2, name: "IFADCCLKSEL")
                    .WithReservedBits(20, 12)
                },
                {(long)ModulatorAndDemodulatorRegisters.PowerAmplifierDebug, new DoubleWordRegister(this)
                    .WithReservedBits(0, 9)
                    .WithFlag(9, name: "MANPACLKAMPCTRL")
                    .WithFlag(10, name: "ENMANPACLKAMPCTRL")
                    .WithFlag(11, name: "ENMANPAPOWER")
                    .WithFlag(12, name: "ENMANPASELSLICE")
                    .WithReservedBits(13, 19)
                },
                {(long)ModulatorAndDemodulatorRegisters.RealTimeCostFunctionEngineControl, new DoubleWordRegister(this, 0x001F81F4)
                    .WithValueField(0, 10, name: "MINCOSTTHD")
                    .WithValueField(10, 4, name: "RTSCHWIN")
                    .WithFlag(14, name: "RTSCHMODE")
                    .WithValueField(15, 3, name: "TRACKINGWIN")
                    .WithValueField(18, 3, name: "SYNCACQWIN")
                    .WithFlag(21, name: "EXTENSCHBYP")
                    .WithReservedBits(22, 7)
                    .WithFlag(29, name: "SINEWEN")
                    .WithFlag(30, name: "VTAFCFRAME")
                    .WithFlag(31, name: "RTCFEEN")
                },
                {(long)ModulatorAndDemodulatorRegisters.EarlyTimeStampControl, new DoubleWordRegister(this)
                    .WithValueField(0, 10, name: "ETSLOC")
                    .WithFlag(10, name: "CAPSIGONPRS")
                    .WithReservedBits(11, 1)
                    .WithValueField(12, 18, name: "CAPTRIG")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.EarlyTimeStampTiming, new DoubleWordRegister(this)
                    .WithValueField(0, 17, name: "ETSTIMVAL")
                    .WithFlag(17, name: "ETSCOUNTEREN")
                    .WithReservedBits(18, 14)
                },
                {(long)ModulatorAndDemodulatorRegisters.AntennaSwitchControl, new DoubleWordRegister(this, 0x003C0000)
                    .WithValueField(0, 6, name: "ANTDFLTSEL")
                    .WithValueField(6, 6, name: "ANTCOUNT")
                    .WithValueField(12, 2, name: "ANTSWTYPE")
                    .WithFlag(14, name: "ANTSWRST")
                    .WithFlag(15, name: "CFGANTPATTEN")
                    .WithFlag(16, name: "ANTSWENABLE")
                    .WithValueField(17, 8, name: "EXTDSTOPPULSECNT")
                    .WithReservedBits(25, 7)
                },
                {(long)ModulatorAndDemodulatorRegisters.AntennaSwitchControl1, new DoubleWordRegister(this, 0x0006AAAA)
                    .WithValueField(0, 24, name: "TIMEPERIOD")
                    .WithReservedBits(24, 8)
                },
                {(long)ModulatorAndDemodulatorRegisters.AntennaSwitchStart, new DoubleWordRegister(this)
                    .WithValueField(0, 18, name: "ANTSWSTARTTIM")
                    .WithReservedBits(18, 14)
                },
                {(long)ModulatorAndDemodulatorRegisters.AntennaSwitchEnd, new DoubleWordRegister(this)
                    .WithValueField(0, 18, name: "ANTSWENDTIM")
                    .WithReservedBits(18, 14)
                },
                {(long)ModulatorAndDemodulatorRegisters.TrecsPreamblePattern, new DoubleWordRegister(this, 0x55555555)
                    .WithValueField(0, 32, name: "PMEXPECTPATT")
                },
                {(long)ModulatorAndDemodulatorRegisters.TrecsPreambleDetectionControl, new DoubleWordRegister(this, 0x00000017)
                    .WithValueField(0, 3, name: "PMACQUINGWIN")
                    .WithValueField(3, 3, name: "PMCOSTVALTHD")
                    .WithValueField(6, 2, name: "PMTIMEOUTSEL")
                    .WithValueField(8, 2, name: "PHSCALE")
                    .WithReservedBits(10, 4)
                    .WithValueField(14, 10, name: "PMMINCOSTTHD")
                    .WithReservedBits(24, 1)
                    .WithValueField(25, 5, name: "COSTHYST")
                    .WithReservedBits(30, 1)
                    .WithFlag(31, name: "PREAMSCH")
                },
                {(long)ModulatorAndDemodulatorRegisters.TrecsConfiguration, new DoubleWordRegister(this, 0x00020004)
                    .WithValueField(0, 3, name: "TRECSOSR")
                    .WithValueField(3, 10, name: "DTIMLOSSTHD")
                    .WithReservedBits(13, 1)
                    .WithFlag(14, name: "DTIMLOSSEN")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 9, name: "PMOFFSET")
                    .WithReservedBits(25, 7)
                },
                {(long)ModulatorAndDemodulatorRegisters.ConfigureAntennaPattern, new DoubleWordRegister(this)
                    .WithValueField(0, 30, name: "CFGANTPATTVAL")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.ConcurrentMode, new DoubleWordRegister(this)
                    .WithReservedBits(0, 31)
                    .WithFlag(31, name: "CONCURRENT")
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet0Group0, new DoubleWordRegister(this)
                    .WithValueField(0, 10, name: "SET0COEFF0")
                    .WithValueField(10, 10, name: "SET0COEFF1")
                    .WithValueField(20, 10, name: "SET0COEFF2")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet0Group1, new DoubleWordRegister(this)
                    .WithValueField(0, 11, name: "SET0COEFF3")
                    .WithValueField(11, 11, name: "SET0COEFF4")
                    .WithReservedBits(22, 10)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet0Group2, new DoubleWordRegister(this)
                    .WithValueField(0, 11, name: "SET0COEFF5")
                    .WithValueField(11, 12, name: "SET0COEFF6")
                    .WithReservedBits(23, 9)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet0Group3, new DoubleWordRegister(this)
                    .WithValueField(0, 12, name: "SET0COEFF7")
                    .WithValueField(12, 12, name: "SET0COEFF8")
                    .WithReservedBits(24, 8)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet0Group4, new DoubleWordRegister(this)
                    .WithValueField(0, 14, name: "SET0COEFF9")
                    .WithValueField(14, 14, name: "SET0COEFF10")
                    .WithReservedBits(28, 4)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet0Group5, new DoubleWordRegister(this)
                    .WithValueField(0, 14, name: "SET0COEFF11")
                    .WithValueField(14, 16, name: "SET0COEFF12")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet0Group6, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "SET0COEFF13")
                    .WithValueField(16, 16, name: "SET0COEFF14")
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet1Group0, new DoubleWordRegister(this)
                    .WithValueField(0, 10, name: "SET1COEFF0")
                    .WithValueField(10, 10, name: "SET1COEFF1")
                    .WithValueField(20, 10, name: "SET1COEFF2")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet1Group1, new DoubleWordRegister(this)
                    .WithValueField(0, 11, name: "SET1COEFF3")
                    .WithValueField(11, 11, name: "SET1COEFF4")
                    .WithReservedBits(22, 10)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet1Group2, new DoubleWordRegister(this)
                    .WithValueField(0, 11, name: "SET1COEFF5")
                    .WithValueField(11, 12, name: "SET1COEFF6")
                    .WithReservedBits(23, 9)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet1Group3, new DoubleWordRegister(this)
                    .WithValueField(0, 12, name: "SET1COEFF7")
                    .WithValueField(12, 12, name: "SET1COEFF8")
                    .WithReservedBits(24, 8)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet1Group4, new DoubleWordRegister(this)
                    .WithValueField(0, 14, name: "SET1COEFF9")
                    .WithValueField(14, 14, name: "SET1COEFF10")
                    .WithReservedBits(28, 4)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet1Group5, new DoubleWordRegister(this)
                    .WithValueField(0, 14, name: "SET1COEFF11")
                    .WithValueField(14, 16, name: "SET1COEFF12")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterCoeSet1Group6, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "SET1COEFF13")
                    .WithValueField(16, 16, name: "SET1COEFF14")
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "FWSWCOEFFEN")
                    .WithFlag(1, name: "FWSELCOEFF")
                    .WithReservedBits(2, 26)
                    .WithValueField(28, 2, name: "CHFSWSEL")
                    .WithReservedBits(30, 1)
                    .WithFlag(31, name: "SWCOEFFEN")
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterLatencyControl, new DoubleWordRegister(this)
                    .WithValueField(0, 2, name: "CHFLATENCY")
                    .WithReservedBits(2, 30)
                },
                {(long)ModulatorAndDemodulatorRegisters.FrameSchTimeoutLength, new DoubleWordRegister(this, 0x00000040)
                    .WithValueField(0, 16, name: "FRMSCHTIME")
                    .WithReservedBits(16, 13)
                    .WithFlag(29, name: "PMRSTSYCNEN")
                    .WithFlag(30, name: "DSARSTSYCNEN")
                    .WithFlag(31, name: "PMENDSCHEN")
                },
                {(long)ModulatorAndDemodulatorRegisters.PreambleFilterCoefficients, new DoubleWordRegister(this, 0x3B3B3B3B)
                    .WithValueField(0, 32, name: "PREFILTCOEFF")
                },
                {(long)ModulatorAndDemodulatorRegisters.CollisionRestartControl, new DoubleWordRegister(this, 0x00001860)
                    .WithFlag(0, name: "RXRESTARTUPONMARSSI")
                    .WithReservedBits(1, 3)
                    .WithValueField(4, 4, name: "RXRESTARTMATHRESHOLD")
                    .WithValueField(8, 2, name: "RXRESTARTMALATCHSEL")
                    .WithValueField(10, 2, name: "RXRESTARTMACOMPENSEL")
                    .WithFlag(12, name: "RXRESTARTMATAP")
                    .WithReservedBits(13, 3)
                    .WithFlag(16, name: "RXRESTARTB4PREDET")
                    .WithReservedBits(17, 13)
                    .WithFlag(30, name: "ANTSWRSTFLTTDIS")
                    .WithFlag(31, name: "FLTRSTEN")
                },
                {(long)ModulatorAndDemodulatorRegisters.PreambleSenseMode, new DoubleWordRegister(this)
                    .WithFlag(0, name: "SQEN")
                    .WithFlag(1, name: "SQSWRST")
                    .WithReservedBits(2, 14)
                    .WithValueField(16, 16, name: "SQTIMOUT")
                },
                {(long)ModulatorAndDemodulatorRegisters.PreambleSenseModeExtended, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "SQSTG2TIMOUT")
                    .WithValueField(16, 16, name: "SQSTG3TIMOUT")
                },
                {(long)ModulatorAndDemodulatorRegisters.SignalQualityIndicator, new DoubleWordRegister(this)
                    .WithFlag(0, name: "SQISELECT")
                    .WithReservedBits(1, 15)
                    .WithValueField(16, 8, name: "CHIPERROR")
                    .WithReservedBits(24, 8)
                },
                {(long)ModulatorAndDemodulatorRegisters.AntennaDiversityModeControl, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "ADPRETHRESH")
                    .WithFlag(8, name: "ENADPRETHRESH")
                    .WithFlag(9, name: "ANTDIVDISCCA")
                    .WithFlag(10, name: "ANTDIVSELCCA")
                    .WithReservedBits(11, 21)
                },
                {(long)ModulatorAndDemodulatorRegisters.PhaseDemodulatorFwMode, new DoubleWordRegister(this)
                    .WithFlag(0, name: "FWSELANT")
                    .WithFlag(1, name: "FWANTSWCMD")
                    .WithReservedBits(2, 29)
                    .WithFlag(31, name: "FWANTDIVEN")
                },
                {(long)ModulatorAndDemodulatorRegisters.PhaseDemodulatorAntennaDiversity, new DoubleWordRegister(this, 0x0000000F)
                    .WithValueField(0, 5, name: "ANTWAIT")
                    .WithValueField(5, 8, name: "SKIPRSSITHD")
                    .WithReservedBits(13, 3)
                    .WithValueField(16, 8, name: "SKIPCORRTHD")
                    .WithReservedBits(24, 6)
                    .WithFlag(30, name: "SKIP2ANT")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.PhaseDemodulatorAntennaDiversityDecision, new DoubleWordRegister(this)
                    .WithValueField(0, 10, name: "CORRANDDIVTHD")
                    .WithValueField(10, 9, name: "RSSIANDDIVTHD")
                    .WithReservedBits(19, 9)
                    .WithFlag(28, name: "RSSICORR0")
                    .WithFlag(29, name: "RSSICORR1")
                    .WithFlag(30, name: "RSSICORR2")
                    .WithFlag(31, name: "RSSICORR3")
                },
                {(long)ModulatorAndDemodulatorRegisters.PhaseDemodulatorControl, new DoubleWordRegister(this, 0x01DF0004)
                    .WithValueField(0, 5, name: "PMDETTHD")
                    .WithValueField(5, 8, name: "PMTIMLOSTHD")
                    .WithFlag(13, name: "PMTIMLOSEN")
                    .WithFlag(14, name: "RSSIFLTBYP")
                    .WithFlag(15, name: "PMDETEN")
                    .WithValueField(16, 6, name: "REMODOSR")
                    .WithValueField(22, 4, name: "REMODDWN")
                    .WithValueField(26, 2, name: "REMODOUTSEL")
                    .WithFlag(28, name: "REMODEN")
                    .WithFlag(29, name: "BCRDETECTOR")
                    .WithFlag(30, name: "BCRTRECSCONC")
                    .WithFlag(31, name: "BCRLEGACYCONC")
                },
                {(long)ModulatorAndDemodulatorRegisters.SignalIdentifierControl0, new DoubleWordRegister(this)
                    .WithValueField(0, 2, name: "MODE")
                    .WithValueField(2, 8, name: "NOISETHRESH")
                    .WithValueField(10, 5, name: "PEAKNUMTHRESHLW")
                    .WithValueField(15, 3, name: "PEAKNUMTHRESHSW")
                    .WithValueField(18, 5, name: "SMALLSAMPLETHRESH")
                    .WithValueField(23, 7, name: "FREQNOMINAL")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.SignalIdentifierControl1, new DoubleWordRegister(this)
                    .WithValueField(0, 5, name: "SUPERCHIPTOLERANCE")
                    .WithValueField(5, 7, name: "SUPERCHIPMEDIAN")
                    .WithValueField(12, 11, name: "CORRTHRESH")
                    .WithValueField(23, 3, name: "CORRNUM")
                    .WithFlag(26, name: "FASTMODE")
                    .WithValueField(27, 5, name: "NARROWPULSETHRESH")
                },
                {(long)ModulatorAndDemodulatorRegisters.SignalIdentifierStatus, new DoubleWordRegister(this)
                    .WithValueField(0, 4, FieldMode.Read, name: "SISTATE")
                    .WithFlag(4, FieldMode.Read, name: "NOISE")
                    .WithValueField(5, 5, FieldMode.Read, name: "LWPEAKCOUNT")
                    .WithValueField(10, 5, FieldMode.Read, name: "NARROWCOUNT")
                    .WithFlag(15, FieldMode.Read, name: "TIMELOCK")
                    .WithFlag(16, FieldMode.Read, name: "SUPERCHIPFAIL")
                    .WithFlag(17, FieldMode.Read, name: "SUPERCHIPPASS")
                    .WithValueField(18, 3, FieldMode.Read, name: "TIMEOFFSET")
                    .WithValueField(21, 6, FieldMode.Read, name: "CORRPASSNUM")
                    .WithReservedBits(27, 2)
                    .WithFlag(29, FieldMode.Read, name: "SNIFFDONE")
                    .WithFlag(30, FieldMode.Read, name: "SIDET")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.ConfigureAntennaPatternExtended, new DoubleWordRegister(this)
                    .WithValueField(0, 30, name: "CFGANTPATTVALEXT")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.SignalIdentifierControl2, new DoubleWordRegister(this)
                    .WithFlag(0, name: "SIRSTAGCMODE")
                    .WithFlag(1, name: "SIRSTPRSMODE")
                    .WithFlag(2, name: "SIRSTCCAMODE")
                    .WithValueField(3, 3, name: "SUPERCHIPTHRESH")
                    .WithFlag(6, name: "DISSIFRAMEDET")
                    .WithFlag(7, name: "AGCRSTUPONSI")
                    .WithReservedBits(8, 24)
                },
                {(long)ModulatorAndDemodulatorRegisters.ChannelFilterSwitchTime, new DoubleWordRegister(this)
                    .WithValueField(0, 18, name: "CHFSWTIME")
                    .WithReservedBits(18, 14)
                },
                {(long)ModulatorAndDemodulatorRegisters.IrCalibrationControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "IRCALEN")
                    .WithValueField(1, 5, name: "MURSHF")
                    .WithReservedBits(6, 1)
                    .WithValueField(7, 6, name: "MUISHF")
                    .WithFlag(13, name: "IRCORREN")
                    .WithFlag(14, name: "IRCALCOEFRSTCMD")
                    .WithFlag(15, name: "IRCALIFADCDBG")
                    .WithReservedBits(16, 16)
                },
                {(long)ModulatorAndDemodulatorRegisters.IrCalCoefficientValues, new DoubleWordRegister(this)
                    .WithValueField(0, 15, FieldMode.Read, name: "CRV")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 15, FieldMode.Read, name: "CIV")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.IrCalCoefficientWrPerAntenna0, new DoubleWordRegister(this)
                    .WithValueField(0, 15, name: "CRVWD")
                    .WithFlag(15, name: "CRVWEN")
                    .WithValueField(16, 15, name: "CIVWD")
                    .WithFlag(31, name: "CIVWEN")
                },
                {(long)ModulatorAndDemodulatorRegisters.IrCalCoefficientWrPerAntenna1, new DoubleWordRegister(this)
                    .WithValueField(0, 15, name: "CRVWD")
                    .WithFlag(15, name: "CRVWEN")
                    .WithValueField(16, 15, name: "CIVWD")
                    .WithFlag(31, name: "CIVWEN")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdControl1, new DoubleWordRegister(this, 0x00080000)
                    .WithValueField(0, 32, name: "ADCTRL1")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdControl2, new DoubleWordRegister(this, 0x00000001)
                    .WithValueField(0, 32, name: "ADCTRL2")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual0, new DoubleWordRegister(this)
                    .WithValueField(0, 10, FieldMode.Read, name: "ADRSSI0")
                    .WithReservedBits(10, 6)
                    .WithValueField(16, 10, FieldMode.Read, name: "ADRSSI1")
                    .WithReservedBits(26, 6)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual1, new DoubleWordRegister(this)
                    .WithValueField(0, 17, FieldMode.Read, name: "ADCORR0")
                    .WithValueField(17, 15, FieldMode.Read, name: "ADSTAT1")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual2, new DoubleWordRegister(this)
                    .WithValueField(0, 10, FieldMode.Read, name: "ADRSSI0P")
                    .WithReservedBits(10, 6)
                    .WithValueField(16, 10, FieldMode.Read, name: "ADRSSI1P")
                    .WithReservedBits(26, 6)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual3, new DoubleWordRegister(this)
                    .WithValueField(0, 17, FieldMode.Read, name: "ADCORR0P")
                    .WithValueField(17, 15, FieldMode.Read, name: "ADSTAT2")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual4, new DoubleWordRegister(this, 0x0200003F)
                    .WithValueField(0, 6, name: "ADAGCGRTHR")
                    .WithReservedBits(6, 10)
                    .WithValueField(16, 10, name: "ADRSSIGRTHR")
                    .WithReservedBits(26, 4)
                    .WithValueField(30, 2, name: "ADGRMODE")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual5, new DoubleWordRegister(this, 0x0000FFFF)
                    .WithValueField(0, 17, name: "ADDIRECTCORR")
                    .WithReservedBits(17, 15)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual6, new DoubleWordRegister(this, 0x0000FFFF)
                    .WithValueField(0, 17, name: "ADBACORRTHR")
                    .WithValueField(17, 15, name: "ADBACORRDIFF")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual7, new DoubleWordRegister(this, 0x000003FF)
                    .WithValueField(0, 10, name: "ADBARSSITHR")
                    .WithReservedBits(10, 6)
                    .WithValueField(16, 10, name: "ADBARSSIDIFF")
                    .WithReservedBits(26, 6)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual8, new DoubleWordRegister(this, 0x0000FFFF)
                    .WithValueField(0, 17, name: "ADBACORRTHR2")
                    .WithReservedBits(17, 3)
                    .WithValueField(20, 2, name: "ADBAMODE")
                    .WithReservedBits(22, 2)
                    .WithValueField(24, 6, name: "ADBAAGCTHR")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual9, new DoubleWordRegister(this)
                    .WithValueField(0, 17, FieldMode.Read, name: "ADCORR1")
                    .WithReservedBits(17, 15)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdQual10, new DoubleWordRegister(this)
                    .WithValueField(0, 17, FieldMode.Read, name: "ADCORR1P")
                    .WithReservedBits(17, 15)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm0, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "ADSTATEC")
                    .WithValueField(4, 4, name: "ADSTATEP")
                    .WithValueField(8, 4, name: "ADSTATEP2")
                    .WithValueField(12, 4, name: "ADSTATEN")
                    .WithFlag(16, name: "ADTD0")
                    .WithFlag(17, name: "ADTD0P")
                    .WithFlag(18, name: "ADTD1")
                    .WithFlag(19, name: "ADTD1P")
                    .WithFlag(20, name: "ADSTATREAD")
                    .WithValueField(21, 5, name: "ADSTAT1SEL")
                    .WithValueField(26, 5, name: "ADSTAT2SEL")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm1, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADOSETANT0")
                    .WithValueField(16, 16, name: "ADOSETANT1")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm2, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADOSWITCHANT")
                    .WithValueField(16, 16, name: "ADORESTARTRX")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm3, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADOQUAL0UPDATE")
                    .WithValueField(16, 16, name: "ADOQUAL1UPDATE")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm4, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADOQUAL0CLEAR")
                    .WithValueField(16, 16, name: "ADOQUAL1CLEAR")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm5, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "ADOMUX")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm6, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADNEXTSTATESW0")
                    .WithValueField(16, 16, name: "ADNEXTSTATESW1")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm7, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADNEXTSTATESW2")
                    .WithValueField(16, 16, name: "ADNEXTSTATESW3")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm8, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADNEXTSTATESW4")
                    .WithValueField(16, 16, name: "ADNEXTSTATESW5")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm9, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADNEXTSTATESW6")
                    .WithValueField(16, 16, name: "ADNEXTSTATESW7")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm10, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADNEXTSTATESW8")
                    .WithValueField(16, 16, name: "ADNEXTSTATESW9")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm11, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADNEXTSTATESW10")
                    .WithValueField(16, 16, name: "ADNEXTSTATESW11")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm12, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADNEXTSTATESW12")
                    .WithValueField(16, 16, name: "ADNEXTSTATESW13")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm13, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADNEXTSTATESW14")
                    .WithValueField(16, 16, name: "ADNEXTSTATESW15")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm14, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMCOND0ENA")
                    .WithValueField(16, 16, name: "ADFSMCOND1ENA")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm15, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMCOND2ENA")
                    .WithValueField(16, 16, name: "ADFSMCOND3ENA")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm16, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMCOND0ENB")
                    .WithValueField(16, 16, name: "ADFSMCOND1ENB")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm17, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMCOND2ENB")
                    .WithValueField(16, 16, name: "ADFSMCOND3ENB")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm18, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "ADFSMCONDSEL")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm19, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMNEXTFORCE")
                    .WithValueField(16, 16, name: "ADFSMCONDTRUE")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm20, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADITENTEREN")
                    .WithValueField(16, 16, name: "ADITLEAVEEN")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm21, new DoubleWordRegister(this)
                    .WithFlag(0, name: "ADENTERFREEZEEN")
                    .WithFlag(1, name: "ADLEAVEFREEZEEN")
                    .WithFlag(2, name: "ADFROZEN")
                    .WithValueField(3, 4, name: "ADUNFREEZENEXT")
                    .WithFlag(7, name: "ADAS")
                    .WithFlag(8, name: "ADBA")
                    .WithReservedBits(9, 7)
                    .WithFlag(16, name: "ADUNFREEZE")
                    .WithReservedBits(17, 15)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm22, new DoubleWordRegister(this)
                    .WithValueField(0, 16, FieldMode.Read, name: "ADITENTERSTATUS")
                    .WithValueField(16, 16, FieldMode.Read, name: "ADITLEAVESTATUS")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm23, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMCOND0ENC")
                    .WithValueField(16, 16, name: "ADFSMCOND1ENC")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm24, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMCOND2ENC")
                    .WithValueField(16, 16, name: "ADFSMCOND3ENC")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm25, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMCONDOR0")
                    .WithValueField(16, 16, name: "ADFSMCONDOR1")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm26, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMCOND0END")
                    .WithValueField(16, 16, name: "ADFSMCOND1END")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm27, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADFSMCOND2END")
                    .WithValueField(16, 16, name: "ADFSMCOND3END")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm28, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADOSETANTFORCE")
                    .WithValueField(16, 16, name: "ADORESTARTRXFORCE")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm29, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "ADOQUALUPDATEFORCE")
                    .WithValueField(16, 16, name: "ADOQUALCLEARFORCE")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdFsm30, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "ADODEMODRXREQ")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc1, new DoubleWordRegister(this, 0x01200040)
                    .WithFlag(0, name: "ADPCEN")
                    .WithValueField(1, 7, name: "ADPCWNDSIZECHIP")
                    .WithValueField(8, 7, name: "ADPCCORROFFSETCHIP")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 8, name: "ADPCTIMINGBAUDS")
                    .WithValueField(24, 3, name: "ADPCWNDCNT")
                    .WithValueField(27, 5, name: "ADPCSKIPCHIPS")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc2, new DoubleWordRegister(this, 0x000000A0)
                    .WithValueField(0, 10, name: "ADPCCORRSAMPLES")
                    .WithValueField(10, 8, name: "ADPCPRETIMINGBAUDS")
                    .WithFlag(18, name: "ADENCORR32")
                    .WithValueField(19, 8, name: "ADPCSIGAMPTHR")
                    .WithValueField(27, 3, name: "ADPCWNDCNTRST")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc3, new DoubleWordRegister(this, 0x01005008)
                    .WithFlag(0, name: "ADBBSSEN")
                    .WithValueField(1, 3, name: "ADBBSSFILTLENGTH")
                    .WithFlag(4, name: "ADBBSSAVGEN")
                    .WithValueField(5, 3, name: "ADBBSSAVGPER")
                    .WithValueField(8, 4, name: "ADBBSSAMPMANT")
                    .WithValueField(12, 4, name: "ADBBSSAMPEXP")
                    .WithValueField(16, 8, name: "ADBBSSAVGWAIT")
                    .WithFlag(24, name: "ADBBSSAVGFREEZE")
                    .WithFlag(25, name: "ADBBSSSELWRDATA")
                    .WithReservedBits(26, 6)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc4, new DoubleWordRegister(this, 0x1F1F1F1F)
                    .WithValueField(0, 5, name: "ADBBSSAMPLUT0")
                    .WithReservedBits(5, 3)
                    .WithValueField(8, 5, name: "ADBBSSAMPLUT1")
                    .WithReservedBits(13, 3)
                    .WithValueField(16, 5, name: "ADBBSSAMPLUT2")
                    .WithReservedBits(21, 3)
                    .WithValueField(24, 5, name: "ADBBSSAMPLUT3")
                    .WithReservedBits(29, 3)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc5, new DoubleWordRegister(this, 0x1B1F1F1F)
                    .WithValueField(0, 5, name: "ADBBSSAMPLUT4")
                    .WithReservedBits(5, 3)
                    .WithValueField(8, 5, name: "ADBBSSAMPLUT5")
                    .WithReservedBits(13, 3)
                    .WithValueField(16, 5, name: "ADBBSSAMPLUT6")
                    .WithReservedBits(21, 3)
                    .WithValueField(24, 5, name: "ADBBSSAMPLUT7")
                    .WithReservedBits(29, 3)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc6, new DoubleWordRegister(this, 0x11131518)
                    .WithValueField(0, 5, name: "ADBBSSAMPLUT8")
                    .WithReservedBits(5, 3)
                    .WithValueField(8, 5, name: "ADBBSSAMPLUT9")
                    .WithReservedBits(13, 3)
                    .WithValueField(16, 5, name: "ADBBSSAMPLUT10")
                    .WithReservedBits(21, 3)
                    .WithValueField(24, 5, name: "ADBBSSAMPLUT11")
                    .WithReservedBits(29, 3)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc7, new DoubleWordRegister(this, 0x0C0D0E10)
                    .WithValueField(0, 5, name: "ADBBSSAMPLUT12")
                    .WithReservedBits(5, 3)
                    .WithValueField(8, 5, name: "ADBBSSAMPLUT13")
                    .WithReservedBits(13, 3)
                    .WithValueField(16, 5, name: "ADBBSSAMPLUT14")
                    .WithReservedBits(21, 3)
                    .WithValueField(24, 5, name: "ADBBSSAMPLUT15")
                    .WithReservedBits(29, 3)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc8, new DoubleWordRegister(this, 0x2F87C145)
                    .WithValueField(0, 3, name: "ADPCOSR")
                    .WithValueField(3, 3, name: "ADPCANTSAMPOFFSET")
                    .WithFlag(6, name: "ADPCANTSAMPSWITCHWAIT")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 6, name: "ADPCANTSAMPBUF")
                    .WithValueField(14, 8, name: "ADPCANTSAMPWRITE")
                    .WithValueField(22, 10, name: "ADPCANTSAMPSWITCH")
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc9, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "ADBBSSAMPAVGLIM")
                    .WithValueField(8, 8, name: "ADBBSSAMPTHR")
                    .WithFlag(16, name: "ADBBSSSYNCEN")
                    .WithValueField(17, 4, name: "ADBBSSUPTHR")
                    .WithValueField(21, 4, name: "ADBBSSDNTHR")
                    .WithReservedBits(25, 7)
                },
                {(long)ModulatorAndDemodulatorRegisters.AdPc10, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "ADBBSSAMPJUMP")
                    .WithFlag(8, name: "ADBBSSCHANGEEN")
                    .WithValueField(9, 4, name: "ADBBSSCHGUPTHR")
                    .WithValueField(13, 4, name: "ADBBSSCHGDNTHR")
                    .WithReservedBits(17, 15)
                },
                {(long)ModulatorAndDemodulatorRegisters.HadmControl0, new DoubleWordRegister(this)
                    .WithFlag(0, name: "HADMEN")
                    .WithFlag(1, name: "TESEN")
                    .WithFlag(2, name: "PESEN")
                    .WithFlag(3, name: "SNDSEQEN")
                    .WithFlag(4, name: "ROLE")
                    .WithFlag(5, name: "RTTPHY")
                    .WithReservedBits(6, 6)
                    .WithValueField(12, 2, name: "PM")
                    .WithFlag(14, name: "AVGMODE")
                    .WithReservedBits(15, 11)
                    .WithValueField(26, 2, name: "DFTSCALE")
                    .WithFlag(28, name: "PKTSENTSEL")
                    .WithFlag(29, name: "TXUPSAMPOSR4")
                    .WithFlag(30, name: "SSAFCGEAR")
                    .WithFlag(31, name: "SRC2AUTOSCALE")
                },
                {(long)ModulatorAndDemodulatorRegisters.HadmControl1, new DoubleWordRegister(this, 0x00040000)
                    .WithValueField(0, 3, name: "STEPSTATE")
                    .WithReservedBits(3, 5)
                    .WithValueField(8, 7, name: "DFTSTARTOFF")
                    .WithValueField(15, 4, name: "MAXSCHWIN")
                    .WithReservedBits(19, 3)
                    .WithValueField(22, 10, name: "AVGSTARTOFF")
                },
                {(long)ModulatorAndDemodulatorRegisters.HadmStatus0, new DoubleWordRegister(this)
                    .WithValueField(0, 16, FieldMode.Read, name: "AVG0")
                    .WithValueField(16, 16, FieldMode.Read, name: "AVG1")
                },
                {(long)ModulatorAndDemodulatorRegisters.HadmStatus1, new DoubleWordRegister(this)
                    .WithValueField(0, 16, FieldMode.Read, name: "FREQOFFSET")
                    .WithValueField(16, 16, FieldMode.Read, name: "TIMETOX")
                },
                {(long)ModulatorAndDemodulatorRegisters.HadmStatus2, new DoubleWordRegister(this)
                    .WithValueField(0, 10, FieldMode.Read, name: "COSTLATE1")
                    .WithValueField(10, 10, FieldMode.Read, name: "COSTCURR1")
                    .WithValueField(20, 10, FieldMode.Read, name: "COSTEARL1")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.HadmStatus3, new DoubleWordRegister(this)
                    .WithValueField(0, 10, FieldMode.Read, name: "COSTLATE0")
                    .WithValueField(10, 10, FieldMode.Read, name: "COSTCURR0")
                    .WithValueField(20, 10, FieldMode.Read, name: "COSTEARL0")
                    .WithReservedBits(30, 2)
                },
                {(long)ModulatorAndDemodulatorRegisters.HadmStatus4, new DoubleWordRegister(this)
                    .WithValueField(0, 15, FieldMode.Read, name: "SBSP500I")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 15, FieldMode.Read, name: "SBSP500Q")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.HadmStatus5, new DoubleWordRegister(this)
                    .WithValueField(0, 15, FieldMode.Read, name: "SBSM500I")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 15, FieldMode.Read, name: "SBSM500Q")
                    .WithReservedBits(31, 1)
                },
                {(long)ModulatorAndDemodulatorRegisters.HadmStatus6, new DoubleWordRegister(this)
                    .WithValueField(0, 16, FieldMode.Read, name: "FREQMEAS")
                    .WithReservedBits(16, 4)
                    .WithValueField(20, 6, FieldMode.Read, name: "SBSPSCALE")
                    .WithValueField(26, 6, FieldMode.Read, name: "SBSMSCALE")
                },
                {(long)ModulatorAndDemodulatorRegisters.Src2NcoControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "SRC2NCOEN")
                    .WithValueField(1, 14, name: "PHASEPERCLK")
                    .WithReservedBits(15, 17)
                },
                {(long)ModulatorAndDemodulatorRegisters.Spare, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "SPARE")
                    .WithReservedBits(8, 24)
                },
            };
            return new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private DoubleWordRegisterCollection BuildProtocolTimerRegistersCollection()
        {
            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            {
                {(long)ProtocolTimerRegisters.IpVersion, new DoubleWordRegister(this, 0x00000002)
                    .WithValueField(0, 32, FieldMode.Read, name: "IPVERSION")
                },
                {(long)ProtocolTimerRegisters.Enable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "EN")
                    .WithReservedBits(1, 31)
                },
                {(long)ProtocolTimerRegisters.ListenBeforeTalkETSIStandardSupport, new DoubleWordRegister(this)
                    .WithFlag(0, writeCallback: (_, value) =>
                        {
                            if(value)
                            {
                                this.Log(LogLevel.Warning, "PROTIMER ETSI linear backoff (ETSIEN) is not supported");
                            }
                        }, name: "ETSIEN")
                    .WithFlag(1, name: "GRANULARLESSTHANRXWARM")
                    .WithValueField(2, 8, name: "RXWARMTHLD")
                    .WithValueField(10, 16, name: "CCAFIXED")
                    .WithReservedBits(26, 6)
                },
                {(long)ProtocolTimerRegisters.LinearRandomValueGeneratedByFirmware0, new DoubleWordRegister(this)
                    .WithValueField(0, 9, name: "RANDOM0")
                    .WithValueField(9, 9, name: "RANDOM1")
                    .WithValueField(18, 9, name: "RANDOM2")
                    .WithReservedBits(27, 5)
                },
                {(long)ProtocolTimerRegisters.LinearRandomValueGeneratedByFirmware1, new DoubleWordRegister(this)
                    .WithValueField(0, 9, name: "RANDOM3")
                    .WithValueField(9, 9, name: "RANDOM4")
                    .WithValueField(18, 9, name: "RANDOM5")
                    .WithReservedBits(27, 5)
                },
                {(long)ProtocolTimerRegisters.LinearRandomValueGeneratedByFirmware2, new DoubleWordRegister(this)
                    .WithValueField(0, 9, name: "RANDOM6")
                    .WithValueField(9, 9, name: "RANDOM7")
                    .WithReservedBits(18, 14)
                },
                {(long)ProtocolTimerRegisters.Control, new DoubleWordRegister(this)
                    .WithReservedBits(0, 1)
                    .WithFlag(1, name: "DEBUGRUN")
                    .WithFlag(2, name: "DMACLRACT")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, name: "OSMEN")
                    .WithFlag(5, name: "ZEROSTARTEN")
                    .WithReservedBits(6, 2)
                    .WithEnumField<DoubleWordRegister, PROTIMER_PreCounterSource>(8, 2, out PROTIMER_preCounterSource, changeCallback: (_, value) =>
                        {
                            switch(value)
                            {
                                case PROTIMER_PreCounterSource.None:
                                    PROTIMER_Enabled = false;
                                    break;
                                case PROTIMER_PreCounterSource.Clock:
                                    // wait for the start command to actually start the proTimer
                                    break;
                                default:
                                    PROTIMER_Enabled = false;
                                    this.Log(LogLevel.Error, "Invalid PRECNTSRC value");
                                    break;
                            }
                        }, name: "PRECNTSRC")
                    .WithReservedBits(10, 2)
                    .WithEnumField<DoubleWordRegister, PROTIMER_BaseCounterSource>(12, 2, out PROTIMER_baseCounterSource, changeCallback: (_, value) =>
                        {
                            switch(value)
                            {
                                case PROTIMER_BaseCounterSource.Unused0:
                                case PROTIMER_BaseCounterSource.Unused1:
                                    this.Log(LogLevel.Error, "Invalid BASECNTSRC value");
                                    break;
                            }
                        }, name: "BASECNTSRC")
                    .WithReservedBits(14, 2)
                    .WithEnumField<DoubleWordRegister, PROTIMER_WrapCounterSource>(16, 2, out PROTIMER_wrapCounterSource, changeCallback: (_, value) =>
                        {
                            switch(value)
                            {
                                case PROTIMER_WrapCounterSource.Unused:
                                    this.Log(LogLevel.Error, "Invalid WRAPCNTSRC value");
                                    break;
                            }
                        }, name: "WRAPCNTSRC")
                    .WithReservedBits(18, 2)
                    .WithEnumField<DoubleWordRegister, PROTIMER_TimeoutCounterSource>(20, 2, out PROTIMER_timeoutCounter[0].Source, name: "TOUT0SRC")
                    .WithEnumField<DoubleWordRegister, PROTIMER_TimeoutCounterSource>(22, 2, out PROTIMER_timeoutCounter[0].SyncSource, name: "TOUT0SYNCSRC")
                    .WithEnumField<DoubleWordRegister, PROTIMER_TimeoutCounterSource>(24, 2, out PROTIMER_timeoutCounter[1].Source, name: "TOUT1SRC")
                    .WithEnumField<DoubleWordRegister, PROTIMER_TimeoutCounterSource>(26, 2, out PROTIMER_timeoutCounter[1].SyncSource, name: "TOUT1SYNCSRC")
                    .WithEnumField<DoubleWordRegister, PROTIMER_RepeatMode>(28, 1, out PROTIMER_timeoutCounter[0].Mode, name: "TOUT0MODE")
                    .WithEnumField<DoubleWordRegister, PROTIMER_RepeatMode>(29, 1, out PROTIMER_timeoutCounter[1].Mode, name: "TOUT1MODE")
                    .WithReservedBits(30, 2)
                },
                {(long)ProtocolTimerRegisters.Command, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Set, writeCallback: (_, value) => { if(PROTIMER_preCounterSource.Value == PROTIMER_PreCounterSource.Clock && value) { PROTIMER_Enabled = true;} }, name: "START")
                    .WithFlag(1, FieldMode.Set, writeCallback: (_, value) => { if(value) PROTIMER_RtcSyncStart(); }, name: "RTCSYNCSTART")
                    .WithFlag(2, FieldMode.Set, writeCallback: (_, value) => { if(value) PROTIMER_Enabled = false; }, name: "STOP")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, FieldMode.Set, writeCallback: (_, value) => { if(value) PROTIMER_timeoutCounter[0].Start(); }, name: "TOUT0START")
                    .WithFlag(5, FieldMode.Set, writeCallback: (_, value) => { if(value) PROTIMER_timeoutCounter[0].Stop(); }, name: "TOUT0STOP")
                    .WithFlag(6, FieldMode.Set, writeCallback: (_, value) => { if(value) PROTIMER_timeoutCounter[1].Start(); }, name: "TOUT1START")
                    .WithFlag(7, FieldMode.Set, writeCallback: (_, value) => { if(value) PROTIMER_timeoutCounter[1].Stop(); }, name: "TOUT1STOP")
                    .WithFlag(8, FieldMode.Set, writeCallback: (_, value) => { if(value) PROTIMER_txRequestState = PROTIMER_TxRxRequestState.Idle; RAC_UpdateRadioStateMachine(); }, name: "FORCETXIDLE")
                    .WithFlag(9, FieldMode.Set, writeCallback: (_, value) => { if(value) PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.Idle; RAC_UpdateRadioStateMachine(); }, name: "FORCERXIDLE")
                    .WithFlag(10, name: "FORCERXRX")
                    .WithReservedBits(11, 5)
                    .WithFlag(16, FieldMode.Set, writeCallback: (_, value) => { if (value) PROTIMER_ListenBeforeTalkStartCommand(); }, name: "LBTSTART")
                    .WithFlag(17, FieldMode.Set, writeCallback: (_, value) => { if (value) PROTIMER_ListenBeforeTalkPauseCommand(); }, name: "LBTPAUSE")
                    .WithFlag(18, FieldMode.Set, writeCallback: (_, value) => { if(value) PROTIMER_ListenBeforeTalkStopCommand(); }, name: "LBTSTOP")
                    .WithReservedBits(19, 13)
                    .WithWriteCallback((_, __) => { PROTIMER_HandleChangedParams(); UpdateInterrupts(); })
                },
                {(long)ProtocolTimerRegisters.Status, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => PROTIMER_Enabled, name: "RUNNING")
                    .WithFlag(1, out PROTIMER_listenBeforeTalkSync, FieldMode.Read, name: "LBTSYNC")
                    .WithFlag(2, out PROTIMER_listenBeforeTalkRunning, FieldMode.Read, name: "LBTRUNNING")
                    .WithFlag(3, out PROTIMER_listenBeforeTalkPaused, FieldMode.Read, name: "LBTPAUSED")
                    .WithFlag(4, out PROTIMER_timeoutCounter[0].Running, FieldMode.Read, name: "TOUT0RUNNING")
                    .WithFlag(5, out PROTIMER_timeoutCounter[0].Synchronizing, FieldMode.Read, name: "TOUT0SYNC")
                    .WithFlag(6, out PROTIMER_timeoutCounter[1].Running, FieldMode.Read, name: "TOUT1RUNNING")
                    .WithFlag(7, out PROTIMER_timeoutCounter[1].Synchronizing, FieldMode.Read, name: "TOUT1SYNC")
                    .WithFlag(8, out PROTIMER_captureCompareChannel[0].CaptureValid, FieldMode.Read, name: "ICV0")
                    .WithFlag(9, out PROTIMER_captureCompareChannel[1].CaptureValid, FieldMode.Read, name: "ICV1")
                    .WithFlag(10, out PROTIMER_captureCompareChannel[2].CaptureValid, FieldMode.Read, name: "ICV2")
                    .WithFlag(11, out PROTIMER_captureCompareChannel[3].CaptureValid, FieldMode.Read, name: "ICV3")
                    .WithFlag(12, out PROTIMER_captureCompareChannel[4].CaptureValid, FieldMode.Read, name: "ICV4")
                    .WithFlag(13, out PROTIMER_captureCompareChannel[5].CaptureValid, FieldMode.Read, name: "ICV5")
                    .WithFlag(14, out PROTIMER_captureCompareChannel[6].CaptureValid, FieldMode.Read, name: "ICV6")
                    .WithFlag(15, out PROTIMER_captureCompareChannel[7].CaptureValid, FieldMode.Read, name: "ICV7")
                    .WithReservedBits(16, 16)
                },
                {(long)ProtocolTimerRegisters.PreCounterValue, new DoubleWordRegister(this)
                    // We don't tick the PRECNT value, so just always return 0
                    .WithValueField(0, 16, valueProviderCallback: _ => 0, name: "PRECNT")
                    .WithReservedBits(16, 16)
                },
                {(long)ProtocolTimerRegisters.BaseCounterValue, new DoubleWordRegister(this)
                    .WithValueField(0, 16, valueProviderCallback: _ => PROTIMER_BaseCounterValue, writeCallback: (_, value) => PROTIMER_BaseCounterValue = (ushort)value, name: "BASECNT")
                    .WithReservedBits(16, 16)
                    .WithWriteCallback((_, __) => PROTIMER_HandleChangedParams())
                },
                {(long)ProtocolTimerRegisters.WrapCounterValue, new DoubleWordRegister(this)
                    .WithValueField(0, 32, valueProviderCallback: _ =>  PROTIMER_WrapCounterValue, writeCallback: (_, value) => PROTIMER_WrapCounterValue = (uint)value, name: "WRAPCNT")
                    .WithWriteCallback((_, __) => PROTIMER_HandleChangedParams())
                },
                {(long)ProtocolTimerRegisters.BaseAndPreCounterValues, new DoubleWordRegister(this)
                    // We don't tick the PRECNT value, so just always return 0
                    .WithValueField(0, 16, FieldMode.Read, valueProviderCallback: _ => 0, name: "PRECNTV")
                    .WithValueField(16, 16, FieldMode.Read, valueProviderCallback: _ => PROTIMER_BaseCounterValue, name: "BASECNTV")
                    .WithWriteCallback((_, __) => PROTIMER_HandleChangedParams())
                },
                {(long)ProtocolTimerRegisters.PreCounterTopValue, new DoubleWordRegister(this, 0xFFFF00)
                    .WithValueField(0, 8, out PROTIMER_preCounterTopFractional, name: "PRECNTTOPFRAC")
                    .WithValueField(8, 16, out PROTIMER_preCounterTopInteger, name: "PRECNTTOP")
                    .WithReservedBits(24, 8)
                    .WithWriteCallback((_, __) => PROTIMER_HandleChangedParams())
                },
                {(long)ProtocolTimerRegisters.BaseCounterTopValue, new DoubleWordRegister(this, 0xFFFF)
                    .WithValueField(0, 16, out PROTIMER_baseCounterTop, name: "BASECNTTOP")
                    .WithReservedBits(16, 16)
                    .WithWriteCallback((_, __) => PROTIMER_HandleChangedParams())
                },
                {(long)ProtocolTimerRegisters.WrapCounterTopValue, new DoubleWordRegister(this, 0xFFFFFFFF)
                    .WithValueField(0, 32, out PROTIMER_wrapCounterTop, name: "WRAPCNTTOP")
                    .WithWriteCallback((_, __) => PROTIMER_HandleChangedParams())
                },
                {(long)ProtocolTimerRegisters.LatchedWrapCounterValue, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "LWRAPCNT")
                },
                {(long)ProtocolTimerRegisters.PreCounterTopAdjustValue, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "PRECNTTOPADJ")
                    .WithReservedBits(16, 16)
                },
                {(long)ProtocolTimerRegisters.Timeout0Counter, new DoubleWordRegister(this)
                    .WithValueField(0, 16, out PROTIMER_timeoutCounter[0].PreCounter, name: "TOUT0PCNT")
                    .WithValueField(16, 16, out PROTIMER_timeoutCounter[0].Counter, name: "TOUT0CNT")
                },
                {(long)ProtocolTimerRegisters.Timeout0CounterTop, new DoubleWordRegister(this, 0x00FF00FF)
                    .WithValueField(0, 16, out PROTIMER_timeoutCounter[0].PreCounterTop, name: "TOUT0PCNTTOP")
                    .WithValueField(16, 16, out PROTIMER_timeoutCounter[0].CounterTop, name: "TOUT0CNTTOP")
                },
                {(long)ProtocolTimerRegisters.Timeout0Compare, new DoubleWordRegister(this)
                    .WithValueField(0, 16, out PROTIMER_timeoutCounter[0].PreCounterCompare, name: "TOUT0PCNTCOMP")
                    .WithValueField(16, 16, out PROTIMER_timeoutCounter[0].CounterCompare, name: "TOUT0CNTCOMP")
                },
                {(long)ProtocolTimerRegisters.Timeout1Counter, new DoubleWordRegister(this)
                    .WithValueField(0, 16, out PROTIMER_timeoutCounter[1].PreCounter, name: "TOUT1PCNT")
                    .WithValueField(16, 16, out PROTIMER_timeoutCounter[1].Counter, name: "TOUT1CNT")
                },
                {(long)ProtocolTimerRegisters.Timeout1CounterTop, new DoubleWordRegister(this, 0x00FF00FF)
                    .WithValueField(0, 16, out PROTIMER_timeoutCounter[1].PreCounterTop, name: "TOUT1PCNTTOP")
                    .WithValueField(16, 16, out PROTIMER_timeoutCounter[1].CounterTop, name: "TOUT1CNTTOP")
                },
                {(long)ProtocolTimerRegisters.Timeout1Compare, new DoubleWordRegister(this)
                    .WithValueField(0, 16, out PROTIMER_timeoutCounter[1].PreCounterCompare, name: "TOUT1PCNTCOMP")
                    .WithValueField(16, 16, out PROTIMER_timeoutCounter[1].CounterCompare, name: "TOUT1CNTCOMP")
                },
                {(long)ProtocolTimerRegisters.InterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out PROTIMER_preCounterOverflowInterrupt, name: "PRECNTOFIF")
                    .WithFlag(1, out PROTIMER_baseCounterOverflowInterrupt, name: "BASECNTOFIF")
                    .WithFlag(2, out PROTIMER_wrapCounterOverflowInterrupt, name: "WRAPCNTOFIF")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, out PROTIMER_timeoutCounter[0].UnderflowInterrupt, name: "TOUT0IF")
                    .WithFlag(5, out PROTIMER_timeoutCounter[1].UnderflowInterrupt, name: "TOUT1IF")
                    .WithFlag(6, out PROTIMER_timeoutCounter[0].MatchInterrupt, name: "TOUT0MATCHIF")
                    .WithFlag(7, out PROTIMER_timeoutCounter[1].MatchInterrupt, name: "TOUT1MATCHIF")
                    .WithFlag(8, out PROTIMER_captureCompareChannel[0].InterruptField, name: "CC0IF")
                    .WithFlag(9, out PROTIMER_captureCompareChannel[1].InterruptField, name: "CC1IF")
                    .WithFlag(10, out PROTIMER_captureCompareChannel[2].InterruptField, name: "CC2IF")
                    .WithFlag(11, out PROTIMER_captureCompareChannel[3].InterruptField, name: "CC3IF")
                    .WithFlag(12, out PROTIMER_captureCompareChannel[4].InterruptField, name: "CC4IF")
                    .WithFlag(13, out PROTIMER_captureCompareChannel[5].InterruptField, name: "CC5IF")
                    .WithFlag(14, out PROTIMER_captureCompareChannel[6].InterruptField, name: "CC6IF")
                    .WithFlag(15, out PROTIMER_captureCompareChannel[7].InterruptField, name: "CC7IF")
                    .WithFlag(16, out PROTIMER_captureCompareChannel[0].OverflowInterrupt, name: "COF0IF")
                    .WithFlag(17, out PROTIMER_captureCompareChannel[1].OverflowInterrupt, name: "COF1IF")
                    .WithFlag(18, out PROTIMER_captureCompareChannel[2].OverflowInterrupt, name: "COF2IF")
                    .WithFlag(19, out PROTIMER_captureCompareChannel[3].OverflowInterrupt, name: "COF3IF")
                    .WithFlag(20, out PROTIMER_captureCompareChannel[4].OverflowInterrupt, name: "COF4IF")
                    .WithFlag(21, out PROTIMER_captureCompareChannel[5].OverflowInterrupt, name: "COF5IF")
                    .WithFlag(22, out PROTIMER_captureCompareChannel[6].OverflowInterrupt, name: "COF6IF")
                    .WithFlag(23, out PROTIMER_captureCompareChannel[7].OverflowInterrupt, name: "COF7IF")
                    .WithFlag(24, out PROTIMER_listenBeforeTalkSuccessInterrupt, name: "LBTSUCCESSIF")
                    .WithFlag(25, out PROTIMER_listenBeforeTalkFailureInterrupt, name: "LBTFAILUREIF")
                    .WithFlag(26, name: "LBTPAUSEDIF")
                    .WithFlag(27, out PROTIMER_listenBeforeTalkRetryInterrupt, name: "LBTRETRYIF")
                    .WithFlag(28, out PROTIMER_rtccSynchedInterrupt, name: "RTCCSYNCHEDIF")
                    .WithFlag(29, out PROTIMER_listenBeforeTalkTimeoutCounterMatchInterrupt, name: "TOUT0MATCHLBTIF")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)ProtocolTimerRegisters.InterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out PROTIMER_preCounterOverflowInterruptEnable, name: "PRECNTOFIEN")
                    .WithFlag(1, out PROTIMER_baseCounterOverflowInterruptEnable, name: "BASECNTOFIEN")
                    .WithFlag(2, out PROTIMER_wrapCounterOverflowInterruptEnable, name: "WRAPCNTOFIEN")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, out PROTIMER_timeoutCounter[0].UnderflowInterruptEnable, name: "TOUT0IEN")
                    .WithFlag(5, out PROTIMER_timeoutCounter[1].UnderflowInterruptEnable, name: "TOUT1IEN")
                    .WithFlag(6, out PROTIMER_timeoutCounter[0].MatchInterruptEnable, name: "TOUT0MATCHIEN")
                    .WithFlag(7, out PROTIMER_timeoutCounter[1].MatchInterruptEnable, name: "TOUT1MATCHIEN")
                    .WithFlag(8, out PROTIMER_captureCompareChannel[0].InterruptEnable, name: "CC0IEN")
                    .WithFlag(9, out PROTIMER_captureCompareChannel[1].InterruptEnable, name: "CC1IEN")
                    .WithFlag(10, out PROTIMER_captureCompareChannel[2].InterruptEnable, name: "CC2IEN")
                    .WithFlag(11, out PROTIMER_captureCompareChannel[3].InterruptEnable, name: "CC3IEN")
                    .WithFlag(12, out PROTIMER_captureCompareChannel[4].InterruptEnable, name: "CC4IEN")
                    .WithFlag(13, out PROTIMER_captureCompareChannel[5].InterruptEnable, name: "CC5IEN")
                    .WithFlag(14, out PROTIMER_captureCompareChannel[6].InterruptEnable, name: "CC6IEN")
                    .WithFlag(15, out PROTIMER_captureCompareChannel[7].InterruptEnable, name: "CC7IEN")
                    .WithFlag(16, out PROTIMER_captureCompareChannel[0].OverflowInterruptEnable, name: "COF0IEN")
                    .WithFlag(17, out PROTIMER_captureCompareChannel[1].OverflowInterruptEnable, name: "COF1IEN")
                    .WithFlag(18, out PROTIMER_captureCompareChannel[2].OverflowInterruptEnable, name: "COF2IEN")
                    .WithFlag(19, out PROTIMER_captureCompareChannel[3].OverflowInterruptEnable, name: "COF3IEN")
                    .WithFlag(20, out PROTIMER_captureCompareChannel[4].OverflowInterruptEnable, name: "COF4IEN")
                    .WithFlag(21, out PROTIMER_captureCompareChannel[5].OverflowInterruptEnable, name: "COF5IEN")
                    .WithFlag(22, out PROTIMER_captureCompareChannel[6].OverflowInterruptEnable, name: "COF6IEN")
                    .WithFlag(23, out PROTIMER_captureCompareChannel[7].OverflowInterruptEnable, name: "COF7IEN")
                    .WithFlag(24, out PROTIMER_listenBeforeTalkSuccessInterruptEnable, name: "LBTSUCCESSIEN")
                    .WithFlag(25, out PROTIMER_listenBeforeTalkFailureInterruptEnable, name: "LBTFAILUREIEN")
                    .WithFlag(26, name: "LBTPAUSEDIEN")
                    .WithFlag(27, out PROTIMER_listenBeforeTalkRetryInterruptEnable, name: "LBTRETRYIEN")
                    .WithFlag(28, out PROTIMER_rtccSynchedInterruptEnable, name: "RTCCSYNCHEDIEN")
                    .WithFlag(29, out PROTIMER_listenBeforeTalkTimeoutCounterMatchInterruptEnable, name: "TOUT0MATCHLBTIEN")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)ProtocolTimerRegisters.SequencerInterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out PROTIMER_seqPreCounterOverflowInterrupt, name: "PRECNTOFSEQIF")
                    .WithFlag(1, out PROTIMER_seqBaseCounterOverflowInterrupt, name: "BASECNTOFSEQIF")
                    .WithFlag(2, out PROTIMER_seqWrapCounterOverflowInterrupt, name: "WRAPCNTOFSEQIF")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, out PROTIMER_timeoutCounter[0].SeqUnderflowInterrupt, name: "TOUT0SEQIF")
                    .WithFlag(5, out PROTIMER_timeoutCounter[1].SeqUnderflowInterrupt, name: "TOUT1SEQIF")
                    .WithFlag(6, out PROTIMER_timeoutCounter[0].SeqMatchInterrupt, name: "TOUT0MATCHSEQIF")
                    .WithFlag(7, out PROTIMER_timeoutCounter[1].SeqMatchInterrupt, name: "TOUT1MATCHSEQIF")
                    .WithFlag(8, out PROTIMER_captureCompareChannel[0].SeqInterruptField, name: "CC0SEQIF")
                    .WithFlag(9, out PROTIMER_captureCompareChannel[1].SeqInterruptField, name: "CC1SEQIF")
                    .WithFlag(10, out PROTIMER_captureCompareChannel[2].SeqInterruptField, name: "CC2SEQIF")
                    .WithFlag(11, out PROTIMER_captureCompareChannel[3].SeqInterruptField, name: "CC3SEQIF")
                    .WithFlag(12, out PROTIMER_captureCompareChannel[4].SeqInterruptField, name: "CC4SEQIF")
                    .WithFlag(13, out PROTIMER_captureCompareChannel[5].SeqInterruptField, name: "CC5SEQIF")
                    .WithFlag(14, out PROTIMER_captureCompareChannel[6].SeqInterruptField, name: "CC6SEQIF")
                    .WithFlag(15, out PROTIMER_captureCompareChannel[7].SeqInterruptField, name: "CC7SEQIF")
                    .WithFlag(16, out PROTIMER_captureCompareChannel[0].SeqOverflowInterrupt, name: "COF0SEQIF")
                    .WithFlag(17, out PROTIMER_captureCompareChannel[1].SeqOverflowInterrupt, name: "COF1SEQIF")
                    .WithFlag(18, out PROTIMER_captureCompareChannel[2].SeqOverflowInterrupt, name: "COF2SEQIF")
                    .WithFlag(19, out PROTIMER_captureCompareChannel[3].SeqOverflowInterrupt, name: "COF3SEQIF")
                    .WithFlag(20, out PROTIMER_captureCompareChannel[4].SeqOverflowInterrupt, name: "COF4SEQIF")
                    .WithFlag(21, out PROTIMER_captureCompareChannel[5].SeqOverflowInterrupt, name: "COF5SEQIF")
                    .WithFlag(22, out PROTIMER_captureCompareChannel[6].SeqOverflowInterrupt, name: "COF6SEQIF")
                    .WithFlag(23, out PROTIMER_captureCompareChannel[7].SeqOverflowInterrupt, name: "COF7SEQIF")
                    .WithFlag(24, out PROTIMER_seqListenBeforeTalkSuccessInterrupt, name: "LBTSUCCESSSEQIF")
                    .WithFlag(25, out PROTIMER_seqListenBeforeTalkFailureInterrupt, name: "LBTFAILURESEQIF")
                    .WithFlag(26, name: "LBTPAUSEDIF")
                    .WithFlag(27, out PROTIMER_seqListenBeforeTalkRetryInterrupt, name: "LBTRETRYSEQIF")
                    .WithFlag(28, out PROTIMER_seqRtccSynchedInterrupt, name: "RTCCSYNCHEDSEQIF")
                    .WithFlag(29, out PROTIMER_seqListenBeforeTalkTimeoutCounterMatchInterrupt, name: "TOUT0MATCHLBTSEQIF")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)ProtocolTimerRegisters.SequencerInterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out PROTIMER_seqPreCounterOverflowInterruptEnable, name: "PRECNTOFSEQIEN")
                    .WithFlag(1, out PROTIMER_seqBaseCounterOverflowInterruptEnable, name: "BASECNTOFSEQIEN")
                    .WithFlag(2, out PROTIMER_seqWrapCounterOverflowInterruptEnable, name: "WRAPCNTOFSEQIEN")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, out PROTIMER_timeoutCounter[0].SeqUnderflowInterruptEnable, name: "TOUT0SEQIEN")
                    .WithFlag(5, out PROTIMER_timeoutCounter[1].SeqUnderflowInterruptEnable, name: "TOUT1SEQIEN")
                    .WithFlag(6, out PROTIMER_timeoutCounter[0].SeqMatchInterruptEnable, name: "TOUT0MATCHSEQIEN")
                    .WithFlag(7, out PROTIMER_timeoutCounter[1].SeqMatchInterruptEnable, name: "TOUT1MATCHSEQIEN")
                    .WithFlag(8, out PROTIMER_captureCompareChannel[0].SeqInterruptEnable, name: "CC0SEQIEN")
                    .WithFlag(9, out PROTIMER_captureCompareChannel[1].SeqInterruptEnable, name: "CC1SEQIEN")
                    .WithFlag(10, out PROTIMER_captureCompareChannel[2].SeqInterruptEnable, name: "CC2SEQIEN")
                    .WithFlag(11, out PROTIMER_captureCompareChannel[3].SeqInterruptEnable, name: "CC3SEQIEN")
                    .WithFlag(12, out PROTIMER_captureCompareChannel[4].SeqInterruptEnable, name: "CC4SEQIEN")
                    .WithFlag(13, out PROTIMER_captureCompareChannel[5].SeqInterruptEnable, name: "CC5SEQIEN")
                    .WithFlag(14, out PROTIMER_captureCompareChannel[6].SeqInterruptEnable, name: "CC6SEQIEN")
                    .WithFlag(15, out PROTIMER_captureCompareChannel[7].SeqInterruptEnable, name: "CC7SEQIEN")
                    .WithFlag(16, out PROTIMER_captureCompareChannel[0].SeqOverflowInterruptEnable, name: "COF0SEQIEN")
                    .WithFlag(17, out PROTIMER_captureCompareChannel[1].SeqOverflowInterruptEnable, name: "COF1SEQIEN")
                    .WithFlag(18, out PROTIMER_captureCompareChannel[2].SeqOverflowInterruptEnable, name: "COF2SEQIEN")
                    .WithFlag(19, out PROTIMER_captureCompareChannel[3].SeqOverflowInterruptEnable, name: "COF3SEQIEN")
                    .WithFlag(20, out PROTIMER_captureCompareChannel[4].SeqOverflowInterruptEnable, name: "COF4SEQIEN")
                    .WithFlag(21, out PROTIMER_captureCompareChannel[5].SeqOverflowInterruptEnable, name: "COF5SEQIEN")
                    .WithFlag(22, out PROTIMER_captureCompareChannel[6].SeqOverflowInterruptEnable, name: "COF6SEQIEN")
                    .WithFlag(23, out PROTIMER_captureCompareChannel[7].SeqOverflowInterruptEnable, name: "COF7SEQIEN")
                    .WithFlag(24, out PROTIMER_seqListenBeforeTalkSuccessInterruptEnable, name: "LBTSUCCESSSEQIEN")
                    .WithFlag(25, out PROTIMER_seqListenBeforeTalkFailureInterruptEnable, name: "LBTFAILURESEQIEN")
                    .WithFlag(26, name: "LBTPAUSEDIEN")
                    .WithFlag(27, out PROTIMER_seqListenBeforeTalkRetryInterruptEnable, name: "LBTRETRYSEQIEN")
                    .WithFlag(28, out PROTIMER_seqRtccSynchedInterruptEnable, name: "RTCCSYNCHEDSEQIEN")
                    .WithFlag(29, out PROTIMER_seqListenBeforeTalkTimeoutCounterMatchInterruptEnable, name: "TOUT0MATCHLBTSEQIEN")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)ProtocolTimerRegisters.RxControl, new DoubleWordRegister(this)
                    .WithEnumField<DoubleWordRegister, PROTIMER_Event>(0, 5, out PROTIMER_rxSetEvent1, name: "RXSETEVENT1")
                    .WithReservedBits(5, 3)
                    .WithEnumField<DoubleWordRegister, PROTIMER_Event>(8, 5, out PROTIMER_rxSetEvent2, name: "RXSETEVENT2")
                    .WithReservedBits(13, 3)
                    .WithEnumField<DoubleWordRegister, PROTIMER_Event>(16, 5, out PROTIMER_rxClearEvent1, name: "RXCLREVENT1")
                    .WithReservedBits(21, 3)
                    .WithEnumField<DoubleWordRegister, PROTIMER_Event>(24, 5, out PROTIMER_rxClearEvent2, name: "RXCLREVENT2")
                    .WithReservedBits(29, 3)
                    .WithChangeCallback((_, __) => PROTIMER_UpdateRxRequestState())
                },
                {(long)ProtocolTimerRegisters.TxControl, new DoubleWordRegister(this)
                    .WithEnumField<DoubleWordRegister, PROTIMER_Event>(0, 5, out PROTIMER_txSetEvent1, name: "TXSETEVENT1")
                    .WithReservedBits(5, 3)
                    .WithEnumField<DoubleWordRegister, PROTIMER_Event>(8, 5, out PROTIMER_txSetEvent2, name: "TXSETEVENT2")
                    .WithReservedBits(13, 19)
                    .WithChangeCallback((_, __) => PROTIMER_UpdateTxRequestState())
                },
                {(long)ProtocolTimerRegisters.ListenBeforeTalkWaitControl, new DoubleWordRegister(this)
                    .WithValueField(0, 4, out PROTIMER_listenBeforeTalkStartExponent, name: "STARTEXP")
                    .WithValueField(4, 4, out PROTIMER_listenBeforeTalkMaxExponent, name: "MAXEXP")
                    .WithValueField(8, 5, out PROTIMER_ccaDelay, name: "CCADELAY")
                    .WithReservedBits(13, 3)
                    .WithValueField(16, 4, out PROTIMER_ccaRepeat, name: "CCAREPEAT")
                    .WithFlag(20, out PROTIMER_fixedBackoff, name: "FIXEDBACKOFF")
                    .WithReservedBits(21, 3)
                    .WithValueField(24, 4, out PROTIMER_retryLimit, name: "RETRYLIMIT")
                    .WithReservedBits(28, 4)
                },
                {(long)ProtocolTimerRegisters.ListenBeforeTalkState, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "TOUT0PCNT")
                    .WithValueField(16, 16, name: "TOUT0CNT")
                },
                {(long)ProtocolTimerRegisters.PseudoRandomGeneratorValue, new DoubleWordRegister(this)
                    .WithValueField(0, 16,
                        // When FIXEDBACKOFF is set, hardware freezes the RANDOM register: it neither
                        // regenerates on read nor accepts updates. Outside fixed backoff it behaves as an
                        // LFSR (fresh value each read).
                        valueProviderCallback: _ => { return PROTIMER_fixedBackoff.Value ? (ushort)PROTIMER_fixedBackoffRandomValue : (ushort)random.Next(); },
                        writeCallback: (_, value) => { if(!PROTIMER_fixedBackoff.Value) { PROTIMER_fixedBackoffRandomValue = (uint)value; } },
                        name: "RANDOM")
                    .WithReservedBits(16, 16)
                },
                {(long)ProtocolTimerRegisters.ListenBeforeTalkState1, new DoubleWordRegister(this)
                    .WithValueField(0, 4, out PROTIMER_ccaCounter, name: "CCACNT")
                    .WithValueField(4, 4, out PROTIMER_listenBeforeTalkExponent, name: "EXP")
                    .WithValueField(8, 4, out PROTIMER_listenBeforeTalkRetryCounter, name: "RETRYCNT")
                    .WithReservedBits(12, 20)
                },
                {(long)ProtocolTimerRegisters.PrsControl, new DoubleWordRegister(this)
                    .WithReservedBits(0, 1)
                    .WithFlag(1, name: "STARTPRSEN")
                    .WithValueField(2, 2, name: "STARTEDGE")
                    .WithReservedBits(4, 5)
                    .WithFlag(9, name: "STOPPRSEN")
                    .WithValueField(10, 2, name: "STOPEDGE")
                    .WithReservedBits(12, 5)
                    .WithFlag(17, out PROTIMER_rtccTriggerFromPrsEnable, name: "RTCCTRIGGERPRSEN")
                    .WithValueField(18, 2, name: "RTCCTRIGGEREDGE")
                    .WithReservedBits(20, 12)
                },
                {(long)ProtocolTimerRegisters.ListenBeforeTalkPrsControl, new DoubleWordRegister(this)
                    .WithReservedBits(0, 8)
                    .WithFlag(8, out PROTIMER_listenBeforeTalkPrsStartEnable, name: "LBTSTARTPRSEN")
                    .WithReservedBits(9, 7)
                    .WithFlag(16, out PROTIMER_listenBeforeTalkPrsPauseEnable, name: "LBTPAUSEPRSEN")
                    .WithReservedBits(17, 7)
                    .WithFlag(24, out PROTIMER_listenBeforeTalkPrsStopEnable, name: "LBTSTOPPRSEN")
                    .WithReservedBits(25, 7)
                },
            };

            var startOffset = (long)ProtocolTimerRegisters.CaptureCompareChannel0Control;
            var controlOffset = (long)ProtocolTimerRegisters.CaptureCompareChannel0Control - startOffset;
            var preOffset = (long)ProtocolTimerRegisters.CaptureCompareChannel0PreValue - startOffset;
            var baseOffset = (long)ProtocolTimerRegisters.CaptureCompareChannel0BaseValue - startOffset;
            var wrapOffset = (long)ProtocolTimerRegisters.CaptureCompareChannel0WrapValue - startOffset;
            var blockSize = (long)ProtocolTimerRegisters.CaptureCompareChannel1Control - (long)ProtocolTimerRegisters.CaptureCompareChannel0Control;
            for(var index = 0; index < PROTIMER_NumberOfCaptureCompareChannels; index++)
            {
                var i = index;
                // CaptureCompareChannel_n_Control
                registerDictionary.Add(startOffset + blockSize * i + controlOffset,
                    new DoubleWordRegister(this)
                        .WithFlag(0, out PROTIMER_captureCompareChannel[i].Enable, name: "ENABLE")
                        .WithEnumField<DoubleWordRegister, PROTIMER_CaptureCompareMode>(1, 1, out PROTIMER_captureCompareChannel[i].Mode, name: "CCMODE")
                        .WithFlag(2, out PROTIMER_captureCompareChannel[i].PreMatchEnable, name: "PREMATCHEN")
                        .WithFlag(3, out PROTIMER_captureCompareChannel[i].BaseMatchEnable, name: "BASEMATCHEN")
                        .WithFlag(4, out PROTIMER_captureCompareChannel[i].WrapMatchEnable, name: "WRAPMATCHEN")
                        .WithFlag(5, name: "OIST")
                        .WithFlag(6, name: "OUTINV")
                        .WithReservedBits(7, 1)
                        .WithValueField(8, 2, name: "MOA")
                        .WithValueField(10, 2, name: "OFOA")
                        .WithValueField(12, 2, name: "OFSEL")
                        .WithFlag(14, name: "PRSCONF")
                        .WithReservedBits(15, 6)
                        .WithEnumField<DoubleWordRegister, PROTIMER_CaptureInputSource>(21, 4, out PROTIMER_captureCompareChannel[i].CaptureInputSource, name: "INSEL")
                        .WithValueField(25, 2, name: "ICEDGE")
                        .WithReservedBits(27, 5)
                        .WithWriteCallback((_, __) => PROTIMER_UpdateCompareTimer(i))
                );
                // CaptureCompareChannel_n_Pre
                registerDictionary.Add(startOffset + blockSize * i + preOffset,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 16, out PROTIMER_captureCompareChannel[i].PreValue,
                            writeCallback: (_, __) =>
                            {
                                PROTIMER_captureCompareChannel[i].CaptureValid.Value = false;
                                PROTIMER_UpdateCompareTimer(i);
                            },
                            valueProviderCallback: _ =>
                            {
                                PROTIMER_captureCompareChannel[i].CaptureValid.Value = false;
                                return PROTIMER_captureCompareChannel[i].PreValue.Value;
                            }, name: "PRE")
                        .WithReservedBits(16, 16)
                );
                // CaptureCompareChannel_n_Base
                registerDictionary.Add(startOffset + blockSize * i + baseOffset,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 16, out PROTIMER_captureCompareChannel[i].BaseValue,
                            writeCallback: (_, __) => { PROTIMER_captureCompareChannel[i].CaptureValid.Value = false; },
                            valueProviderCallback: _ =>
                            {
                                PROTIMER_captureCompareChannel[i].CaptureValid.Value = false;
                                return PROTIMER_captureCompareChannel[i].BaseValue.Value;
                            }, name: "BASE")
                        .WithReservedBits(16, 16)
                );
                // CaptureCompareChannel_n_Wrap
                registerDictionary.Add(startOffset + blockSize * i + wrapOffset,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 32, out PROTIMER_captureCompareChannel[i].WrapValue,
                            writeCallback: (_, __) => { PROTIMER_captureCompareChannel[i].CaptureValid.Value = false; },
                            valueProviderCallback: _ =>
                            {
                                PROTIMER_captureCompareChannel[i].CaptureValid.Value = false;
                                return PROTIMER_captureCompareChannel[i].WrapValue.Value;
                            }, name: "WRAP")
                );
            }

            return new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private DoubleWordRegisterCollection BuildRadioControllerRegistersCollection()
        {
            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            {
                {(long)RadioControllerRegisters.IpVersion, new DoubleWordRegister(this, 0x00000003)
                    .WithValueField(0, 32, FieldMode.Read, name: "IPVERSION")
                },
                {(long)RadioControllerRegisters.Enable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "EN")
                    .WithReservedBits(1, 31)
                },
                {(long)RadioControllerRegisters.RXENSourceEnable, new DoubleWordRegister(this)
                    .WithValueField(0, 8, out RAC_softwareRxEnable, name: "SWRXEN")
                    .WithFlag(8, name: "CHANNELBUSYEN")
                    .WithFlag(9, name: "TIMDETEN")
                    .WithFlag(10, name: "PREDETEN")
                    .WithFlag(11, name: "FRAMEDETEN")
                    .WithFlag(12, name: "DEMODRXREQEN")
                    .WithFlag(13, name: "PRSRXEN")
                    .WithReservedBits(14, 18)
                    .WithWriteCallback((_, __) => RAC_UpdateRadioStateMachine())
                },
                {(long)RadioControllerRegisters.Status, new DoubleWordRegister(this)
                    .WithValueField(0, 16, FieldMode.Read, valueProviderCallback: _ => RAC_RxEnableMask, name: "RXMASK")
                    .WithReservedBits(16, 3)
                    .WithFlag(19, out RAC_forceStateActive, FieldMode.Read, name: "FORCESTATEACTIVE")
                    .WithFlag(20, out RAC_txAfterFramePending, FieldMode.Read, name: "TXAFTERFRAMEPEND")
                    .WithFlag(21, out RAC_txAfterFrameActive, FieldMode.Read, name: "TXAFTERFRAMEACTIVE")
                    .WithFlag(22, out RAC_sequencerInSleeping, FieldMode.Read, name: "SEQSLEEPING")
                    .WithFlag(23, out RAC_sequencerInDeepSleep, FieldMode.Read, name: "SEQSLEEPDEEP")
                    .WithEnumField<DoubleWordRegister, RAC_RadioState>(24, 4, FieldMode.Read, valueProviderCallback: _ => RAC_currentRadioState, name: "STATE")
                    .WithFlag(28, out RAC_sequencerActive, FieldMode.Read, name: "SEQACTIVE")
                    .WithFlag(29, name: "DEMODENS")
                    .WithFlag(30, FieldMode.Read, valueProviderCallback: _ => RAC_TxEnable, name: "TXENS")
                    .WithFlag(31, FieldMode.Read, valueProviderCallback: _ => RAC_RxEnable, name: "RXENS")
                },
                {(long)RadioControllerRegisters.Status1, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ => RAC_TxEnableMask, name: "TXMASK")
                    .WithReservedBits(8, 24)
                },
                {(long)RadioControllerRegisters.Status2, new DoubleWordRegister(this)
                    .WithEnumField<DoubleWordRegister, RAC_RadioState>(0, 4, FieldMode.Read, valueProviderCallback: _ => RAC_previous1RadioState, name: "PREVSTATE1")
                    .WithEnumField<DoubleWordRegister, RAC_RadioState>(4, 4, FieldMode.Read, valueProviderCallback: _ => RAC_previous2RadioState, name: "PREVSTATE2")
                    .WithEnumField<DoubleWordRegister, RAC_RadioState>(8, 4, FieldMode.Read, valueProviderCallback: _ => RAC_previous3RadioState, name: "PREVSTATE3")
                    .WithEnumField<DoubleWordRegister, RAC_RadioState>(12, 4, FieldMode.Read, valueProviderCallback: _ => RAC_currentRadioState, name: "CURRSTATE")
                    .WithReservedBits(16, 16)
                },
                {(long)RadioControllerRegisters.Control, new DoubleWordRegister(this)
                    .WithFlag(0, out RAC_forceDisable, writeCallback: (_, value) => { if (value) {RAC_TxEnable = false; RAC_UpdateRadioStateMachine();} }, name: "FORCEDISABLE")
                    .WithFlag(1, name: "PRSTXEN")
                    .WithFlag(2, out RAC_txAfterRx, name: "TXAFTERRX")
                    .WithFlag(3, name: "PRSMODE")
                    .WithReservedBits(4, 1)
                    .WithFlag(5, name: "PRSCLR")
                    .WithFlag(6, name: "TXPOSTPONE")
                    .WithFlag(7, name: "ACTIVEPOL")
                    .WithFlag(8, name: "PAENPOL")
                    .WithFlag(9, name: "LNAENPOL")
                    .WithFlag(10, name: "PRSRXDIS")
                    .WithReservedBits(11, 5)
                    .WithFlag(16, out RAC_prsForceTx, name: "PRSFORCETX")
                    .WithReservedBits(17, 7)
                    .WithFlag(24, name: "SEQRESET")
                    .WithFlag(25, out RAC_exitShutdownDisable, FieldMode.Read, name: "EXITSHUTDOWNDIS")
                    .WithFlag(26, FieldMode.Set, writeCallback: (_, value) => { if(value) { RAC_ReleaseSequencer(); } }, name: "CPUWAITDIS")
                    .WithFlag(27, name: "SEQCLKDIS")
                    .WithFlag(28, name: "RXOFDIS")
                    .WithReservedBits(29, 3)
                    .WithChangeCallback((_, __) => RAC_UpdateRadioStateMachine())
                },
                {(long)RadioControllerRegisters.Command, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Set, writeCallback: (_, value) => { if (value) {RAC_TxEnable = true;} }, name: "TXEN")
                    .WithFlag(1, FieldMode.Set, writeCallback: (_, value) => { if (value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.ForceTx);} }, name: "FORCETX")
                    .WithFlag(2, name: "TXONCCA")
                    .WithFlag(3, FieldMode.Set, writeCallback: (_, value) => { if (value) {RAC_TxEnable = false;} }, name: "CLEARTXEN")
                    .WithFlag(4, FieldMode.Set, writeCallback: (_, value) => { if (value) {RAC_HandleTxAfterFrameCommand();} }, name: "TXAFTERFRAME")
                    .WithFlag(5, FieldMode.Set, writeCallback: (_, value) => { if(value) {RAC_TxEnable = false; RAC_txAfterFramePending.Value = false; RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.TxDisable);} }, name: "TXDIS")
                    .WithFlag(6, FieldMode.Set, writeCallback: (_, value) => { if (value && !RAC_seqStateRxOverflowInterrupt.Value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.ClearRxOverflow);} }, name: "CLEARRXOVERFLOW")
                    .WithFlag(7, FieldMode.Set, writeCallback: (_, value) => { if (value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxCalibration);} }, name: "RXCAL")
                    .WithFlag(8, FieldMode.Set, writeCallback: (_, value) => { if (value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxDisable);} }, name: "RXDIS")
                    .WithReservedBits(9, 1)
                    .WithFlag(10, name: "FRCWR")
                    .WithFlag(11, name: "FRCRD")
                    .WithFlag(12, name: "PAENSET")
                    .WithFlag(13, name: "PAENCLEAR")
                    .WithFlag(14, name: "LNAENSET")
                    .WithFlag(15, name: "LNAENCLEAR")
                    .WithReservedBits(16, 14)
                    .WithFlag(30, name: "DEMODENSET")
                    .WithFlag(31, name: "DEMODENCLEAR")
                },
                {(long)RadioControllerRegisters.ForceStateTransition, new DoubleWordRegister(this)
                    .WithEnumField<DoubleWordRegister, RAC_RadioState>(0, 4, out RAC_forceStateTransition, writeCallback: (_, __) =>
                        {
                            RAC_forceStateActive.Value = true;
                        }, name: "FORCESTATE")
                    .WithReservedBits(4, 28)
                    .WithChangeCallback((_, __) => RAC_UpdateRadioStateMachine())
                },
                {(long)RadioControllerRegisters.Em1pControlAndStatus, new DoubleWordRegister(this)
                    .WithFlag(0, out RAC_em1pRadioSwControl, name: "RADIOEM1PMODE")
                    .WithFlag(1, out RAC_em1pRadioDisableSwRequest, name: "RADIOEM1PDISSWREQ")
                    .WithReservedBits(2, 2)
                    .WithFlag(4, out RAC_em1pMcuSwControl, name: "MCUEM1PMODE")
                    .WithFlag(5, out RAC_em1pMcuDisableSwRequest, name: "MCUEM1PDISSWREQ")
                    .WithReservedBits(6, 10)
                    .WithFlag(16, FieldMode.Read, valueProviderCallback: _ => RAC_Em1pRequest, name: "RADIOEM1PREQ")
                    // EMU power-up is instantaneous, so the 4-phase handshake ACK follows the request.
                    .WithFlag(17, FieldMode.Read, valueProviderCallback: _ => RAC_Em1pRequest, name: "RADIOEM1PACK")
                    .WithFlag(18, FieldMode.Read, valueProviderCallback: _ => RAC_Em1pHardwareRequest, name: "RADIOEM1PHWREQ")
                    .WithReservedBits(19, 13)
                    .WithChangeCallback((_, __) => RAC_UpdateEm1pRequest())
                },
                {(long)RadioControllerRegisters.InterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out RAC_radioStateChangeInterrupt, name: "STATECHANGEIF")
                    .WithFlag(1, out RAC_stimerCompareEventInterrupt, name: "STIMCMPEVIF")
                    .WithFlag(2, name: "SEQLOCKUPIF")
                    .WithFlag(3, name: "SEQRESETREQIF")
                    .WithReservedBits(4, 12)
                    .WithValueField(16, 8, out RAC_mainCoreSeqInterrupts, name: "SEQIF")
                    .WithReservedBits(24, 8)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RadioControllerRegisters.InterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out RAC_radioStateChangeInterruptEnable, name: "STATECHANGEIEN")
                    .WithFlag(1, out RAC_stimerCompareEventInterruptEnable, name: "STIMCMPEVIEN")
                    .WithFlag(2, name: "SEQLOCKUPIEN")
                    .WithFlag(3, name: "SEQRESETREQIEN")
                    .WithReservedBits(4, 12)
                    .WithValueField(16, 8, out RAC_mainCoreSeqInterruptsEnable, name: "SEQIEN")
                    .WithReservedBits(24, 8)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RadioControllerRegisters.SequencerInterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out RAC_seqRadioStateChangeInterrupt, name: "STATECHANGESEQIF")
                    .WithFlag(1, out RAC_seqStimerCompareEventInterrupt, name: "STIMCMPEVSEQIF")
                    .WithFlag(2, out RAC_seqDemodRxRequestClearInterrupt, name: "DEMODRXREQCLRSEQIF")
                    .WithFlag(3, out RAC_seqPrsEventInterrupt, name: "PRSEVENTSEQIF")
                    .WithReservedBits(4, 12)
                    .WithFlag(16, out RAC_seqStateOffInterrupt, name: "STATEOFFIF")
                    .WithFlag(17, out RAC_seqStateRxWarmInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxWarmIrqCleared); } }, name: "STATERXWARMIF")
                    .WithFlag(18, out RAC_seqStateRxSearchInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxSearchIrqCleared); } }, name: "STATERXSEARCHIF")
                    .WithFlag(19, out RAC_seqStateRxFrameInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxFrameIrqCleared); } }, name: "STATERXFRAMEIF")
                    .WithFlag(20, out RAC_seqStateRxPoweringDownInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxPowerDownIrqCleared); } }, name: "STATERXPDIF")
                    .WithFlag(21, out RAC_seqStateRx2RxInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.Rx2RxIrqCleared); } }, name: "STATERX2RXIF")
                    .WithFlag(22, out RAC_seqStateRxOverflowInterrupt, name: "STATERXOVERFLOWIF")
                    .WithFlag(23, out RAC_seqStateRx2TxInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.Rx2TxIrqCleared); } }, name: "STATERX2TXIF")
                    .WithFlag(24, out RAC_seqStateTxWarmInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.TxWarmIrqCleared);} }, name: "STATETXWARMIF")
                    .WithFlag(25, out RAC_seqStateTxInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.TxIrqCleared); } }, name: "STATETXIF")
                    .WithFlag(26, out RAC_seqStateTxPoweringDownInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.TxPowerDownIrqCleared); } }, name: "STATETXPDIF")
                    .WithFlag(27, out RAC_seqStateTx2RxInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.Tx2RxIrqCleared); } }, name: "STATETX2RXIF")
                    .WithFlag(28, out RAC_seqStateTx2TxInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.Tx2TxIrqCleared); } }, name: "STATETX2TXIF")
                    .WithFlag(29, out RAC_seqStateShutDownInterrupt, changeCallback: (_, value) => { if (!value) {RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.ShutdownIrqCleared); } }, name: "STATESHUTDOWNIF")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RadioControllerRegisters.SequencerInterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out RAC_seqRadioStateChangeInterruptEnable, name: "STATECHANGESEQIEN")
                    .WithFlag(1, out RAC_seqStimerCompareEventInterruptEnable, name: "STIMCMPEVSEQIEN")
                    .WithFlag(2, out RAC_seqDemodRxRequestClearInterruptEnable, name: "DEMODRXREQCLRSEQIEN")
                    .WithFlag(3, out RAC_seqPrsEventInterruptEnable, name: "PRSEVENTSEQIEN")
                    .WithReservedBits(4, 12)
                    .WithFlag(16, out RAC_seqStateOffInterruptEnable, name: "STATEOFFIEN")
                    .WithFlag(17, out RAC_seqStateRxWarmInterruptEnable, name: "STATERXWARMIEN")
                    .WithFlag(18, out RAC_seqStateRxSearchInterruptEnable, name: "STATERXSEARCHIEN")
                    .WithFlag(19, out RAC_seqStateRxFrameInterruptEnable, name: "STATERXFRAMEIEN")
                    .WithFlag(20, out RAC_seqStateRxPoweringDownInterruptEnable, name: "STATERXPDIEN")
                    .WithFlag(21, out RAC_seqStateRx2RxInterruptEnable, name: "STATERX2RXIEN")
                    .WithFlag(22, out RAC_seqStateRxOverflowInterruptEnable, name: "STATERXOVERFLOWIEN")
                    .WithFlag(23, out RAC_seqStateRx2TxInterruptEnable, name: "STATERX2TXIEN")
                    .WithFlag(24, out RAC_seqStateTxWarmInterruptEnable, name: "STATETXWARMIEN")
                    .WithFlag(25, out RAC_seqStateTxInterruptEnable, name: "STATETXIEN")
                    .WithFlag(26, out RAC_seqStateTxPoweringDownInterruptEnable, name: "STATETXPDIEN")
                    .WithFlag(27, out RAC_seqStateTx2RxInterruptEnable, name: "STATETX2RXIEN")
                    .WithFlag(28, out RAC_seqStateTx2TxInterruptEnable, name: "STATETX2TXIEN")
                    .WithFlag(29, out RAC_seqStateShutDownInterruptEnable, name: "STATESHUTDOWNIEN")
                    .WithReservedBits(30, 2)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RadioControllerRegisters.SequencerControl, new DoubleWordRegister(this)
                    .WithEnumField<DoubleWordRegister, RAC_SeqTimerCompareAction>(0, 1, out RAC_seqTimerCompareAction, name: "COMPACT")
                    .WithEnumField<DoubleWordRegister, RAC_SeqTimerCompareInvalidMode>(1, 2, out RAC_seqTimerCompareInvalidMode, name: "COMPINVALMODE")
                    .WithFlag(3, out RAC_seqTimerCompareRelative, name: "RELATIVE")
                    .WithFlag(4, out RAC_seqTimerAlwaysRun, name: "STIMERALWAYSRUN")
                    .WithFlag(5, name: "STIMERDEBUGRUN")
                    .WithFlag(6, name: "STATEDEBUGRUN")
                    .WithReservedBits(7, 17)
                    .WithValueField(24, 2, out RAC_softwareIrq, writeCallback: (_, __) => UpdateInterrupts(), name: "SWIRQ")
                    .WithReservedBits(26, 6)
                },
                {(long)RadioControllerRegisters.SequencerTimerValue, new DoubleWordRegister(this)
                    .WithValueField(0, 16, FieldMode.Read, valueProviderCallback: _ => RAC_SeqTimerValue, name: "STIMER")
                    .WithReservedBits(16, 16)
                },
                {(long)RadioControllerRegisters.SequencerTimerCompareValue, new DoubleWordRegister(this)
                    .WithValueField(0, 15, out RAC_seqTimerCompareValue, writeCallback: (_, value) => RAC_SeqTimerLimit = (ushort)value, name: "STIMERCOMP")
                    .WithReservedBits(16, 16)
                },
                {(long)RadioControllerRegisters.SequencerPrescaler, new DoubleWordRegister(this, 0x7)
                    .WithValueField(0, 7, out RAC_seqTimerPrescaler, name: "STIMERPRESC")
                    .WithReservedBits(7, 25)
                },
                {(long)RadioControllerRegisters.Storage0, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_seqStorage[0], name: "SR0")
                },
                {(long)RadioControllerRegisters.Storage1, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_seqStorage[1], name: "SR1")
                },
                {(long)RadioControllerRegisters.Storage2, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_seqStorage[2], name: "SR2")
                },
                {(long)RadioControllerRegisters.Storage3, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_seqStorage[3], name: "SR3")
                },
                {(long)RadioControllerRegisters.RadioFrequencyStatus, new DoubleWordRegister(this)
                    .WithFlag(0, out RAC_paRampingDone, FieldMode.Read, name: "MODRAMPUPDONE")
                    .WithReservedBits(1, 31)
                },
                {(long)RadioControllerRegisters.SynthesizerEnableControl, new DoubleWordRegister(this)
                    .WithReservedBits(0, 1)
                    .WithFlag(1, name: "VCOSTARTUP")
                    .WithReservedBits(2, 5)
                    .WithFlag(7, name: "VCBUFEN")
                    .WithReservedBits(8, 1)
                    .WithFlag(9, writeCallback: (oldValue, newValue) =>
                    {
                        // Rising edge marks the start of the synth-enable sequence, ahead of the LO-counter calibration.
                        if(newValue && !oldValue && RAC_currentRadioState == RAC_RadioState.RxWarm)
                        {
                            SYNTH_startTime = GetTime();
                            SYNTH_startValid = true;
                        }
                    }, name: "SYNTHSTARTREQ")
                    .WithFlag(10, name: "MMDPOWERBALANCEDISABLE")
                    .WithReservedBits(11, 9)
                    .WithFlag(20, name: "LPFBWSEL")
                    .WithReservedBits(21, 11)
                },
                {(long)RadioControllerRegisters.PowerAmplifierEnableControl, new DoubleWordRegister(this)
                    .WithReservedBits(0, 8)
                    .WithFlag(8, valueProviderCallback: _ => { return RAC_PaOutputLevelRamping; }, writeCallback: (_, value) => { RAC_PaOutputLevelRamping = value; }, name: "PARAMP")
                    .WithReservedBits(9, 7)
                    .WithFlag(16, name: "INVRAMPCLK")
                    .WithFlag(17, name: "DIV2RAMPCLK")
                    .WithFlag(18, name: "RSTDIV2RAMPCLK")
                    .WithReservedBits(19, 13)
                },
                {(long)RadioControllerRegisters.Scratch0, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_scratch[0], name: "SCRATCH0")
                },
                {(long)RadioControllerRegisters.Scratch1, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_scratch[1], name: "SCRATCH1")
                },
                {(long)RadioControllerRegisters.Scratch2, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_scratch[2], name: "SCRATCH2")
                },
                {(long)RadioControllerRegisters.Scratch3, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_scratch[3], name: "SCRATCH3")
                },
                {(long)RadioControllerRegisters.Scratch4, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_scratch[4], name: "SCRATCH4")
                },
                {(long)RadioControllerRegisters.Scratch5, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_scratch[5], name: "SCRATCH5")
                },
                {(long)RadioControllerRegisters.Scratch6, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_scratch[6], name: "SCRATCH6")
                },
                {(long)RadioControllerRegisters.Scratch7, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RAC_scratch[7], name: "SCRATCH7")
                },
                {(long)RadioControllerRegisters.RadioFrequencyLock0, new DoubleWordRegister(this, 0x80000000)
                    .WithValueField(0, 10, name: "SYNTHLODIVFREQCTRL")
                    .WithReservedBits(10, 6)
                    .WithFlag(16, name: "RACIFPGAEN")
                    .WithFlag(17, valueProviderCallback: _ => !RAC_0DbmSupport, writeCallback: (_, value) => {RAC_0DbmSupport = !value;}, name: "RACTX0DBM")
                    .WithFlag(18, valueProviderCallback: _ => !RAC_10DbmSupport, writeCallback: (_, value) => {RAC_10DbmSupport = !value;}, name: "RACTX10DBM")
                    .WithFlag(19, valueProviderCallback: _ => !RAC_20DbmSupport, writeCallback: (_, value) => {RAC_20DbmSupport = !value;}, name: "RACTX20DBM")
                    .WithReservedBits(20, 1)
                    .WithFlag(21, name: "MODEMHADM")
                    .WithFlag(22, name: "FRCCONVMODE")
                    .WithFlag(23, name: "FRCPAUSING")
                    .WithFlag(24, name: "MODEMANTSWENABLE")
                    .WithFlag(25, name: "MODEMLRBLE")
                    .WithFlag(26, name: "MODEMDSSS")
                    .WithFlag(27, name: "MODEMDEC1")
                    .WithFlag(28, name: "MODEMMODFORMAT")
                    .WithFlag(29, name: "MODEMDUALSYNC")
                    .WithFlag(30, name: "MODEMANTDIVMODE")
                    .WithFlag(31, out RAC_unlocked, name: "UNLOCKED")
                },
                {(long)RadioControllerRegisters.TestControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "MODEN")
                    .WithFlag(1, name: "DEMODEN")
                    .WithReservedBits(2, 30)
                },
                {(long)RadioControllerRegisters.SystemTickTimerControl, new DoubleWordRegister(this)
                    .WithValueField(0, 24, name: "STCAL")
                    .WithFlag(24, name: "STSKEW")
                    .WithReservedBits(25, 7)
                },
                {(long)RadioControllerRegisters.FrameControlWordBufferWrite, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "WDATA")
                    .WithReservedBits(8, 24)
                },
                {(long)RadioControllerRegisters.FrameControlWordBufferRead, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "RDATA")
                    .WithReservedBits(8, 24)
                },
                {(long)RadioControllerRegisters.SynthesizerRegulatorEnableControl, new DoubleWordRegister(this, 0x04000000)
                    .WithReservedBits(0, 10)
                    .WithValueField(10, 3, name: "MMDLDOVREFTRIM")
                    .WithReservedBits(13, 11)
                    .WithValueField(24, 3, name: "CHPLDOVREFTRIM")
                    .WithReservedBits(27, 5)
                },
                {(long)RadioControllerRegisters.VoltageControlledOscillatorControl, new DoubleWordRegister(this, 0x0000044C)
                    .WithValueField(0, 4, name: "VCOAMPLITUDE")
                    .WithValueField(4, 4, name: "VCODETAMPLITUDERX")
                    .WithValueField(8, 4, name: "VCODETAMPLITUDETX")
                    .WithReservedBits(12, 20)
                },
                {(long)RadioControllerRegisters.ChargePumpControl, new DoubleWordRegister(this)
                    .WithReservedBits(0, 14)
                    .WithFlag(14, name: "CHPOUTPUTTRISTATEMODE")
                    .WithReservedBits(15, 17)
                },
                {(long)RadioControllerRegisters.IntermediateFrequencyProgrammableGainAmplifierControl, new DoubleWordRegister(this)
                    .WithReservedBits(0, 19)
                    .WithFlag(19, name: "DCCALON")
                    .WithFlag(20, name: "DCRSTEN")
                    .WithFlag(21, name: "DCESTIEN")
                    .WithValueField(22, 3, name: "DCCALDEC0")
                    .WithValueField(25, 3, name: "DCCALDCGEAR")
                    .WithReservedBits(28, 4)
                },
                {(long)RadioControllerRegisters.AutomaticPowerControl, new DoubleWordRegister(this, 0xFF000000)
                    .WithReservedBits(0, 2)
                    .WithFlag(2, name: "ENAPCSW")
                    .WithReservedBits(3, 21)
                    .WithValueField(24, 8, name: "AMPCONTROLLIMITSW")
                },
                {(long)RadioControllerRegisters.AntennaDiversity, new DoubleWordRegister(this)
                    .WithFlag(0, name: "INTDIVLNAMIXEN0")
                    .WithReservedBits(1, 1)
                    .WithFlag(2, name: "INTDIVLNAMIXRFATTDCEN0")
                    .WithFlag(3, name: "INTDIVLNAMIXRFPKDENRF0")
                    .WithFlag(4, name: "INTDIVSYLODIVRLO02G4EN")
                    .WithFlag(5, name: "INTDIVLNAMIXEN1")
                    .WithReservedBits(6, 1)
                    .WithFlag(7, name: "INTDIVLNAMIXRFATTDCEN1")
                    .WithFlag(8, name: "INTDIVLNAMIXRFPKDENRF1")
                    .WithFlag(9, name: "INTDIVSYLODIVRLO12G4EN")
                    .WithValueField(10, 2, name: "ANTDIVSTATUS")
                    .WithReservedBits(12, 20)
                },
                {(long)RadioControllerRegisters.AuxiliaryAnalogToDigitalConverterTrim, new DoubleWordRegister(this, 0x06D55502)
                    .WithFlag(0, name: "AUXADCCLKINVERT")
                    .WithValueField(1, 2, name: "AUXADCLDOVREFTRIM")
                    .WithFlag(3, name: "AUXADCOUTPUTINVERT")
                    .WithValueField(4, 5, name: "AUXADCRCTUNE")
                    .WithValueField(9, 2, name: "AUXADCTRIMADCINPUTRES")
                    .WithValueField(11, 2, name: "AUXADCTRIMCURRINPUTBUF")
                    .WithValueField(13, 2, name: "AUXADCTRIMCURROPA1")
                    .WithValueField(15, 2, name: "AUXADCTRIMCURROPA2")
                    .WithValueField(17, 2, name: "AUXADCTRIMCURRREFBUF")
                    .WithValueField(19, 2, name: "AUXADCTRIMCURRTSENSE")
                    .WithValueField(21, 2, name: "AUXADCTRIMCURRVCMBUF")
                    .WithFlag(23, name: "AUXADCTRIMLDOHIGHCURRENT")
                    .WithValueField(24, 2, name: "AUXADCTRIMREFP")
                    .WithValueField(26, 2, name: "AUXADCTRIMVREFVCM")
                    .WithFlag(28, name: "AUXADCTSENSETRIMVBE2")
                    .WithReservedBits(29, 3)
                },
                {(long)RadioControllerRegisters.AuxiliaryAnalogToDigitalConverterEnable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "AUXADCENAUXADC")
                    .WithFlag(1, name: "AUXADCENINPUTBUFFER")
                    .WithFlag(2, name: "AUXADCENLDO")
                    .WithFlag(3, name: "AUXADCENOUTPUTDRV")
                    .WithFlag(4, name: "AUXADCENPMON")
                    .WithFlag(5, name: "AUXADCENRESONDIAGA")
                    .WithFlag(6, name: "AUXADCENTSENSE")
                    .WithFlag(7, name: "AUXADCENTSENSECAL")
                    .WithFlag(8, name: "AUXADCINPUTBUFFERBYPASS")
                    .WithFlag(9, name: "AUXADCENMEASTHERMISTOR")
                    .WithReservedBits(10, 22)
                },
                {(long)RadioControllerRegisters.AuxiliaryAnalogToDigitalConverterControl0, new DoubleWordRegister(this, 0x00000100)
                    .WithValueField(0, 10, name: "CYCLES")
                    .WithValueField(10, 2, name: "MUXSEL")
                    .WithFlag(12, name: "CLRCOUNTER")
                    .WithFlag(13, name: "CLRFILTER")
                    .WithReservedBits(14, 18)
                },
                {(long)RadioControllerRegisters.AuxiliaryAnalogToDigitalConverterControl1, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "AUXADCINPUTRESSEL")
                    .WithValueField(4, 4, name: "AUXADCINPUTSELECT")
                    .WithValueField(8, 4, name: "AUXADCPMONSELECT")
                    .WithReservedBits(12, 4)
                    .WithValueField(16, 5, name: "AUXADCTSENSESELCURR")
                    .WithReservedBits(21, 3)
                    .WithFlag(24, name: "AUXADCRESET")
                    .WithFlag(25, name: "AUXADCTSENSESELVBE")
                    .WithReservedBits(26, 2)
                    .WithValueField(28, 4, name: "AUXADCTHERMISTORFREQSEL")
                },
                {(long)RadioControllerRegisters.AuxiliaryAnalogToDigitalConverterDigitalOutput, new DoubleWordRegister(this)
                    .WithValueField(0, 28, FieldMode.Read, name: "AUXADCOUT")
                    .WithReservedBits(28, 4)
                },
                {(long)RadioControllerRegisters.ClockMultEnable0, new DoubleWordRegister(this, 0xAA400005)
                    .WithValueField(0, 2, name: "CLKMULTBWCAL")
                    .WithFlag(2, name: "CLKMULTDISICO")
                    .WithFlag(3, name: "CLKMULTENBBDET")
                    .WithFlag(4, name: "CLKMULTENBBXLDET")
                    .WithFlag(5, name: "CLKMULTENBBXMDET")
                    .WithFlag(6, name: "CLKMULTENCFDET")
                    .WithFlag(7, name: "CLKMULTENDITHER")
                    .WithFlag(8, name: "CLKMULTENDRVADC")
                    .WithFlag(9, name: "CLKMULTENDRVN")
                    .WithFlag(10, name: "CLKMULTENDRVP")
                    .WithFlag(11, name: "CLKMULTENDRVRX2P4G")
                    .WithFlag(12, name: "CLKMULTENDRVRXSUBG")
                    .WithFlag(13, name: "CLKMULTENDRVTXDUALB")
                    .WithFlag(14, name: "CLKMULTENFBDIV")
                    .WithFlag(15, name: "CLKMULTENREFDIV")
                    .WithFlag(16, name: "CLKMULTENREG1")
                    .WithFlag(17, name: "CLKMULTENREG2")
                    .WithFlag(18, name: "CLKMULTENREG3")
                    .WithFlag(19, name: "CLKMULTENROTDET")
                    .WithFlag(20, name: "CLKMULTENBYPASS40MHZ")
                    .WithReservedBits(21, 1)
                    .WithValueField(22, 2, name: "CLKMULTFREQCAL")
                    .WithValueField(24, 2, name: "CLKMULTREG2ADJI")
                    .WithValueField(26, 2, name: "CLKMULTREG1ADJV")
                    .WithValueField(28, 2, name: "CLKMULTREG2ADJV")
                    .WithValueField(30, 2, name: "CLKMULTREG3ADJV")
                },
                {(long)RadioControllerRegisters.ClockMultEnable1, new DoubleWordRegister(this, 0x00000188)
                    .WithValueField(0, 4, name: "CLKMULTINNIBBLE")
                    .WithReservedBits(4, 1)
                    .WithFlag(5, name: "CLKMULTLDFNIB")
                    .WithFlag(6, name: "CLKMULTLDMNIB")
                    .WithValueField(7, 2, name: "CLKMULTRDNIBBLE")
                    .WithReservedBits(9, 1)
                    .WithFlag(10, name: "CLKMULTLDCNIB")
                    .WithValueField(11, 6, name: "CLKMULTDRVAMPSEL")
                    .WithReservedBits(17, 15)
                },
                {(long)RadioControllerRegisters.ClockMultControl, new DoubleWordRegister(this, 0x000000C0)
                    .WithValueField(0, 7, name: "CLKMULTDIVN")
                    .WithValueField(7, 3, name: "CLKMULTDIVR")
                    .WithValueField(10, 3, name: "CLKMULTDIVX")
                    .WithFlag(13, name: "CLKMULTENRESYNC")
                    .WithFlag(14, name: "CLKMULTVALID")
                    .WithReservedBits(15, 17)
                },
                {(long)RadioControllerRegisters.ClockMultStatus, new DoubleWordRegister(this)
                    .WithValueField(0, 4, FieldMode.Read, name: "CLKMULTOUTNIBBLE")
                    .WithFlag(4, FieldMode.Read, name: "CLKMULTACKVALID")
                    .WithReservedBits(5, 27)
                },
                {(long)RadioControllerRegisters.IntermediateFrequencyAnalogToDigitalConverterDebug, new DoubleWordRegister(this, 0x0000000E)
                    .WithFlag(0, name: "IFADCENADC")
                    .WithFlag(1, name: "IFADCENCLK")
                    .WithFlag(2, name: "IFADCENREFBUF")
                    .WithFlag(3, name: "IFADCENSIDETONE")
                    .WithFlag(4, name: "IFADCINVERTCLK")
                    .WithFlag(5, name: "IFADCSHORTINPUT")
                    .WithFlag(6, name: "IFADCDISABLECLK")
                    .WithReservedBits(7, 25)
                },
                {(long)RadioControllerRegisters.IntermediateFrequencyAnalogToDigitalConverterTrim0, new DoubleWordRegister(this, 0x11512C6C)
                    .WithFlag(0, name: "IFADCCLKSEL")
                    .WithReservedBits(1, 1)
                    .WithValueField(2, 3, name: "IFADCLDOSERIESAMPLVL")
                    .WithValueField(5, 3, name: "IFADCLDOSHUNTAMPLVL1")
                    .WithFlag(8, name: "IFADCLDOSHUNTAMPLVL2")
                    .WithValueField(9, 3, name: "IFADCLDOSHUNTCURLVL1")
                    .WithValueField(12, 3, name: "IFADCLDOSHUNTCURLVL2")
                    .WithValueField(15, 3, name: "IFADCOTACURRENT")
                    .WithValueField(18, 3, name: "IFADCREFBUFAMPLVL")
                    .WithValueField(21, 3, name: "IFADCREFBUFCURLVL")
                    .WithValueField(24, 3, name: "IFADCSIDETONEAMP")
                    .WithValueField(27, 3, name: "IFADCSIDETONEFREQ")
                    .WithFlag(30, name: "IFADCENHALFMODE")
                    .WithReservedBits(31, 1)
                },
                {(long)RadioControllerRegisters.IntermediateFrequencyAnalogToDigitalConverterTrim1, new DoubleWordRegister(this, 0x00000123)
                    .WithValueField(0, 3, name: "IFADCVCMLVL")
                    .WithFlag(3, name: "IFADCENNEGRES")
                    .WithValueField(4, 3, name: "IFADCNEGRESCURRENT")
                    .WithValueField(7, 2, name: "IFADCNEGRESVCM")
                    .WithReservedBits(9, 23)
                },
                {(long)RadioControllerRegisters.IntermediateFrequencyAnalogToDigitalConverterCalibration, new DoubleWordRegister(this, 0x00000C00)
                    .WithFlag(0, name: "IFADCENRCCAL")
                    .WithFlag(1, name: "IFADCTUNERCCALMODE")
                    .WithReservedBits(2, 6)
                    .WithValueField(8, 5, name: "IFADCTUNERC")
                    .WithReservedBits(13, 3)
                    .WithValueField(16, 8, name: "IFADCRCCALCOUNTERSTARTVAL")
                    .WithReservedBits(24, 8)
                },
                {(long)RadioControllerRegisters.IntermediateFrequencyAnalogToDigitalConverterStatus, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, name: "IFADCRCCALOUT")
                    .WithReservedBits(1, 31)
                },
                {(long)RadioControllerRegisters.LowNoiseAmplifierMixerDebug, new DoubleWordRegister(this)
                    .WithFlag(0, name: "LNAMIXDISMXR0")
                    .WithFlag(1, name: "LNAMIXDISMXR1")
                    .WithReservedBits(2, 30)
                },
                {(long)RadioControllerRegisters.LowNoiseAmplifierMixerTrim0, new DoubleWordRegister(this, 0x00000110)
                    .WithValueField(0, 3, name: "LNAMIXCAPSEL0")
                    .WithValueField(3, 2, name: "LNAMIXMXRBIAS0")
                    .WithValueField(5, 4, name: "LNAMIXVOUTADJ0")
                    .WithReservedBits(9, 23)
                },
                {(long)RadioControllerRegisters.LowNoiseAmplifierMixerTrim1, new DoubleWordRegister(this, 0x00000110)
                    .WithValueField(0, 3, name: "LNAMIXCAPSEL1")
                    .WithValueField(3, 2, name: "LNAMIXMXRBIAS1")
                    .WithValueField(5, 4, name: "LNAMIXVOUTADJ1")
                    .WithReservedBits(9, 23)
                },
                {(long)RadioControllerRegisters.LowNoiseAmplifierMixerTrim2, new DoubleWordRegister(this, 0x545033D0)
                    .WithReservedBits(0, 4)
                    .WithValueField(4, 6, name: "LNAMIXCURCTRL")
                    .WithValueField(10, 2, name: "LNAMIXHIGHCUR")
                    .WithValueField(12, 4, name: "LNAMIXLOWCUR")
                    .WithReservedBits(16, 3)
                    .WithValueField(19, 2, name: "LNAMIXNCASADJ0")
                    .WithValueField(21, 2, name: "LNAMIXPCASADJ0")
                    .WithValueField(23, 4, name: "LNAMIXTRIMVREG")
                    .WithValueField(27, 2, name: "LNAMIXNCASADJ1")
                    .WithValueField(29, 2, name: "LNAMIXPCASADJ1")
                    .WithReservedBits(31, 1)
                },
                {(long)RadioControllerRegisters.LowNoiseAmplifierMixerTrim3, new DoubleWordRegister(this, 0x00000208)
                    .WithValueField(0, 6, name: "LNAMIXIBIASADJ0")
                    .WithValueField(6, 6, name: "LNAMIXIBIASADJ1")
                    .WithReservedBits(12, 20)
                },
                {(long)RadioControllerRegisters.LowNoiseAmplifierMixerTrim4, new DoubleWordRegister(this, 0x88082002)
                    .WithValueField(0, 2, name: "LNAMIXRFPKDBWSEL")
                    .WithReservedBits(2, 6)
                    .WithValueField(8, 6, name: "LNAMIXRFPKDCALCMLO")
                    .WithValueField(14, 6, name: "LNAMIXRFPKDCALCMHI")
                    .WithReservedBits(20, 4)
                    .WithValueField(24, 4, name: "LNAMIXRFPKDTHRESHSELLO")
                    .WithValueField(28, 4, name: "LNAMIXRFPKDTHRESHSELHI")
                },
                {(long)RadioControllerRegisters.LowNoiseAmplifierMixerCalibration, new DoubleWordRegister(this, 0x000007E0)
                    .WithFlag(0, name: "LNAMIXCALEN")
                    .WithReservedBits(1, 1)
                    .WithFlag(2, name: "LNAMIXCALVMODE")
                    .WithFlag(3, name: "LNAMIXENIRCAL0")
                    .WithFlag(4, name: "LNAMIXENIRCAL1")
                    .WithValueField(5, 3, name: "LNAMIXIRCALAMP0")
                    .WithValueField(8, 3, name: "LNAMIXIRCALAMP1")
                    .WithReservedBits(11, 21)
                },
                {(long)RadioControllerRegisters.LowNoiseAmplifierMixerEnable, new DoubleWordRegister(this)
                    .WithReservedBits(0, 3)
                    .WithFlag(3, name: "LNAMIXENLDO")
                    .WithReservedBits(4, 28)
                },
                {(long)RadioControllerRegisters.PreCounterOverflow, new DoubleWordRegister(this, 0x00000026)
                    .WithFlag(0, name: "PREBYPFORCE")
                    .WithValueField(1, 3, name: "PREREGTRIM")
                    .WithValueField(4, 2, name: "PREVREFTRIM")
                    .WithReservedBits(6, 26)
                },
                {(long)RadioControllerRegisters.PowerAmplifierTrim0, new DoubleWordRegister(this, 0x00000077)
                    .WithValueField(0, 4, name: "TX0DBMTRIMBIASN")
                    .WithValueField(4, 4, name: "TX0DBMTRIMBIASP")
                    .WithValueField(8, 8, name: "TXPAAMPCTRL")
                    .WithFlag(16, name: "TXPABYPASSREG")
                    .WithReservedBits(17, 7)
                    .WithFlag(24, name: "ENAMPCTRLREG")
                    .WithReservedBits(25, 7)
                },
                {(long)RadioControllerRegisters.PowerAmplifierTrim1, new DoubleWordRegister(this, 0x03034373)
                    .WithValueField(0, 2, name: "TX0DBMTRIMPREDRVREGIBCORE")
                    .WithFlag(2, name: "TX0DBMTRIMPREDRVREGPSR")
                    .WithReservedBits(3, 1)
                    .WithValueField(4, 4, name: "TX0DBMTRIMPREDRVREGIBNDIO")
                    .WithValueField(8, 2, name: "TX0DBMTRIMPREDRVSLOPE")
                    .WithReservedBits(10, 2)
                    .WithValueField(12, 4, name: "TX0DBMTRIMREGFB")
                    .WithValueField(16, 3, name: "TX0DBMTRIMREGVREF")
                    .WithReservedBits(19, 1)
                    .WithValueField(20, 3, name: "TX0DBMTRIMTAPCAP100F")
                    .WithReservedBits(23, 1)
                    .WithValueField(24, 3, name: "TX0DBMTRIMTAPCAP200F")
                    .WithReservedBits(27, 5)
                },
                {(long)RadioControllerRegisters.PowerAmplifierTrim2, new DoubleWordRegister(this)
                    .WithValueField(0, 3, name: "TX0DBMTRIMDUTYCYN")
                    .WithReservedBits(3, 1)
                    .WithValueField(4, 3, name: "TX0DBMTRIMDUTYCYP")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 3, name: "TXPATRIM10DBMDUTYCYN")
                    .WithReservedBits(11, 1)
                    .WithValueField(12, 3, name: "TXPATRIM10DBMDUTYCYP")
                    .WithReservedBits(15, 17)
                },
                {(long)RadioControllerRegisters.PowerAmplifierTrim3, new DoubleWordRegister(this, 0x008D33AA)
                    .WithReservedBits(0, 1)
                    .WithFlag(1, name: "TXPATRIMBLEEDAUTOREG")
                    .WithValueField(2, 2, name: "TXPATRIMIBIASMASTER")
                    .WithValueField(4, 2, name: "TXPATRIMPREDRVREGFB")
                    .WithFlag(6, name: "TXPATRIMPREDRVREGFBKATT")
                    .WithFlag(7, name: "TXPATRIMPREDRVREGPSR")
                    .WithValueField(8, 2, name: "TXPATRIMPREDRVREGSLICES")
                    .WithReservedBits(10, 2)
                    .WithValueField(12, 3, name: "TXPATRIMPREDRVREGVREF")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 3, name: "TXPATRIMREGFB")
                    .WithFlag(19, name: "TXPATRIMREGPSR")
                    .WithValueField(20, 4, name: "TXPATRIMREGVREF")
                    .WithReservedBits(24, 8)
                },
                {(long)RadioControllerRegisters.PowerAmplifierTrim4, new DoubleWordRegister(this, 0x00006000)
                    .WithValueField(0, 4, name: "TXPATRIM10DBMCTAP")
                    .WithValueField(4, 2, name: "TXPATRIM10DBMPREDRVCAP")
                    .WithValueField(6, 2, name: "TXPATRIM10DBMPREDRVSLC")
                    .WithValueField(8, 4, name: "TXPATRIM20DBMCTAP")
                    .WithValueField(12, 4, name: "TXPATRIM20DBMPREDRV")
                    .WithValueField(16, 4, name: "TXPATRIMCAPPAOUTM")
                    .WithValueField(20, 4, name: "TXPATRIMCAPPAOUTP")
                    .WithValueField(24, 2, name: "TXPATRIMCMGAIN")
                    .WithReservedBits(26, 6)
                },
                {(long)RadioControllerRegisters.PowerAmplifierTrim5, new DoubleWordRegister(this, 0x00181800)
                    .WithFlag(0, name: "TXPATRIMDACGLITCH")
                    .WithValueField(1, 3, name: "TXPATRIMDLY0")
                    .WithValueField(4, 3, name: "TXPATRIMDLY1")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 4, name: "TXPATRIMNBIAS")
                    .WithValueField(12, 2, name: "TXPATRIMNCASC")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 4, name: "TXPATRIMPBIAS")
                    .WithValueField(20, 2, name: "TXPATRIMPCASC")
                    .WithReservedBits(22, 2)
                    .WithValueField(24, 2, name: "TXPATRIMREGSLICES")
                    .WithReservedBits(26, 6)
                },
                {(long)RadioControllerRegisters.TxPower, new DoubleWordRegister(this, 0x00000010)
                    .WithValueField(0, 4, name: "TX0DBMPOWER")
                    .WithValueField(4, 2, name: "TX0DBMSELSLICE")
                    .WithReservedBits(6, 26)
                },
                {(long)RadioControllerRegisters.TxRamp, new DoubleWordRegister(this)
                    .WithFlag(0, name: "PARAMPMODE")
                    .WithReservedBits(1, 31)
                },
                {(long)RadioControllerRegisters.ProgrammableGainAmplifierTrim, new DoubleWordRegister(this, 0x00000B15)
                    .WithValueField(0, 5, name: "PGACTUNE")
                    .WithReservedBits(5, 1)
                    .WithValueField(6, 3, name: "PGAVCMOUTTRIM")
                    .WithValueField(9, 3, name: "PGAVLDOTRIM")
                    .WithReservedBits(12, 20)
                },
                {(long)RadioControllerRegisters.ProgrammableGainAmplifierCalibration, new DoubleWordRegister(this, 0x00002020)
                    .WithValueField(0, 6, name: "PGAOFFCALI")
                    .WithReservedBits(6, 2)
                    .WithValueField(8, 6, name: "PGAOFFCALQ")
                    .WithReservedBits(14, 18)
                },
                {(long)RadioControllerRegisters.ProgrammableGainAmplifierControl, new DoubleWordRegister(this, 0x00008001)
                    .WithValueField(0, 4, name: "PGABWMODE")
                    .WithFlag(4, name: "PGAENGHZ")
                    .WithFlag(5, name: "PGAENBIAS")
                    .WithFlag(6, name: "PGAENLATCHI")
                    .WithFlag(7, name: "PGAENLATCHQ")
                    .WithFlag(8, name: "PGAENLDOLOAD")
                    .WithFlag(9, name: "PGAENPGAI")
                    .WithFlag(10, name: "PGAENPGAQ")
                    .WithFlag(11, name: "PGAENPKD")
                    .WithReservedBits(12, 1)
                    .WithFlag(13, name: "PGAENSUBG")
                    .WithValueField(14, 3, name: "PGAPOWERMODE")
                    .WithValueField(17, 4, name: "PGATHRPKDLOSEL")
                    .WithValueField(21, 4, name: "PGATHRPKDHISEL")
                    .WithReservedBits(25, 7)
                },
                {(long)RadioControllerRegisters.RadioFrequencyBiasCalibration, new DoubleWordRegister(this, 0x30201A20)
                    .WithValueField(0, 6, name: "RFBIASCALBIAS")
                    .WithReservedBits(6, 2)
                    .WithValueField(8, 6, name: "RFBIASCALTC")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 6, name: "RFBIASCALVREF")
                    .WithReservedBits(22, 2)
                    .WithValueField(24, 6, name: "RFBIASCALVREFSTARTUP")
                    .WithReservedBits(30, 2)
                },
                {(long)RadioControllerRegisters.RadioFrequencyBiasControl, new DoubleWordRegister(this, 0x00040000)
                    .WithFlag(0, name: "RFBIASDISABLEBOOTSTRAP")
                    .WithFlag(1, name: "RFBIASLDOHIGHCURRENT")
                    .WithFlag(2, name: "RFBIASNONFLASHMODE")
                    .WithFlag(3, name: "RFBIASSTARTUPCORE")
                    .WithFlag(4, name: "RFBIASSTARTUPSUPPLY")
                    .WithReservedBits(5, 11)
                    .WithValueField(16, 4, name: "RFBIASLDOVREFTRIM")
                    .WithReservedBits(20, 12)
                },
                {(long)RadioControllerRegisters.RadioEnable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "PREEN")
                    .WithFlag(1, name: "PRESTB100UDIS")
                    .WithFlag(2, name: "RFBIASEN")
                    .WithReservedBits(3, 29)
                },
                {(long)RadioControllerRegisters.RadioFrequencyPathEnable0, new DoubleWordRegister(this, 0x00000002)
                    .WithFlag(0, name: "LNAMIXEN0")
                    .WithFlag(1, name: "LNAMIXRFATTDCEN0")
                    .WithFlag(2, name: "LNAMIXRFPKDENRF0")
                    .WithFlag(3, name: "SYLODIVRLO02G4EN")
                    .WithReservedBits(4, 2)
                    .WithFlag(6, name: "LNAMIXTRSW0")
                    .WithReservedBits(7, 25)
                },
                {(long)RadioControllerRegisters.RadioFrequencyPathEnable1, new DoubleWordRegister(this, 0x00000002)
                    .WithFlag(0, name: "LNAMIXEN1")
                    .WithFlag(1, name: "LNAMIXRFATTDCEN1")
                    .WithFlag(2, name: "LNAMIXRFPKDENRF1")
                    .WithFlag(3, name: "SYLODIVRLO12G4EN")
                    .WithReservedBits(4, 2)
                    .WithFlag(6, name: "LNAMIXTRSW1")
                    .WithReservedBits(7, 25)
                },
                {(long)RadioControllerRegisters.Rx, new DoubleWordRegister(this, 0x00000020)
                    .WithFlag(0, name: "IFADCCAPRESET")
                    .WithFlag(1, name: "IFADCENLDOSERIES")
                    .WithFlag(2, name: "IFADCENLDOSHUNT")
                    .WithFlag(3, name: "LNAMIXENRFPKD")
                    .WithFlag(4, name: "LNAMIXENRFPKDLOTHRESH")
                    .WithFlag(5, name: "LNAMIXLDOLOWCUR")
                    .WithReservedBits(6, 1)
                    .WithFlag(7, name: "LNAMIXREGLOADEN")
                    .WithFlag(8, name: "PGAENLDO")
                    .WithFlag(9, name: "SYCHPQNC3EN")
                    .WithReservedBits(10, 6)
                    .WithFlag(16, name: "SYCHPBIASTRIMBUFRX")
                    .WithFlag(17, name: "SYPFDCHPLPENRX")
                    .WithFlag(18, name: "SYPFDFPWENRX")
                    .WithReservedBits(19, 13)
                },
                {(long)RadioControllerRegisters.Tx, new DoubleWordRegister(this)
                    .WithFlag(0, name: "TXPAENREG")
                    .WithFlag(1, name: "TXPAENRAMPCLK")
                    .WithFlag(2, name: "TX0DBMENBLEEDREG")
                    .WithFlag(3, name: "TX0DBMENREG")
                    .WithReservedBits(4, 1)
                    .WithFlag(5, name: "TX0DBMENBIAS")
                    .WithFlag(6, name: "TX0DBMENPREDRV")
                    .WithFlag(7, name: "TX0DBMENPREDRVREG")
                    .WithFlag(8, name: "TX0DBMENPREDRVREGBIAS")
                    .WithFlag(9, name: "TX0DBMENRAMPCLK")
                    .WithFlag(10, name: "TX0DBMENBLEEDPREDRVREG")
                    .WithReservedBits(11, 1)
                    .WithFlag(12, name: "TXPAENBLEEDPREDRVREG")
                    .WithFlag(13, name: "TXPAENBLEEDREG")
                    .WithFlag(14, name: "TXPAENPAOUT")
                    .WithFlag(15, name: "TXPAENPREDRVREG")
                    .WithFlag(16, name: "SYCHPBIASTRIMBUFTX")
                    .WithFlag(17, name: "SYPFDCHPLPENTX")
                    .WithFlag(18, name: "SYPFDFPWENTX")
                    .WithFlag(19, name: "TXPAEN10DBM")
                    .WithFlag(20, name: "TXPAEN10DBMMAINCTAP")
                    .WithFlag(21, name: "TXPAEN10DBMPREDRV")
                    .WithFlag(22, name: "TXPAEN10DBMVMID")
                    .WithFlag(23, name: "TXPAEN20DBM")
                    .WithFlag(24, name: "TXPAEN20DBMMAINCTAP")
                    .WithFlag(25, name: "TXPAEN20DBMPREDRV")
                    .WithFlag(26, name: "TXPAEN20DBMVMID")
                    .WithFlag(27, name: "TXPAEN20DBMVBIASHF")
                    .WithValueField(28, 2, name: "TXPATRIM20DBMVBIASHF")
                    .WithFlag(30, name: "ENPAPOWER")
                    .WithFlag(31, name: "ENPASELSLICE")
                },
                {(long)RadioControllerRegisters.SyDebug, new DoubleWordRegister(this, 0x00000020)
                    .WithFlag(0, name: "SYCHPSWITCHEDBIASEN")
                    .WithFlag(1, name: "SYENMMDDREGBIASSTB")
                    .WithFlag(2, name: "SYLODIVAUXLODIS")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, name: "SYLODIVLDOPSRTRIM")
                    .WithFlag(5, name: "SYPFDLOCKWINDOWN")
                    .WithFlag(6, name: "SYPFDTRADMODEEN")
                    .WithReservedBits(7, 1)
                    .WithFlag(8, name: "SYVCOREGBYPASS")
                    .WithReservedBits(9, 23)
                },
                {(long)RadioControllerRegisters.SyTrim0, new DoubleWordRegister(this, 0x018FF169)
                    .WithValueField(0, 3, name: "SYCHPBIAS")
                    .WithValueField(3, 3, name: "SYCHPCURRRX")
                    .WithValueField(6, 3, name: "SYCHPCURRTX")
                    .WithValueField(9, 3, name: "SYCHPLEVNSRC")
                    .WithValueField(12, 3, name: "SYCHPLEVPSRCRX")
                    .WithValueField(15, 3, name: "SYCHPLEVPSRCTX")
                    .WithFlag(18, name: "SYCHPSRCENRX")
                    .WithFlag(19, name: "SYCHPSRCENTX")
                    .WithValueField(20, 3, name: "SYCHPREPLICACURRADJ")
                    .WithValueField(23, 3, name: "SYTRIMCHPREGAMPBIAS")
                    .WithValueField(26, 2, name: "SYTRIMCHPREGAMPBW")
                    .WithReservedBits(28, 4)
                },
                {(long)RadioControllerRegisters.SyTrim1, new DoubleWordRegister(this, 0x1FE00410)
                    .WithValueField(0, 2, name: "SYLODIVLDOTRIMCORERX")
                    .WithValueField(2, 2, name: "SYLODIVLDOTRIMCORETX")
                    .WithValueField(4, 4, name: "SYLODIVLDOTRIMNDIORX")
                    .WithValueField(8, 4, name: "SYLODIVLDOTRIMNDIOTX")
                    .WithReservedBits(12, 6)
                    .WithValueField(18, 3, name: "SYLODIVTLO20DBM2G4DELAY")
                    .WithValueField(21, 3, name: "SYMMDREPLICA1CURRADJ")
                    .WithValueField(24, 3, name: "SYMMDREPLICA2CURRADJ")
                    .WithValueField(27, 3, name: "SYTRIMMMDREGAMPBIAS")
                    .WithValueField(30, 2, name: "SYTRIMMMDREGAMPBW")
                },
                {(long)RadioControllerRegisters.SyCalibration, new DoubleWordRegister(this, 0x01008100)
                    .WithReservedBits(0, 8)
                    .WithFlag(8, name: "SYVCOMODEPKD")
                    .WithFlag(9, name: "SYVCOMORECURRENT")
                    .WithFlag(10, name: "SYVCOSLOWNOISEFILTER")
                    .WithReservedBits(11, 4)
                    .WithValueField(15, 2, name: "SYVCOVCAPVCM")
                    .WithReservedBits(17, 7)
                    .WithValueField(24, 2, name: "SYHILOADCHPREG")
                    .WithReservedBits(26, 6)
                },
                {(long)RadioControllerRegisters.SyEnable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "SYCHPEN")
                    .WithFlag(1, name: "SYCHPLPENRX")
                    .WithFlag(2, name: "SYCHPLPENTX")
                    .WithFlag(3, name: "SYENCHPREG")
                    .WithFlag(4, name: "SYENCHPREPLICA")
                    .WithFlag(5, name: "SYENMMDREG")
                    .WithFlag(6, name: "SYENMMDREPLICA1")
                    .WithFlag(7, name: "SYENMMDREPLICA2")
                    .WithFlag(8, name: "SYENVCOBIAS")
                    .WithFlag(9, name: "SYENVCOPFET")
                    .WithFlag(10, name: "SYENVCOREG")
                    .WithReservedBits(11, 2)
                    .WithFlag(13, name: "SYSTARTCHPREG")
                    .WithFlag(14, name: "SYSTARTMMDREG")
                    .WithReservedBits(15, 17)
                },
                {(long)RadioControllerRegisters.SyloEnable, new DoubleWordRegister(this)
                    .WithReservedBits(0, 1)
                    .WithFlag(1, name: "SYLODIVEN")
                    .WithFlag(2, name: "SYLODIVLDOBIASEN")
                    .WithFlag(3, name: "SYLODIVLDOEN")
                    .WithFlag(4, name: "SYLODIVRLOADCCLK2G4EN")
                    .WithReservedBits(5, 3)
                    .WithFlag(8, name: "SYLODIVTLO0DBM2G4AUXEN")
                    .WithFlag(9, name: "SYLODIVTLO0DBM2G4EN")
                    .WithFlag(10, name: "SYLODIVTLO20DBM2G4AUXEN")
                    .WithFlag(11, name: "SYLODIVTLO20DBM2G4EN")
                    .WithFlag(12, name: "SYLODIVRLOADCCLKSEL")
                    .WithReservedBits(13, 19)
                },
                {(long)RadioControllerRegisters.SymmControl, new DoubleWordRegister(this, 0x00000048)
                    .WithValueField(0, 2, name: "SYMMDDIVRSDIG")
                    .WithValueField(2, 3, name: "SYMMDMODERX")
                    .WithValueField(5, 3, name: "SYMMDMODETX")
                    .WithFlag(8, name: "SYMMDENRSDIG")
                    .WithReservedBits(9, 23)
                },
                {(long)RadioControllerRegisters.DigitalClockRetimeControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "DIGCLKRETIMEENRETIME")
                    .WithFlag(1, name: "DIGCLKRETIMEDISRETIME")
                    .WithFlag(2, name: "DIGCLKRETIMERESETN")
                    .WithReservedBits(3, 1)
                    .WithValueField(4, 3, name: "DIGCLKRETIMELIMITH")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 3, name: "DIGCLKRETIMELIMITL")
                    .WithReservedBits(11, 21)
                },
                {(long)RadioControllerRegisters.DigitalClockRetimeStatus, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, name: "DIGCLKRETIMECLKSEL")
                    .WithFlag(1, FieldMode.Read, name: "DIGCLKRETIMERESETNLO")
                    .WithReservedBits(2, 30)
                },
                {(long)RadioControllerRegisters.XoRetimeControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "XORETIMEENRETIME")
                    .WithFlag(1, name: "XORETIMEDISRETIME")
                    .WithFlag(2, name: "XORETIMERESETN")
                    .WithReservedBits(3, 1)
                    .WithValueField(4, 3, name: "XORETIMELIMITH")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 3, name: "XORETIMELIMITL")
                    .WithReservedBits(11, 21)
                },
                {(long)RadioControllerRegisters.XoRetimeStatus, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, name: "XORETIMECLKSEL")
                    .WithFlag(1, FieldMode.Read, name: "XORETIMERESETNLO")
                    .WithReservedBits(2, 30)
                },
                {(long)RadioControllerRegisters.AutomaticGainControlOverwrite0, new DoubleWordRegister(this)
                    .WithFlag(0, name: "ENMANLNAMIXRFATT")
                    .WithFlag(1, name: "ENMANLNAMIXSLICE")
                    .WithFlag(2, name: "ENMANPGAGAIN")
                    .WithFlag(3, name: "ENMANIFADCSCALE")
                    .WithValueField(4, 6, name: "MANLNAMIXSLICE0")
                    .WithValueField(10, 6, name: "MANLNAMIXSLICE1")
                    .WithReservedBits(16, 4)
                    .WithValueField(20, 4, name: "MANPGAGAIN")
                    .WithValueField(24, 2, name: "MANIFADCSCALE")
                    .WithReservedBits(26, 6)
                },
                {(long)RadioControllerRegisters.AutomaticGainControlOverwrite1, new DoubleWordRegister(this)
                    .WithValueField(0, 14, name: "MANLNAMIXRFATT0")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 14, name: "MANLNAMIXRFATT1")
                    .WithReservedBits(30, 2)
                },
                {(long)RadioControllerRegisters.AutomaticGainControlOverwrite2, new DoubleWordRegister(this)
                    .WithFlag(0, name: "ENMANFENOTCH")
                    .WithFlag(1, name: "MANFENOTCHEN")
                    .WithValueField(2, 4, name: "MANFENOTCHATTNSEL")
                    .WithFlag(6, name: "MANFENOTCHRATTNENRF0")
                    .WithFlag(7, name: "MANFENOTCHRATTNENRF1")
                    .WithValueField(8, 4, name: "MANFENOTCHCAPCRSE")
                    .WithValueField(12, 4, name: "MANFENOTCHCAPFINE")
                    .WithReservedBits(16, 16)
                },
                {(long)RadioControllerRegisters.Spare, new DoubleWordRegister(this)
                    .WithValueField(0, 3, name: "AUXADCSPARE")
                    .WithValueField(3, 2, name: "CLKMULTSPARE")
                    .WithFlag(5, name: "TX0DBMSPARE")
                    .WithReservedBits(6, 3)
                    .WithValueField(9, 8, name: "SYSPARE")
                    .WithValueField(17, 5, name: "FENOTCHSPARE")
                    .WithValueField(22, 4, name: "LNAMIXSPARE")
                    .WithValueField(26, 3, name: "IFADCCTRLRESERVED")
                    .WithValueField(29, 3, name: "IFADCCTRLRESERVEDLV")
                },
                {(long)RadioControllerRegisters.PowerAmplifierControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "TX0DBMLATCHBYPASS")
                    .WithFlag(1, name: "TX0DBMSLICERESET")
                    .WithFlag(2, name: "TXPABYPASSPREDRVREG")
                    .WithFlag(3, name: "TXPAPULLDOWNVDDPA")
                    .WithValueField(4, 4, name: "TXPAPOWER")
                    .WithFlag(8, name: "TXPALATCHBYPASS10DBM")
                    .WithFlag(9, name: "TXPALATCHBYPASS20DBM")
                    .WithFlag(10, name: "TXPASELPREDRVREGVDDPA")
                    .WithFlag(11, name: "TXPASELPREDRVREGVDDRF")
                    .WithValueField(12, 4, name: "TXPASELSLICE")
                    .WithFlag(16, name: "TXPASLICERST")
                    .WithReservedBits(17, 15)
                },
                {(long)RadioControllerRegisters.FeNotch0, new DoubleWordRegister(this, 0x00004000)
                    .WithReservedBits(0, 12)
                    .WithValueField(12, 3, name: "FENOTCHVBIAS")
                    .WithReservedBits(15, 17)
                },
                {(long)RadioControllerRegisters.FeNotch1, new DoubleWordRegister(this)
                    .WithFlag(0, name: "FENOTCHENVDDSW")
                    .WithReservedBits(1, 1)
                    .WithFlag(2, name: "FENOTCHRCCALEN")
                    .WithReservedBits(3, 5)
                    .WithValueField(8, 8, name: "FENOTCHRCCALCOUNTER")
                    .WithValueField(16, 4, name: "FENOTCHRCCALOSC")
                    .WithFlag(20, name: "FENOTCHRCCALOUT")
                    .WithReservedBits(21, 11)
                },
                {(long)RadioControllerRegisters.ThermisterControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "EN")
                    .WithFlag(1, name: "HALFSWITCH")
                    .WithReservedBits(2, 30)
                },
                {(long)RadioControllerRegisters.DiagaAlternateEnable, new DoubleWordRegister(this)
                    .WithReservedBits(0, 1)
                    .WithFlag(1, name: "RFEN")
                    .WithReservedBits(2, 30)
                },
                {(long)RadioControllerRegisters.DiagaAlternateRfBlocksAndTpSelect, new DoubleWordRegister(this)
                    .WithValueField(0, 6, name: "TP")
                    .WithReservedBits(6, 2)
                    .WithValueField(8, 7, name: "BLK")
                    .WithReservedBits(15, 17)
                },
                {(long)RadioControllerRegisters.DiagaAlternateBridgeControl, new DoubleWordRegister(this)
                    .WithReservedBits(0, 5)
                    .WithFlag(5, name: "RFSHORTDIS")
                    .WithReservedBits(6, 26)
                },
                {(long)RadioControllerRegisters.RadioFrequencyLock1, new DoubleWordRegister(this, 0x000000FF)
                    .WithValueField(0, 4, name: "RACPASTRIPE")
                    .WithValueField(4, 4, name: "RACPASLICE")
                    .WithReservedBits(8, 24)
                },
            };

            return new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private DoubleWordRegisterCollection BuildFrameControllerRegistersCollection()
        {
            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            {
                {(long)FrameControllerRegisters.IpVersion, new DoubleWordRegister(this, 0x00000003)
                    .WithValueField(0, 32, FieldMode.Read, name: "IPVERSION")
                },
                {(long)FrameControllerRegisters.Enable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "EN")
                    .WithReservedBits(1, 31)
                },
                {(long)FrameControllerRegisters.Status, new DoubleWordRegister(this)
                    .WithValueField(0, 5, name: "SNIFFDCOUNT")
                    .WithFlag(5, out FRC_activeTransmitFrameDescriptor, FieldMode.Read, name: "ACTIVETXFCD")
                    .WithFlag(6, out FRC_activeReceiveFrameDescriptor, FieldMode.Read, name: "ACTIVERXFCD")
                    .WithFlag(7, name: "SNIFFDFRAME")
                    .WithFlag(8, out FRC_rxRawBlocked, FieldMode.Read, name: "RXRAWBLOCKED")
                    .WithFlag(9, FieldMode.Read, valueProviderCallback: _ => FRC_frameOk, name: "FRAMEOK")
                    .WithFlag(10, FieldMode.Read, valueProviderCallback: _ => FRC_rxAbortInProgress, name: "RXABORTINPROGRESS")
                    .WithFlag(11, name: "TXWORD")
                    .WithFlag(12, name: "RXWORD")
                    .WithFlag(13, name: "CONVPAUSED")
                    .WithFlag(14, name: "TXSUBFRAMEPAUSED")
                    .WithFlag(15, name: "INTERLEAVEREADPAUSED")
                    .WithFlag(16, name: "INTERLEAVEWRITEPAUSED")
                    .WithFlag(17, name: "FRAMEDETPAUSED")
                    .WithFlag(18, FieldMode.Read, valueProviderCallback: _ => FRC_frameLengthError, name: "FRAMELENGTHERROR")
                    .WithFlag(19, name: "DEMODERROR")
                    .WithValueField(20, 5, out FRC_fsmState, name: "FSMSTATE")
                    .WithFlag(25, name: "RXWCNTMATCHPAUSED")
                    .WithFlag(26, FieldMode.Read, valueProviderCallback: _ => FRC_crcErrorTolerated, name: "CRCERRORTOLERATED")
                    .WithReservedBits(27, 5)
                },
                {(long)FrameControllerRegisters.DynamicFrameLengthControl, new DoubleWordRegister(this)
                    .WithEnumField<DoubleWordRegister, FRC_DynamicFrameLengthMode>(0, 3, out FRC_dynamicFrameLengthMode, name: "DFLMODE")
                    .WithEnumField<DoubleWordRegister, FRC_DynamicFrameLengthBitOrder>(3, 1, out FRC_dynamicFrameLengthBitOrder, name: "DFLBITORDER")
                    .WithValueField(4, 3, out FRC_dynamicFrameLengthBitShift, name: "DFLSHIFT")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 4, out FRC_dynamicFrameLengthOffset, name: "DFLOFFSET")
                    .WithValueField(12, 4, out FRC_dynamicFrameLengthBits, name: "DFLBITS")
                    .WithValueField(16, 4, out FRC_minDecodedLength, name: "MINLENGTH")
                    .WithFlag(20, out FRC_dynamicFrameCrcIncluded, name: "DFLINCLUDECRC")
                    .WithValueField(21, 4, name: "DFLBOIOFFSET")
                    .WithReservedBits(25, 7)
                },
                {(long)FrameControllerRegisters.MaximumFrameLength, new DoubleWordRegister(this, 0x00004FFF)
                    .WithValueField(0, 12, out FRC_maxDecodedLength, name: "MAXLENGTH")
                    .WithValueField(12, 4, out FRC_initialDecodedFrameLength, name: "INILENGTH")
                    .WithReservedBits(16, 16)
                },
                {(long)FrameControllerRegisters.AddressFilterControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "EN")
                    .WithFlag(1, name: "BRDCST00EN")
                    .WithFlag(2, name: "BRDCSTFFEN")
                    .WithReservedBits(3, 5)
                    .WithValueField(8, 8, name: "ADDRESS")
                    .WithReservedBits(16, 16)
                },
                {(long)FrameControllerRegisters.FrameControllerDataBuffer, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "DATABUFFER")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.WordCounter, new DoubleWordRegister(this)
                    .WithValueField(0 ,12, out FRC_wordCounter, FieldMode.Read, name: "WCNT")
                    .WithReservedBits(12, 20)
                },
                {(long)FrameControllerRegisters.WordCounterCompare0, new DoubleWordRegister(this)
                    .WithValueField(0, 12, out FRC_frameLength, name: "FRAMELENGTH")
                    .WithReservedBits(12, 20)
                },
                {(long)FrameControllerRegisters.WordCounterCompare1, new DoubleWordRegister(this)
                    .WithValueField(0, 12, out FRC_lengthFieldLocation, name: "LENGTHFIELDLOC")
                    .WithReservedBits(12, 20)
                },
                {(long)FrameControllerRegisters.WordCounterCompare2, new DoubleWordRegister(this)
                    .WithValueField(0, 12, out FRC_addressFieldLocation, name: "ADDRFIELDLOC")
                    .WithReservedBits(12, 20)
                },
                {(long)FrameControllerRegisters.Command, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Set, writeCallback: (_, value) => {if (value) FRC_RxAbortCommand(); }, name: "RXABORT")
                    .WithFlag(1, name: "FRAMEDETRESUME")
                    .WithFlag(2, name: "INTERLEAVEWRITERESUME")
                    .WithFlag(3, name: "INTERLEAVEREADRESUME")
                    .WithFlag(4, name: "CONVRESUME")
                    .WithFlag(5, name: "CONVTERMINATE")
                    .WithFlag(6, name: "TXSUBFRAMERESUME")
                    .WithFlag(7, name: "INTERLEAVEINIT")
                    .WithFlag(8, name: "INTERLEAVECNTCLEAR")
                    .WithFlag(9, name: "CONVINIT")
                    .WithFlag(10, name: "BLOCKINIT")
                    .WithFlag(11, name: "STATEINIT")
                    .WithFlag(12, FieldMode.Set, writeCallback: (_, value) =>
                        {
                            if(value)
                            {
                                FRC_rxRawBlocked.Value = false;
                                FRC_UpdateRawMode();
                            }
                        }, name: "RXRAWUNBLOCK")
                    .WithFlag(13, name: "RXPAUSERESUME")
                    .WithReservedBits(14, 18)
                },
                {(long)FrameControllerRegisters.Control, new DoubleWordRegister(this, 0x03000700)
                    .WithFlag(0, name: "RANDOMTX")
                    .WithFlag(1, name: "UARTMODE")
                    .WithFlag(2, name: "BITORDER")
                    .WithReservedBits(3, 1)
                    .WithEnumField<DoubleWordRegister, FRC_FrameDescriptorMode>(4, 2, out FRC_txFrameDescriptorMode, name: "TXFCDMODE")
                    .WithEnumField<DoubleWordRegister, FRC_FrameDescriptorMode>(6, 2, out FRC_rxFrameDescriptorMode, name: "RXFCDMODE")
                    .WithValueField(8, 3, name: "BITSPERWORD") // TODO: Assume 0x7 (first word in a frame is 8bit)
                    .WithValueField(11, 2, out FRC_rateSelect, name: "RATESELECT")
                    .WithFlag(13, name: "TXPREFETCH")
                    .WithFlag(14, name: "TXFETCHBLOCKING")
                    .WithReservedBits(15, 1)
                    .WithFlag(16, name: "SEQHANDSHAKE")
                    .WithFlag(17, name: "PRBSTEST")
                    .WithFlag(18, name: "LPMODEDIS")
                    .WithFlag(19, name: "WAITEOFEN")
                    .WithFlag(20, name: "RXABORTIGNOREDIS")
                    .WithReservedBits(21, 3)
                    .WithFlag(24, name: "SKIPTXTRAILDATAWHITEN")
                    .WithFlag(25, name: "SKIPRXSUPSTATEWHITEN")
                    .WithFlag(26, name: "HOLDTXTRAILDATAACTIVE")
                    .WithReservedBits(27, 5)
                },
                {(long)FrameControllerRegisters.RxControl, new DoubleWordRegister(this)
                    .WithFlag(0, out FRC_rxStoreCrc, name: "STORECRC")
                    .WithFlag(1, out FRC_rxAcceptCrcErrors, name: "ACCEPTCRCERRORS")
                    .WithFlag(2, name: "ACCEPTBLOCKERRORS")
                    .WithFlag(3, name: "TRACKABFRAME") /* TODO: RENODE-354 */
                    .WithFlag(4, out FRC_rxBufferClear, name: "BUFCLEAR")
                    .WithFlag(5, out FRC_rxBufferRestoreOnFrameError, name: "BUFRESTOREFRAMEERROR")
                    .WithFlag(6, out FRC_rxBufferRestoreOnRxAborted, name: "BUFRESTORERXABORTED")
                    .WithValueField(7, 4, name: "RXFRAMEENDAHEADBYTES")
                    .WithFlag(11, name: "ACCEPTUARTERRORS")
                    .WithReservedBits(12, 20)
                },
                {(long)FrameControllerRegisters.TrailingRxData, new DoubleWordRegister(this)
                    .WithFlag(0, out FRC_rxAppendRssi, name: "RSSI")
                    .WithFlag(1, out FRC_rxAppendStatus, name: "CRCOK")
                    .WithFlag(2, out FRC_rxAppendProtimerCc0base, name: "PROTIMERCC0BASE")
                    .WithFlag(3, out FRC_rxAppendProtimerCc0LowWrap, name: "PROTIMERCC0WRAPL")
                    .WithFlag(4, out FRC_rxAppendProtimerCc0HighWrap, name: "PROTIMERCC0WRAPH")
                    .WithFlag(5, name: "RTCSTAMP")
                    .WithReservedBits(6, 26)
                },
                {(long)FrameControllerRegisters.InterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out FRC_txDoneInterrupt, name: "TXDONEIF")
                    .WithFlag(1, out FRC_txAfterFrameDoneInterrupt, name: "TXAFTERFRAMEDONEIF")
                    .WithFlag(2, out FRC_txAbortedInterrupt, name: "TXABORTEDIF")
                    .WithFlag(3, out FRC_txUnderflowInterrupt, name: "TXUFIF")
                    .WithFlag(4, out FRC_rxDoneInterrupt, name: "RXDONEIF")
                    .WithFlag(5, out FRC_rxAbortedInterrupt, name: "RXABORTEDIF")
                    .WithFlag(6, out FRC_frameErrorInterrupt, name: "FRAMEERRORIF")
                    .WithFlag(7, name: "BLOCKERRORIF")
                    .WithFlag(8, out FRC_rxOverflowInterrupt, name: "RXOFIF")
                    .WithFlag(9, out FRC_wordCounterCompareInterrupt[0], name: "WCNTCMP0IF")
                    .WithFlag(10, out FRC_wordCounterCompareInterrupt[1], name: "WCNTCMP1IF")
                    .WithFlag(11, out FRC_wordCounterCompareInterrupt[2], name: "WCNTCMP2IF")
                    .WithFlag(12, name: "ADDRERRORIF")
                    .WithFlag(13, name: "BUSERRORIF")
                    .WithFlag(14, out FRC_rxRawEventInterrupt, name: "RXRAWEVENTIF")
                    .WithFlag(15, out FRC_txRawEventInterrupt, name: "TXRAWEVENTIF")
                    .WithFlag(16, name: "SNIFFOFIF")
                    .WithFlag(17, name: "WCNTCMP3IF")
                    .WithFlag(18, name: "WCNTCMP4IF")
                    .WithFlag(19, name: "BOISETIF")
                    .WithFlag(20, out FRC_packetBufferStartInterrupt, name: "PKTBUFSTARTIF")
                    .WithFlag(21, out FRC_packetBufferThresholdInterrupt, name: "PKTBUFTHRESHOLDIF")
                    .WithFlag(22, name: "RXRAWOFIF")
                    .WithFlag(23, name: "WCNTCMP5IF")
                    .WithFlag(24, name: "FRAMEDETPAUSEDIF")
                    .WithFlag(25, name: "INTERLEAVEWRITEPAUSEDIF")
                    .WithFlag(26, name: "INTERLEAVEREADPAUSEDIF")
                    .WithFlag(27, name: "TXSUBFRAMEPAUSEDIF")
                    .WithFlag(28, name: "CONVPAUSEDIF")
                    .WithFlag(29, name: "RXWORDIF")
                    .WithFlag(30, name: "TXWORDIF")
                    .WithFlag(31, name: "UARTERRORIF")
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)FrameControllerRegisters.InterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out FRC_txDoneInterruptEnable, name: "TXDONEIEN")
                    .WithFlag(1, out FRC_txAfterFrameDoneInterruptEnable, name: "TXAFTERFRAMEDONEIEN")
                    .WithFlag(2, out FRC_txAbortedInterruptEnable, name: "TXABORTEDIEN")
                    .WithFlag(3, out FRC_txUnderflowInterruptEnable, name: "TXUFIEN")
                    .WithFlag(4, out FRC_rxDoneInterruptEnable, name: "RXDONEIEN")
                    .WithFlag(5, out FRC_rxAbortedInterruptEnable, name: "RXABORTEDIEN")
                    .WithFlag(6, out FRC_frameErrorInterruptEnable, name: "FRAMEERRORIEN")
                    .WithFlag(7, name: "BLOCKERRORIEN")
                    .WithFlag(8, out FRC_rxOverflowInterruptEnable, name: "RXOFIEN")
                    .WithFlag(9, out FRC_wordCounterCompareInterruptEnable[0], name: "WCNTCMP0IEN")
                    .WithFlag(10, out FRC_wordCounterCompareInterruptEnable[1], name: "WCNTCMP1IEN")
                    .WithFlag(11, out FRC_wordCounterCompareInterruptEnable[2], name: "WCNTCMP2IEN")
                    .WithFlag(12, name: "ADDRERRORIEN")
                    .WithFlag(13, name: "BUSERRORIEN")
                    .WithFlag(14, out FRC_rxRawEventInterruptEnable, name: "RXRAWEVENTIEN")
                    .WithFlag(15, out FRC_txRawEventInterruptEnable, name: "TXRAWEVENTIEN")
                    .WithFlag(16, name: "SNIFFOFIEN")
                    .WithFlag(17, name: "WCNTCMP3IEN")
                    .WithFlag(18, name: "WCNTCMP4IEN")
                    .WithFlag(19, name: "BOISETIEN")
                    .WithFlag(20, out FRC_packetBufferStartInterruptEnable, name: "PKTBUFSTARTIEN")
                    .WithFlag(21, out FRC_packetBufferThresholdInterruptEnable, name: "PKTBUFTHRESHOLDIEN")
                    .WithFlag(22, name: "RXRAWOFIEN")
                    .WithFlag(23, name: "WCNTCMP5IEN")
                    .WithFlag(24, name: "FRAMEDETPAUSEDIEN")
                    .WithFlag(25, name: "INTERLEAVEWRITEPAUSEDIEN")
                    .WithFlag(26, name: "INTERLEAVEREADPAUSEDIEN")
                    .WithFlag(27, name: "TXSUBFRAMEPAUSEDIEN")
                    .WithFlag(28, name: "CONVPAUSEDIEN")
                    .WithFlag(29, name: "RXWORDIEN")
                    .WithFlag(30, name: "TXWORDIEN")
                    .WithFlag(31, name: "UARTERRORIEN")
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)FrameControllerRegisters.SequencerInterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out FRC_seqTxDoneInterrupt, name: "TXDONESEQIF")
                    .WithFlag(1, out FRC_seqTxAfterFrameDoneInterrupt, name: "TXAFTERFRAMEDONESEQIF")
                    .WithFlag(2, out FRC_seqTxAbortedInterrupt, name: "TXABORTEDSEQIF")
                    .WithFlag(3, out FRC_seqTxUnderflowInterrupt, name: "TXUFSEQIF")
                    .WithFlag(4, out FRC_seqRxDoneInterrupt, name: "RXDONESEQIF")
                    .WithFlag(5, out FRC_seqRxAbortedInterrupt, name: "RXABORTEDSEQIF")
                    .WithFlag(6, out FRC_seqFrameErrorInterrupt, name: "FRAMEERRORSEQIF")
                    .WithFlag(7, name: "BLOCKERRORSEQIF")
                    .WithFlag(8, out FRC_seqRxOverflowInterrupt, name: "RXOFSEQIF")
                    .WithFlag(9, out FRC_seqWordCounterCompareInterrupt[0], name: "WCNTCMP0SEQIF")
                    .WithFlag(10, out FRC_seqWordCounterCompareInterrupt[1], name: "WCNTCMP1SEQIF")
                    .WithFlag(11, out FRC_seqWordCounterCompareInterrupt[2], name: "WCNTCMP2SEQIF")
                    .WithFlag(12, name: "ADDRERRORSEQIF")
                    .WithFlag(13, name: "BUSERRORSEQIF")
                    .WithFlag(14, out FRC_seqRxRawEventInterrupt, name: "RXRAWEVENTSEQIF")
                    .WithFlag(15, out FRC_seqTxRawEventInterrupt, name: "TXRAWEVENTSEQIF")
                    .WithFlag(16, name: "SNIFFOFSEQIF")
                    .WithFlag(17, name: "WCNTCMP3SEQIF")
                    .WithFlag(18, name: "WCNTCMP4SEQIF")
                    .WithFlag(19, name: "BOISETSEQIF")
                    .WithFlag(20, out FRC_seqPacketBufferStartInterrupt, name: "PKTBUFSTARTSEQIF")
                    .WithFlag(21, out FRC_seqPacketBufferThresholdInterrupt, name: "PKTBUFTHRESHOLDSEQIF")
                    .WithFlag(22, name: "RXRAWOFSEQIF")
                    .WithFlag(23, name: "WCNTCMP5SEQIF")
                    .WithFlag(24, name: "FRAMEDETPAUSEDSEQIF")
                    .WithFlag(25, name: "INTERLEAVEWRITEPAUSEDSEQIF")
                    .WithFlag(26, name: "INTERLEAVEREADPAUSEDSEQIF")
                    .WithFlag(27, name: "TXSUBFRAMEPAUSEDSEQIF")
                    .WithFlag(28, name: "CONVPAUSEDSEQIF")
                    .WithFlag(29, name: "RXWORDSEQIF")
                    .WithFlag(30, name: "TXWORDSEQIF")
                    .WithFlag(31, name: "UARTERRORSEQIF")
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)FrameControllerRegisters.SequencerInterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out FRC_seqTxDoneInterruptEnable, name: "TXDONESEQIEN")
                    .WithFlag(1, out FRC_seqTxAfterFrameDoneInterruptEnable, name: "TXAFTERFRAMEDONESEQIEN")
                    .WithFlag(2, out FRC_seqTxAbortedInterruptEnable, name: "TXABORTEDSEQIEN")
                    .WithFlag(3, out FRC_seqTxUnderflowInterruptEnable, name: "TXUFSEQIEN")
                    .WithFlag(4, out FRC_seqRxDoneInterruptEnable, name: "RXDONESEQIEN")
                    .WithFlag(5, out FRC_seqRxAbortedInterruptEnable, name: "RXABORTEDSEQIEN")
                    .WithFlag(6, out FRC_seqFrameErrorInterruptEnable, name: "FRAMEERRORSEQIEN")
                    .WithFlag(7, name: "BLOCKERRORSEQIEN")
                    .WithFlag(8, out FRC_seqRxOverflowInterruptEnable, name: "RXOFSEQIEN")
                    .WithFlag(9, out FRC_seqWordCounterCompareInterruptEnable[0], name: "WCNTCMP0SEQIEN")
                    .WithFlag(10, out FRC_seqWordCounterCompareInterruptEnable[1], name: "WCNTCMP1SEQIEN")
                    .WithFlag(11, out FRC_seqWordCounterCompareInterruptEnable[2], name: "WCNTCMP2SEQIEN")
                    .WithFlag(12, name: "ADDRERRORSEQIEN")
                    .WithFlag(13, name: "BUSERRORSEQIEN")
                    .WithFlag(14, out FRC_seqRxRawEventInterruptEnable, name: "RXRAWEVENTSEQIEN")
                    .WithFlag(15, out FRC_seqTxRawEventInterruptEnable, name: "TXRAWEVENTSEQIEN")
                    .WithFlag(16, name: "SNIFFOFSEQIEN")
                    .WithFlag(17, name: "WCNTCMP3SEQIEN")
                    .WithFlag(18, name: "WCNTCMP4SEQIEN")
                    .WithFlag(19, name: "BOISETSEQIEN")
                    .WithFlag(20, out FRC_seqPacketBufferStartInterruptEnable, name: "PKTBUFSTARTSEQIEN")
                    .WithFlag(21, out FRC_seqPacketBufferThresholdInterruptEnable, name: "PKTBUFTHRESHOLDSEQIEN")
                    .WithFlag(22, name: "RXRAWOFSEQIEN")
                    .WithFlag(23, name: "WCNTCMP5SEQIEN")
                    .WithFlag(24, name: "FRAMEDETPAUSEDSEQIEN")
                    .WithFlag(25, name: "INTERLEAVEWRITEPAUSEDSEQIEN")
                    .WithFlag(26, name: "INTERLEAVEREADPAUSEDSEQIEN")
                    .WithFlag(27, name: "TXSUBFRAMEPAUSEDSEQIEN")
                    .WithFlag(28, name: "CONVPAUSEDSEQIEN")
                    .WithFlag(29, name: "RXWORDSEQIEN")
                    .WithFlag(30, name: "TXWORDSEQIEN")
                    .WithFlag(31, name: "UARTERRORSEQIEN")
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)FrameControllerRegisters.RawDataControl, new DoubleWordRegister(this)
                    .WithValueField(0, 2, name: "TXRAWMODE")
                    .WithEnumField<DoubleWordRegister, FRC_RxRawDataMode>(2, 3, out FRC_rxRawDataSelect, writeCallback: (_, value) =>
                        {
                            switch(value)
                            {
                                case FRC_RxRawDataMode.Disable:
                                    FRC_rxRawBlocked.Value = false;
                                    break;
                                case FRC_RxRawDataMode.SingleItem:
                                    break;
                                default:
                                    this.Log(LogLevel.Error, "Unsupported RXRAWMODE value ({0}).", value);
                                    break;
                            }
                        }, name: "RXRAWMODE")
                    .WithFlag(5, out FRC_enableRawDataRandomNumberGenerator, name: "RXRAWRANDOM")
                    .WithReservedBits(6, 1)
                    .WithEnumField<DoubleWordRegister, FRC_RxRawDataTriggerMode>(7, 2, out FRC_rxRawDataTriggerSelect, writeCallback: (_, value) =>
                        {
                            switch(value)
                            {
                                case FRC_RxRawDataTriggerMode.Immediate:
                                    break;
                                default:
                                    this.Log(LogLevel.Error, "Unsupported RXRAWTRIGGER value ({0}).", value);
                                    break;
                            }
                        }, name: "RXRAWTRIGGER")
                    .WithReservedBits(9, 4)
                    .WithFlag(13, name: "DEMODRAWDATAMUX")
                    .WithReservedBits(14, 18)
                    .WithChangeCallback((_, __) => FRC_UpdateRawMode())
                },
                {(long)FrameControllerRegisters.RxRawData, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ =>
                    {
                        if(FRC_rxRawDataSelect.Value == FRC_RxRawDataMode.SingleItem)
                        {
                            FRC_rxRawBlocked.Value = false;
                        }
                        FRC_UpdateRawMode();
                        return (uint)random.Next();
                    }, name: "RXRAWDATA")
                },
                {(long)FrameControllerRegisters.PacketCaptureBufferControl, new DoubleWordRegister(this)
                    .WithValueField(0, 12, out FRC_packetBufferStartAddress, name: "PKTBUFSTARTLOC")
                    .WithValueField(12, 6, out FRC_packetBufferThreshold, writeCallback: (_, __) => { UpdateInterrupts(); }, name: "PKTBUFTHRESHOLD")
                    .WithReservedBits(18, 6)
                    .WithFlag(24, out FRC_packetBufferThresholdEnable, writeCallback: (_, __) => { UpdateInterrupts(); }, name: "PKTBUFTHRESHOLDEN")
                    .WithFlag(25, out FRC_packetBufferStop, FieldMode.Write, name: "PKTBUFSTOP")
                    .WithReservedBits(26, 6)
                },
                {(long)FrameControllerRegisters.PacketCaptureBufferStatus, new DoubleWordRegister(this)
                    .WithValueField(0, 6, out FRC_packetBufferCount, FieldMode.Read, name: "PKTBUFCOUNT")
                    .WithReservedBits(6, 24)
                },
                {(long)FrameControllerRegisters.SnifferControl, new DoubleWordRegister(this, 0x000007FC)
                    .WithValueField(0, 2, name: "SNIFFMODE")
                    .WithFlag(2, name: "SNIFFBITS")
                    .WithFlag(3, out FRC_ptiEmitRx, name: "SNIFFRXDATA")
                    .WithFlag(4, out FRC_ptiEmitTx, name: "SNIFFTXDATA")
                    .WithFlag(5, out FRC_ptiEmitRssi, name: "SNIFFRSSI")
                    .WithFlag(6, out FRC_ptiEmitState, name: "SNIFFSTATE")
                    .WithFlag(7, out FRC_ptiEmitAux, name: "SNIFFAUXDATA")
                    .WithValueField(8, 8, name: "SNIFFBR")
                    .WithFlag(16, name: "SNIFFSLEEPCTRL")
                    .WithFlag(17, out FRC_ptiEmitSyncWord, name: "SNIFFSYNCWORD")
                    .WithReservedBits(18, 14)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)FrameControllerRegisters.AuxiliarySnifferDataOutput, new DoubleWordRegister(this)
                    .WithValueField(0, 9, writeCallback: (_, data) => {
                        if (FRC_ptiEmitAux.Value)
                        {
                            this.Log(LogLevel.Noisy, "PTI AUX: {0}", data);
                            // chop off the top bit
                            byte b = (byte) (data & 0xFF);
                            PtiDataOut?.Invoke(new byte[] { b });
                        }
                        if ((data & 0x0100) == 0u) {
                            PtiFrameComplete?.Invoke();
                        }
                    }, name: "SNIFFAUXDATA")
                    .WithReservedBits(9, 23)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)FrameControllerRegisters.WhitenerControl, new DoubleWordRegister(this)
                    .WithValueField(0, 5, name: "FEEDBACKSEL")
                    .WithValueField(5, 2, name: "XORFEEDBACK")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 4, name: "SHROUTPUTSEL")
                    .WithFlag(12, name: "BLOCKERRORCORRECT")
                    .WithReservedBits(13, 19)
                },
                {(long)FrameControllerRegisters.WhitenerPolynomial, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "POLY")
                    .WithReservedBits(16, 16)
                },
                {(long)FrameControllerRegisters.WhitenerInitialValue, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "WHITEINIT")
                    .WithReservedBits(16, 16)
                },
                {(long)FrameControllerRegisters.ForwardErrorCorrectionControl, new DoubleWordRegister(this)
                    .WithValueField(0, 3, name: "BLOCKWHITEMODE")
                    .WithReservedBits(3, 1)
                    .WithValueField(4, 2, name: "CONVMODE")
                    .WithFlag(6, name: "CONVDECODEMODE")
                    .WithFlag(7, name: "CONVTRACEBACKDISABLE")
                    .WithValueField(8, 2, name: "CONVINV")
                    .WithValueField(10, 2, name: "INTERLEAVEMODE")
                    .WithValueField(12, 4, name: "INTERLEAVEFIRSTINDEX")
                    .WithFlag(16, name: "INTERLEAVEWIDTH")
                    .WithFlag(17, name: "CONVBUSLOCK")
                    .WithFlag(18, name: "CONVSUBFRAMETERMINATE")
                    .WithFlag(19, name: "SINGLEBLOCK")
                    .WithFlag(20, name: "FORCE2FSK")
                    .WithFlag(21, name: "CONVHARDERROR")
                    .WithReservedBits(22, 10)
                },
                {(long)FrameControllerRegisters.BlockDecodingRamAddress, new DoubleWordRegister(this, 0x00004000)
                    .WithReservedBits(0, 2)
                    .WithValueField(2, 30, name: "BLOCKRAMADDR")
                },
                {(long)FrameControllerRegisters.ConvolutionalDecodingRamAddress, new DoubleWordRegister(this, 0x00004000)
                    .WithReservedBits(0, 2)
                    .WithValueField(2, 30, name: "CONVRAMADDR")
                },
                {(long)FrameControllerRegisters.TrailingTxDataControl, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "TRAILTXDATA")
                    .WithValueField(8, 3, name: "TRAILTXDATACNT")
                    .WithFlag(11, name: "TRAILTXDATAFORCE")
                    .WithValueField(12, 10, name: "TRAILTXREPLEN")
                    .WithFlag(22, name: "TXSUPPLENOVERIDE")
                    .WithFlag(23, name: "POSTAMBLEEN")
                    .WithReservedBits(24, 8)
                },
                {(long)FrameControllerRegisters.SubFrameCounterValue, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "SCNT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.ConvolutionalCoderGeneratorPolynomials, new DoubleWordRegister(this)
                    .WithValueField(0, 7, name: "GENERATOR0")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 7, name: "GENERATOR1")
                    .WithReservedBits(15, 1)
                    .WithFlag(16, name: "RECURSIVE")
                    .WithFlag(17, name: "NONSYSTEMATIC")
                    .WithReservedBits(18, 14)
                },
                {(long)FrameControllerRegisters.PuncturingControl, new DoubleWordRegister(this, 0x00000101)
                    .WithValueField(0, 7, name: "PUNCT0")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 7, name: "PUNCT1")
                    .WithReservedBits(15, 17)
                },
                {(long)FrameControllerRegisters.PauseControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "FRAMEDETPAUSEEN")
                    .WithFlag(1, name: "TXINTERLEAVEWRITEPAUSEEN")
                    .WithFlag(2, name: "RXINTERLEAVEWRITEPAUSEEN")
                    .WithFlag(3, name: "INTERLEAVEREADPAUSEEN")
                    .WithFlag(4, name: "TXSUBFRAMEPAUSEEN")
                    .WithFlag(5, name: "RXWCNTMATCHPAUSEEN")
                    .WithReservedBits(6, 5)
                    .WithValueField(11, 6, name: "CONVPAUSECNT")
                    .WithValueField(17, 5, name: "INTERLEAVEWRITEPAUSECNT")
                    .WithValueField(22, 5, name: "INTERLEAVEREADPAUSECNT")
                    .WithReservedBits(27, 5)
                },
                {(long)FrameControllerRegisters.OverTheAirNumberOfBitsCounter, new DoubleWordRegister(this)
                    .WithValueField(0, 16, FieldMode.Read, name: "OTARXCNT")
                    .WithValueField(16, 16, FieldMode.Read, name: "OTATXCNT")
                },
                {(long)FrameControllerRegisters.BufferControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "TXBUFFERMODE")
                    .WithValueField(1, 2, name: "RXBUFFERMODE")
                    .WithFlag(3, name: "RXFRCBUFMUX")
                    .WithReservedBits(4, 28)
                },
                {(long)FrameControllerRegisters.RxPauseData, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, name: "PAUSEDATA")
                },
                {(long)FrameControllerRegisters.MostLikelyConvolutionalDecoderState, new DoubleWordRegister(this)
                    .WithValueField(0, 6, FieldMode.Read, name: "LIKELYCONVSTATE")
                    .WithReservedBits(6, 26)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENTNEXT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverWritePointer, new DoubleWordRegister(this)
                    .WithValueField(0, 5, name: "INTWRITEPOINT")
                    .WithReservedBits(5, 27)
                },
                {(long)FrameControllerRegisters.InterleaverReadPointer, new DoubleWordRegister(this)
                    .WithValueField(0, 5, name: "INTREADPOINT")
                    .WithReservedBits(5, 27)
                },
                {(long)FrameControllerRegisters.AutomaticClockGating, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "AUTOCGEN")
                    .WithReservedBits(16, 16)
                },
                {(long)FrameControllerRegisters.AutomaticClockGatingClockStop, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "FORCEOFF")
                    .WithReservedBits(16, 16)
                },
                {(long)FrameControllerRegisters.WordCounterCompare3, new DoubleWordRegister(this)
                    .WithValueField(0, 12, name: "SUPPLENFIELDLOC")
                    .WithReservedBits(12, 20)
                },
                {(long)FrameControllerRegisters.BitOfInterestControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "BOIEN")
                    .WithValueField(1, 12, name: "BOIFIELDLOC")
                    .WithValueField(13, 3, name: "BOIBITPOS")
                    .WithFlag(16, name: "BOIMATCHVAL")
                    .WithReservedBits(17, 15)
                },
                {(long)FrameControllerRegisters.DynamicSuppLengthControl, new DoubleWordRegister(this)
                    .WithValueField(0, 3, name: "DSLMODE")
                    .WithFlag(3, name: "DSLBITORDER")
                    .WithValueField(4, 3, name: "DSLSHIFT")
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 8, name: "DSLOFFSET")
                    .WithValueField(16, 4, name: "DSLBITS")
                    .WithValueField(20, 4, name: "DSLMINLENGTH")
                    .WithValueField(24, 3, name: "RXSUPRECEPMODE")
                    .WithFlag(27, name: "STORESUP")
                    .WithValueField(28, 3, name: "SUPSHFFACTOR")
                    .WithReservedBits(31, 1)
                },
                {(long)FrameControllerRegisters.WordCounterCompare4, new DoubleWordRegister(this)
                    .WithValueField(0, 12, name: "SUPPLENGTH")
                    .WithReservedBits(12, 20)
                },
                {(long)FrameControllerRegisters.WordCounterCompare5, new DoubleWordRegister(this)
                    .WithValueField(0, 12, name: "RXPAUSELOC")
                    .WithReservedBits(12, 20)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue0, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue1, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue2, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue3, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue4, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue5, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue6, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue7, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue8, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue9, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue10, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue11, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue12, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue13, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue14, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.InterleaverElementValue15, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "INTELEMENT")
                    .WithReservedBits(8, 24)
                },
                {(long)FrameControllerRegisters.AhbConfiguration, new DoubleWordRegister(this, 0x00000001)
                    .WithFlag(0, name: "AHBHPROTBUFFERABLE")
                    .WithReservedBits(1, 31)
                },
            };

            var startOffset = (long)FrameControllerRegisters.FrameControlDescriptor0;
            var blockSize = (long)FrameControllerRegisters.FrameControlDescriptor1 - (long)FrameControllerRegisters.FrameControlDescriptor0;
            for(var index = 0; index < FRC_NumberOfFrameDescriptors; index++)
            {
                var i = index;

                registerDictionary.Add(startOffset + blockSize * i,
                    new DoubleWordRegister(this, 0x000000FF)
                        .WithValueField(0, 8, out FRC_frameDescriptor[i].WordsField, name: "WORDS")
                        .WithValueField(8, 2, out FRC_frameDescriptor[i].BufferField, name: "BUFFER")
                        .WithFlag(10, out FRC_frameDescriptor[i].IncludeCrc, name: "INCLUDECRC")
                        .WithFlag(11, out FRC_frameDescriptor[i].CalculateCrc, name: "CALCCRC")
                        .WithValueField(12, 2, out FRC_frameDescriptor[i].CrcSkipWords, name: "SKIPCRC")
                        .WithFlag(14, out FRC_frameDescriptor[i].SkipWhitening, name: "SKIPWHITE")
                        .WithFlag(15, out FRC_frameDescriptor[i].AddTrailData, name: "ADDTRAILTXDATA")
                        .WithFlag(16, out FRC_frameDescriptor[i].ExcludeSubframeFromWcnt, name: "EXCLUDESUBFRAMEWCNT")
                        .WithReservedBits(17, 15)
                );
            }

            startOffset = (long)FrameControllerRegisters.PacketCaptureDataBuffer0;
            blockSize = (long)FrameControllerRegisters.PacketCaptureDataBuffer1 - (long)FrameControllerRegisters.PacketCaptureDataBuffer0;
            for(var index = 0; index < (FRC_PacketBufferCaptureSize / 4); index++)
            {
                var i = index;

                registerDictionary.Add(startOffset + blockSize * i,
                    new DoubleWordRegister(this)
                        .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ => FRC_packetBufferCapture[i * 4], writeCallback: (_, value) => FRC_packetBufferCapture[i * 4] = (byte)value, name: $"PKTBUF{i * 4}")
                        .WithValueField(8, 8, FieldMode.Read, valueProviderCallback: _ => FRC_packetBufferCapture[i * 4 + 1], writeCallback: (_, value) => FRC_packetBufferCapture[i * 4 + 1] = (byte)value, name: $"PKTBUF{i * 4 + 1}")
                        .WithValueField(16, 8, FieldMode.Read, valueProviderCallback: _ => FRC_packetBufferCapture[i * 4 + 2], writeCallback: (_, value) => FRC_packetBufferCapture[i * 4 + 2] = (byte)value, name: $"PKTBUF{i * 4 + 2}")
                        .WithValueField(24, 8, FieldMode.Read, valueProviderCallback: _ => FRC_packetBufferCapture[i * 4 + 3], writeCallback: (_, value) => FRC_packetBufferCapture[i * 4 + 3] = (byte)value, name: $"PKTBUF{i * 4 + 3}")
                );
            }

            return new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private DoubleWordRegisterCollection BuildRadioMailboxRegistersCollection()
        {
            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            {
                {(long)RadioMailboxRegisters.MessagePointer0, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RFMAILBOX_messagePointer[0], name: "MSGPTR0")
                },
                {(long)RadioMailboxRegisters.MessagePointer1, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RFMAILBOX_messagePointer[1], name: "MSGPTR1")
                },
                {(long)RadioMailboxRegisters.MessagePointer2, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RFMAILBOX_messagePointer[2], name: "MSGPTR2")
                },
                {(long)RadioMailboxRegisters.MessagePointer3, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out RFMAILBOX_messagePointer[3], name: "MSGPTR3")
                },
                {(long)RadioMailboxRegisters.InterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, out RFMAILBOX_messageInterrupt[0], name: "MBOXIF0")
                    .WithFlag(1, out RFMAILBOX_messageInterrupt[1], name: "MBOXIF1")
                    .WithFlag(2, out RFMAILBOX_messageInterrupt[2], name: "MBOXIF2")
                    .WithFlag(3, out RFMAILBOX_messageInterrupt[3], name: "MBOXIF3")
                    .WithReservedBits(4, 28)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RadioMailboxRegisters.InterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out RFMAILBOX_messageInterruptEnable[0], name: "MBOXIEN0")
                    .WithFlag(1, out RFMAILBOX_messageInterruptEnable[1], name: "MBOXIEN1")
                    .WithFlag(2, out RFMAILBOX_messageInterruptEnable[2], name: "MBOXIEN2")
                    .WithFlag(3, out RFMAILBOX_messageInterruptEnable[3], name: "MBOXIEN3")
                    .WithReservedBits(4, 28)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
            };

            return new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private DoubleWordRegisterCollection BuildCyclicRedundancyCheckRegistersCollection()
        {
            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            {
                {(long)CyclicRedundancyCheckRegisters.IpVersion, new DoubleWordRegister(this, 0x00000000)
                    .WithValueField(0, 32, FieldMode.Read, name: "IPVERSION")
                },
                {(long)CyclicRedundancyCheckRegisters.Enable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "EN")
                    .WithReservedBits(1, 31)
                },
                {(long)CyclicRedundancyCheckRegisters.Control, new DoubleWordRegister(this, 0x00000704)
                    .WithFlag(0, name: "INPUTINV")
                    .WithFlag(1, name: "OUTPUTINV")
                    .WithEnumField<DoubleWordRegister, CRC_CrcWidthMode>(2, 2, out CRC_crcWidthMode, name: "CRCWIDTH")
                    .WithReservedBits(4, 1)
                    .WithFlag(5, name: "INPUTBITORDER")
                    .WithFlag(6, out CRC_reverseCrcByteOrdering, name: "BYTEREVERSE")
                    .WithFlag(7, name: "BITREVERSE")
                    .WithValueField(8, 4, out CRC_crcBitsPerWord, name: "BITSPERWORD")
                    .WithFlag(12, name: "PADCRCINPUT")
                    .WithReservedBits(13, 19)
                },
                {(long)CyclicRedundancyCheckRegisters.Status, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, name: "BUSY")
                    .WithReservedBits(1, 31)
                },
                {(long)CyclicRedundancyCheckRegisters.Command, new DoubleWordRegister(this)
                    .WithFlag(0, name: "INITIALIZE")
                    .WithReservedBits(1, 31)
                },
                {(long)CyclicRedundancyCheckRegisters.InputData, new DoubleWordRegister(this)
                    .WithValueField(0, 16, name: "INPUTDATA")
                    .WithReservedBits(16, 16)
                },
                {(long)CyclicRedundancyCheckRegisters.InitializationValue, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "INIT")
                },
                {(long)CyclicRedundancyCheckRegisters.Data, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, name: "DATA")
                },
                {(long)CyclicRedundancyCheckRegisters.PolynomialValue, new DoubleWordRegister(this)
                    .WithValueField(0, 32, name: "POLY")
                },
            };

            return new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private DoubleWordRegisterCollection BuildSynthesizerRegistersCollection()
        {
            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            {
                {(long)SynthesizerRegisters.IpVersion, new DoubleWordRegister(this, 0x00000002)
                    .WithValueField(0, 32, FieldMode.Read, name: "IPVERSION")
                },
                {(long)SynthesizerRegisters.Enable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "EN")
                    .WithReservedBits(1, 31)
                },
                {(long)SynthesizerRegisters.Frequency, new DoubleWordRegister(this)
                    .WithValueField(0, 28, out SYNTH_frequency, writeCallback: (_, __) => TryAcquireOnAirFrames(), name: "FREQ")
                    .WithReservedBits(28, 4)
                },
                {(long)SynthesizerRegisters.FrequencyDivisionControl, new DoubleWordRegister(this, 0x001)
                    .WithEnumField<DoubleWordRegister, SYNTH_LoDivFreqCtrl>(0, 9, out SYNTH_loDivFreqCtrl, writeCallback: (_, __) => TryAcquireOnAirFrames(), name: "LODIVFREQCTRL")
                    .WithReservedBits(9, 23)
                },
                // RAIL writes the protocol's logical channel index here (e.g. 11..26 for 802.15.4).
                // The simulator exposes that value via LogicalChannel for human-readable log strings.
                {(long)SynthesizerRegisters.ChannelSpacing, new DoubleWordRegister(this)
                    .WithValueField(0, 16, out SYNTH_channelSpacing, name: "CHSP")
                    .WithReservedBits(16, 15)
                },
                {(long)SynthesizerRegisters.Status, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, name: "INLOCK")
                    .WithFlag(1, FieldMode.Read, name: "IFFREQEN")
                    .WithFlag(2, FieldMode.Read, name: "SYNTHREADY")
                    .WithReservedBits(3, 5)
                    .WithFlag(8, FieldMode.Read, name: "CAPCALRUNNING")
                    .WithFlag(9, FieldMode.Read, name: "CAPCALBITRESULT")
                    .WithFlag(10, FieldMode.Read, name: "CAPCALFULLWAIT")
                    .WithReservedBits(11, 3)
                    .WithFlag(14, FieldMode.Read, name: "CAPCALERROR")
                    .WithReservedBits(15, 1)
                    .WithFlag(16, FieldMode.Read, name: "VCOFREQACTIVE")
                    .WithReservedBits(17, 9)
                    .WithFlag(26, FieldMode.Read, name: "CHPALLOWTRISTATE")
                    .WithReservedBits(27, 5)
                },
                {(long)SynthesizerRegisters.Command, new DoubleWordRegister(this)
                    .WithFlag(0, name: "SYNTHSTART")
                    .WithFlag(1, name: "SYNTHSTOP")
                    .WithFlag(2, name: "ENABLEIF")
                    .WithFlag(3, name: "DISABLEIF")
                    .WithFlag(4, name: "CAPCALSTART")
                    .WithReservedBits(5, 4)
                    .WithFlag(9, name: "VCOADDCAP")
                    .WithFlag(10, name: "VCOSUBCAP")
                    .WithReservedBits(11, 21)
                },
                {(long)SynthesizerRegisters.Control, new DoubleWordRegister(this, 0x00000003)
                    .WithValueField(0, 3, name: "LOCKTHRESHOLD")
                    .WithReservedBits(3, 13)
                    .WithValueField(16, 3, name: "PRSMUX0")
                    .WithReservedBits(19, 1)
                    .WithValueField(20, 3, name: "PRSMUX1")
                    .WithFlag(23, name: "DISCLKSYNTH")
                    .WithFlag(24, name: "INVCLKSYNTH")
                    .WithReservedBits(25, 2)
                    .WithFlag(27, name: "TRISTATEPOSTPONE")
                    .WithFlag(28, name: "INTEGERMODE")
                    .WithReservedBits(29, 1)
                    .WithFlag(30, name: "MMDRSTNOVERRIDEEN")
                    .WithFlag(31, name: "MMDMANRSTN")
                },
                {(long)SynthesizerRegisters.CalibrationControl, new DoubleWordRegister(this, 0x00002801)
                    .WithValueField(0, 4, name: "NUMCYCLES")
                    .WithFlag(4, name: "CAPCALSIDE")
                    .WithFlag(5, name: "CAPCALENDMODE")
                    .WithValueField(6, 2, name: "CAPCALCYCLEWAIT")
                    .WithFlag(8, name: "CAPCALWAITMODE")
                    .WithFlag(9, name: "CAPCALSINGLESTEP")
                    .WithValueField(10, 5, name: "STARTUPTIMING")
                    .WithReservedBits(15, 9)
                    .WithFlag(24, name: "SYNTHOVERRIDEEN")
                    .WithFlag(25, name: "CLKLOADDENOMVAL")
                    .WithFlag(26, name: "PRESCALERRESETVAL")
                    .WithFlag(27, name: "COUNTERRESETVAL")
                    .WithReservedBits(28, 4)
                },
                {(long)SynthesizerRegisters.VCDACControl, new DoubleWordRegister(this, 0x00000020)
                    .WithValueField(0, 6, name: "VCDACVAL")
                    .WithFlag(6, name: "VCDACEN")
                    .WithFlag(7, name: "LPFEN")
                    .WithFlag(8, name: "LPFQSEN")
                    .WithReservedBits(9, 23)
                },
                {(long)SynthesizerRegisters.IntermediateFrequency, new DoubleWordRegister(this)
                    .WithValueField(0, 20, name: "IFFREQ")
                    .WithFlag(20, name: "LOSIDE")
                    .WithReservedBits(21, 11)
                },
                {(long)SynthesizerRegisters.ChannelControl, new DoubleWordRegister(this)
                    .WithValueField(0, 6, name: "CHNO")
                    .WithReservedBits(6, 26)
                },
                {(long)SynthesizerRegisters.CalibrationOffset, new DoubleWordRegister(this)
                    .WithValueField(0, 15, name: "CALOFFSET")
                    .WithReservedBits(15, 17)
                },
                {(long)SynthesizerRegisters.VoltageControlledOscillatorFrequencyTuning, new DoubleWordRegister(this, 0x00008400)
                    .WithValueField(0, 11, name: "VCOTUNING")
                    .WithValueField(11, 5, name: "VCAPSEL")
                    .WithReservedBits(16, 16)
                },
                {(long)SynthesizerRegisters.VoltageControlledOscillatorFrequencyRangeControl, new DoubleWordRegister(this)
                    .WithValueField(0, 2, name: "MODE")
                    .WithFlag(2, name: "SWITCHMODE")
                    .WithValueField(3, 3, name: "HIGHTHRESH")
                    .WithValueField(6, 3, name: "LOWTHRESH")
                    .WithFlag(9, name: "OVERRIDEEN")
                    .WithFlag(10, name: "DATA")
                    .WithFlag(11, name: "LOAD")
                    .WithFlag(12, name: "RAMPEN")
                    .WithFlag(13, name: "CMPENABLE")
                    .WithFlag(14, name: "CMPRESET")
                    .WithValueField(15, 3, name: "RAMPRATE")
                    .WithFlag(18, name: "THROUGH")
                    .WithFlag(19, name: "INVCOMPDIR")
                    .WithReservedBits(20, 12)
                },
                {(long)SynthesizerRegisters.VoltageControlledOscillatorGainCalibration, new DoubleWordRegister(this, 0x00000077)
                    .WithValueField(0, 4, name: "VCOKVCOARSE")
                    .WithValueField(4, 4, name: "VCOKVFINE")
                    .WithReservedBits(8, 24)
                },
                {(long)SynthesizerRegisters.ChargePumpDigitalToAnalogConverterControl, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "CHPDACVAL")
                    .WithFlag(8, name: "OVERRIDE")
                    .WithReservedBits(9, 23)
                },
                {(long)SynthesizerRegisters.CapacitorArrayCalibrationCycleCount, new DoubleWordRegister(this)
                    .WithValueField(0, 10, FieldMode.Read, name: "CAPCALCYCLECNT")
                    .WithReservedBits(10, 22)
                },
                {(long)SynthesizerRegisters.VoltageControlledOscillatorForceCalibrationCycleCount, new DoubleWordRegister(this, 0x01FF0FFF)
                    .WithValueField(0, 14, name: "TIMEOUTCOUNTMAX")
                    .WithReservedBits(14, 2)
                    .WithValueField(16, 14, name: "TIMEOUTCOUNTRESTART")
                    .WithReservedBits(30, 2)
                },
                {(long)SynthesizerRegisters.InterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, name: "LOCKED")
                    .WithFlag(1, name: "UNLOCKED")
                    .WithFlag(2, name: "SYRDY")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, name: "VCOHIGH")
                    .WithFlag(5, name: "VCOLOW")
                    .WithReservedBits(6, 3)
                    .WithFlag(9, name: "LOCNTDONE")
                    .WithReservedBits(10, 22)
                },
                {(long)SynthesizerRegisters.InterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "LOCKED")
                    .WithFlag(1, name: "UNLOCKED")
                    .WithFlag(2, name: "SYRDY")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, name: "VCOHIGH")
                    .WithFlag(5, name: "VCOLOW")
                    .WithReservedBits(6, 3)
                    .WithFlag(9, name: "LOCNTDONE")
                    .WithReservedBits(10, 22)
                },
                {(long)SynthesizerRegisters.LoCounterControl, new DoubleWordRegister(this)
                    .WithFlag(0, name: "ENABLE")
                    .WithFlag(1, name: "CLEAR")
                    .WithFlag(2, name: "RUN")
                    .WithFlag(3, name: "READ")
                    .WithValueField(4, 4, name: "NUMCYCLE")
                    .WithFlag(8, name: "LOCNTOVERRIDEEN")
                    .WithFlag(9, name: "LOCNTMANCLEAR")
                    .WithFlag(10, name: "LOCNTMANRUN")
                    .WithFlag(11, name: "FCALRUNCLKEN")
                    .WithReservedBits(12, 20)
                },
                {(long)SynthesizerRegisters.LoCounterStatus, new DoubleWordRegister(this)
                    .WithValueField(0, 19, FieldMode.Read, name: "LOCOUNT")
                    .WithFlag(19, FieldMode.Read, name: "BUSY")
                    .WithReservedBits(20, 12)
                },
                {(long)SynthesizerRegisters.LoCounterTargetValue, new DoubleWordRegister(this)
                    .WithValueField(0, 19, FieldMode.Read, name: "TARGET")
                    .WithReservedBits(19, 13)
                },
                {(long)SynthesizerRegisters.MmdDenominatorInitialValue, new DoubleWordRegister(this)
                    .WithValueField(0, 9, name: "DENOMINIT0")
                    .WithValueField(9, 9, name: "DENOMINIT1")
                    .WithValueField(18, 9, name: "DENOMINIT2")
                    .WithReservedBits(27, 5)
                },
                {(long)SynthesizerRegisters.ChargePumpDigitalToAnalogConverterInitialValue, new DoubleWordRegister(this)
                    .WithValueField(0, 12, name: "DACINIT")
                    .WithReservedBits(12, 20)
                },
                {(long)SynthesizerRegisters.LowPassFilterCalModeControl, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "OP1BWCAL")
                    .WithValueField(4, 4, name: "OP1COMPCAL")
                    .WithValueField(8, 3, name: "RFBVALCAL")
                    .WithValueField(11, 3, name: "RPVALCAL")
                    .WithValueField(14, 4, name: "RZVALCAL")
                    .WithReservedBits(18, 14)
                },
                {(long)SynthesizerRegisters.LowPassFilterRxModeControl1, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "OP1BWRX")
                    .WithValueField(4, 4, name: "OP1COMPRX")
                    .WithValueField(8, 3, name: "RFBVALRX")
                    .WithValueField(11, 3, name: "RPVALRX")
                    .WithValueField(14, 4, name: "RZVALRX")
                    .WithReservedBits(18, 14)
                },
                {(long)SynthesizerRegisters.LowPassFilterTxModeControl1, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "OP1BWTX")
                    .WithValueField(4, 4, name: "OP1COMPTX")
                    .WithValueField(8, 3, name: "RFBVALTX")
                    .WithValueField(11, 3, name: "RPVALTX")
                    .WithValueField(14, 4, name: "RZVALTX")
                    .WithReservedBits(18, 14)
                },
                {(long)SynthesizerRegisters.LowPassFilterRxModeControl2, new DoubleWordRegister(this)
                    .WithFlag(0, name: "LPFSWENRX")
                    .WithValueField(1, 2, name: "LPFINCAPRX")
                    .WithFlag(3, name: "LPFGNDSWENRX")
                    .WithValueField(4, 5, name: "CALCRX")
                    .WithFlag(9, name: "CASELRX")
                    .WithValueField(10, 5, name: "CAVALRX")
                    .WithFlag(15, name: "CFBSELRX")
                    .WithFlag(16, name: "CZSELRX")
                    .WithValueField(17, 8, name: "CZVALRX")
                    .WithFlag(25, name: "MODESELRX")
                    .WithValueField(26, 3, name: "VCMLVLRX")
                    .WithReservedBits(29, 3)
                },
                {(long)SynthesizerRegisters.LowPassFilterTxModeControl2, new DoubleWordRegister(this)
                    .WithFlag(0, name: "LPFSWENTX")
                    .WithValueField(1, 2, name: "LPFINCAPTX")
                    .WithFlag(3, name: "LPFGNDSWENTX")
                    .WithValueField(4, 5, name: "CALCTX")
                    .WithFlag(9, name: "CASELTX")
                    .WithValueField(10, 5, name: "CAVALTX")
                    .WithFlag(15, name: "CFBSELTX")
                    .WithFlag(16, name: "CZSELTX")
                    .WithValueField(17, 8, name: "CZVALTX")
                    .WithFlag(25, name: "MODESELTX")
                    .WithValueField(26, 3, name: "VCMLVLTX")
                    .WithReservedBits(29, 3)
                },
                {(long)SynthesizerRegisters.DsmRxModeControl, new DoubleWordRegister(this, 0x00000013)
                    .WithFlag(0, name: "DITHERDSMINPUTRX")
                    .WithValueField(1, 3, name: "DITHERDSMOUTPUTRX")
                    .WithValueField(4, 4, name: "DITHERDACRX")
                    .WithFlag(8, name: "DSMMODERX")
                    .WithFlag(9, name: "LSBFORCERX")
                    .WithReservedBits(10, 14)
                    .WithFlag(24, name: "DEMMODERX")
                    .WithFlag(25, name: "MASHORDERRX")
                    .WithFlag(26, name: "REQORDERRX")
                    .WithReservedBits(27, 5)
                },
                {(long)SynthesizerRegisters.DsmTxModeControl, new DoubleWordRegister(this, 0x00000013)
                    .WithFlag(0, name: "DITHERDSMINPUTTX")
                    .WithValueField(1, 3, name: "DITHERDSMOUTPUTTX")
                    .WithValueField(4, 4, name: "DITHERDACTX")
                    .WithFlag(8, name: "DSMMODETX")
                    .WithFlag(9, name: "LSBFORCETX")
                    .WithReservedBits(10, 14)
                    .WithFlag(24, name: "DEMMODETX")
                    .WithFlag(25, name: "MASHORDERTX")
                    .WithFlag(26, name: "REQORDERTX")
                    .WithReservedBits(27, 5)
                },
                {(long)SynthesizerRegisters.SequencerInterruptFlags, new DoubleWordRegister(this)
                    .WithFlag(0, name: "LOCKED")
                    .WithFlag(1, name: "UNLOCKED")
                    .WithFlag(2, name: "SYRDY")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, name: "VCOHIGH")
                    .WithFlag(5, name: "VCOLOW")
                    .WithReservedBits(6, 3)
                    .WithFlag(9, name: "LOCNTDONE")
                    .WithReservedBits(10, 22)
                },
                {(long)SynthesizerRegisters.SequencerInterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, name: "LOCKED")
                    .WithFlag(1, name: "UNLOCKED")
                    .WithFlag(2, name: "SYRDY")
                    .WithReservedBits(3, 1)
                    .WithFlag(4, name: "VCOHIGH")
                    .WithFlag(5, name: "VCOLOW")
                    .WithReservedBits(6, 3)
                    .WithFlag(9, name: "LOCNTDONE")
                    .WithReservedBits(10, 22)
                },
            };

            return new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private void TryAcquireOnAirFrames()
        {
            CheckOngoingRx();

            if(RAC_currentRadioState != RAC_RadioState.RxSearch)
            {
                return;
            }

            TrySyncTime();
            foreach(var packet in InterferenceQueue.GetLivePackets(GetTime()))
            {
                if(packet.Sender == this || packet.Channel != Channel)
                {
                    continue;
                }
                TryReceiveFrame(packet.PacketContent, packet.Sender, rxStartedMidPacket: true);
            }
        }

        private void TryReceiveFrame(byte[] frame, IRadio sender, bool rxStartedMidPacket, bool syncTime = true)
        {
            var txStartTime = InterferenceQueue.GetTxStartTime(sender);
            if(txStartTime == TimeInterval.Empty)
            {
                this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "TryReceiveFrame() at {0} on channel {1} ({2}) ({3}): Dropping (TX was aborted)",
                         GetTime(), LogicalChannel, MODEM_GetPhySignature(), SYNTH_GetRfFrequencyString());
                return;
            }

            if(syncTime)
            {
                TrySyncTime();
            }
            var currentTime = GetTime();
            // With independently advancing machine time sources, the frame can be delivered before this
            // receiver has reached the sender's TX timestamp. The medium notifies only once, so retry here:
            // run the reception again when our own time reaches the TX start. If the sender has started
            // another transmission by then, this one is gone and so is the frame.
            if(currentTime < txStartTime)
            {
                machine.LocalTimeSource.ExecuteInSyncedState(_ =>
                {
                    machine.ClockSource.ExecuteInLock(() =>
                    {
                        if(InterferenceQueue.GetTxStartTime(sender) == txStartTime)
                        {
                            TryReceiveFrame(frame, sender, rxStartedMidPacket, syncTime: false);
                        }
                    });
                }, new TimeStamp(txStartTime, machine.LocalTimeSource.Domain));
                return;
            }
            var txRxSimulatorDelayUs = (currentTime - txStartTime).TotalMicroseconds;

            // If we are already receiving a frame, this co-channel transmission interferes with the
            // ongoing reception. Energy-based: any co-channel signal corrupts it, regardless of whether
            // either frame is decode-compatible. We mark the ongoing RX as collided and drop the newcomer.
            if(RAC_internalRxState != RAC_InternalRxState.Idle)
            {
                if(sender == currentFrameSender && txStartTime == currentFrameTxStart)
                {
                    return;
                }
                this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "TryReceiveFrame() at {0} on channel {1} ({2}) ({3}), TX started at {4} (diff: {5})",
                         GetTime(), LogicalChannel, MODEM_GetPhySignature(), SYNTH_GetRfFrequencyString(), txStartTime, txRxSimulatorDelayUs);
                // TODO: for now we always consider this a collision. In the future we will want to take into account
                // the RSSI of both packets and determine if we could hear one of them.
                // The ongoing RX is marked as "interfered", which can result in either a preamble not heard
                // at all or a frame that fails CRC.
                RAC_rxFailReason |= RAC_RxFailReason.Collision;
                this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "Dropping: (RX already ongoing - collision)");
                return;
            }

            this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "TryReceiveFrame() at {0} on channel {1} ({2}) ({3}), TX started at {4} (diff: {5})",
                     GetTime(), LogicalChannel, MODEM_GetPhySignature(), SYNTH_GetRfFrequencyString(), txStartTime, txRxSimulatorDelayUs);

            // The txStartTime guard above already returned if the sender had no over-the-air entry, so a
            // missing signature here is an internal invariant violation rather than a normal drop.
            if(!InterferenceQueue.TryGetTxSignature(sender, out var senderSignature))
            {
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "TryReceiveFrame: no TX signature found for sender although a TX start time was resolved");
            }

            // No reception is in progress. Drop frames whose PHY we cannot decode (the medium only matched
            // carrier frequency). With no RX to corrupt there is nothing to flag as a collision; we simply
            // cannot lock onto a PHY we are not configured for. The sync word is still checked later by the
            // RX pipeline for decode-compatible frames.
            if(!MODEM_GetPhySignature().IsDecodeCompatibleWith(senderSignature))
            {
                this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "Dropping: (incompatible PHY: sender={0}, local={1})",
                         senderSignature, MODEM_GetPhySignature());
                return;
            }

            // Adopt the sender's coding sub-rate for the whole reception so our modelled airtime matches what
            // the transmitter actually spent on air.
            currentFrameCodedSymbolsPerInfoBit = senderSignature.CodedSymbolsPerInfoBit;

            double txChainDelayUs = MODEM_GetTxChainDelayUs();
            double preambleUs = MODEM_GetPreambleOverTheAirTimeUs();
            double syncUs = MODEM_GetSyncWordOverTheAirTimeUs();
            double delayUs = txChainDelayUs + preambleUs + syncUs;

            if(rxStartedMidPacket)
            {
                double elapsedAfterChainUs = Math.Max(0, txRxSimulatorDelayUs - txChainDelayUs);
                double remainingPreambleUs = Math.Max(0, preambleUs - elapsedAfterChainUs);
                double minHeardPreambleUs = preambleUs * MODEM_MinimumHeardPreambleFraction;
                if(remainingPreambleUs < minHeardPreambleUs)
                {
                    this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info,
                             "Dropping (insufficient preamble): at {0} on channel {1} ({2}) remaining={3}us min={4}us",
                             GetTime(), LogicalChannel, SYNTH_GetRfFrequencyString(), remainingPreambleUs, minHeardPreambleUs);
                    currentFrameCodedSymbolsPerInfoBit = 0;
                    return;
                }
            }

            // We first schedule the RX timer to the point that the preamble and sync word are fully transmitted.
            // That must also include the txChainDelay since at the transmitter we don't delay the FrameTxStart event.
            // The RX state is set here so that the payload airtime computed later uses the sender's sub-rate
            // (see MODEM_GetCodedSymbolsPerInfoBit's RX override).
            RAC_internalRxState = RAC_InternalRxState.PreambleAndSyncWord;
            MODEM_demodulatorState.Value = MODEM_DemodulatorState.FrameSearch;

            currentFrame = frame;
            currentFrameSender = sender;
            currentFrameTxStart = txStartTime;
            currentFrameChannel = Channel;

            // We set the RSSIFRAME here to make sure the sender hasn't removed the packet from the interference queue
            // by the time the receiver has completed its RX delay.
            // It is sampled at the TX start, when this frame is certainly on the air, whatever the time at which
            // the frame reached us.
            AGC_FrameRssiIntegerPart = (sbyte)InterferenceQueue.GetCurrentRssi(this, Channel, txStartTime);

            // If another transmission is on air on our channel at any point since this one began, the two
            // overlap in time: mark the reception as collided from the start (energy-based interference).
            if(InterferenceQueue.AnyOtherOnAir(sender, Channel, txStartTime, currentTime))
            {
                RAC_rxFailReason |= RAC_RxFailReason.Collision;
                this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "RX starting with co-channel interference already on air -> collision");
            }

            if(delayUs > txRxSimulatorDelayUs && PROTIMER_UsToPreCntOverflowTicks(delayUs - txRxSimulatorDelayUs) > 0)
            {
                currentFrameElapsedAirtimeUs = 0;
                rxTimer.Frequency = PROTIMER_GetPreCntOverflowFrequency();
                rxTimer.Limit = PROTIMER_UsToPreCntOverflowTicks(delayUs - txRxSimulatorDelayUs);
                rxTimer.Enabled = true;
                this.Log(LogLevel.Noisy, "Schedule rxTimer for PRE/SYNC: {0}us PRE={1} SYNC={2} TXCHAIN={3}",
                         delayUs - txRxSimulatorDelayUs, preambleUs, syncUs, txChainDelayUs);
            }
            else
            {
                // The RX/TX simulator delay already accounted for the intended delay.
                // We store the time we already spent RXing the actual frame and call the RX timer
                // handler directly.
                currentFrameElapsedAirtimeUs = txRxSimulatorDelayUs - delayUs;
                RAC_RxTimerLimitReached();
            }
            FRC_SnifferReceiveFrame(frame);
        }

        private void CheckOngoingRx()
        {
            if(RAC_internalRxState == RAC_InternalRxState.Idle)
            {
                return;
            }

            // There is an ongoing RX: Check if the TX was aborted.
            if(InterferenceQueue.WasTransmissionAborted(currentFrameSender, currentFrameTxStart))
            {
                if(RAC_currentRadioState == RAC_RadioState.RxFrame)
                {
                    // The sender stopped after sync. Keep the RX going for the remaining airtime
                    // and report an RX failure when delivery completes.
                    this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info,
                             "TX aborted after sync at {0} on channel {1} ({2}); RX will fail",
                             GetTime(), LogicalChannel, SYNTH_GetRfFrequencyString());
                    RAC_rxFailReason |= RAC_RxFailReason.TxAborted;
                }
                else
                {
                    this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info,
                             "Dropping (TX was aborted) at {0} on channel {1} ({2})",
                             GetTime(), LogicalChannel, SYNTH_GetRfFrequencyString());
                    RAC_ClearOngoingRx();
                }
                return;
            }

            if(RAC_internalRxState == RAC_InternalRxState.PreambleAndSyncWord && Channel != currentFrameChannel)
            {
                this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info,
                         "Dropping (retuned away before sync) at {0} on channel {1} ({2})",
                         GetTime(), LogicalChannel, SYNTH_GetRfFrequencyString());
                RAC_ClearOngoingRx();
            }
        }

        // TransmitFrame() is invoked as soon as the radio state machine transitions to the TX state.
        private bool TransmitFrame(byte[] frame)
        {
            TrySyncTime();

            if(RAC_internalTxState != RAC_InternalTxState.Idle)
            {
                this.Log(LogLevel.Error, "TransmitFrame(): state not IDLE");
                return false;
            }

            RAC_TxEnable = false;

            if(frame.Length == 0)
            {
                return false;
            }

            // We schedule the TX timer to include the whole frame (including the preamble and SYNC word) plus the 
            // TxChainDelay and TxChainDoneDelay, so that when the timer expires, we can simply complete the TX process.
            // Note, we subtract the TxDoneDelay since that signal occurs BEFORE the last bit of the frame actually went over the air.
            var timerDelayUs = MODEM_GetFrameOverTheAirTimeUs(frame, true, true) + MODEM_GetTxChainDelayUs() - MODEM_GetTxChainDoneDelayUs();

            this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "Sending frame at {0} on channel {1} ({2}): {3}",
                     GetTime(), LogicalChannel, SYNTH_GetRfFrequencyString(), BitConverter.ToString(frame));

            this.Log(LogLevel.Noisy, "TX timer delay (us)={0} (PRECNT overflows={1}) (OTA frame time (us)={2})",
                     timerDelayUs, PROTIMER_UsToPreCntOverflowTicks(timerDelayUs), MODEM_GetFrameOverTheAirTimeUs(frame, true, true));

            InterferenceQueue.Add(this, MODEM_GetPhySignature(), Channel, 0 /*TODO: TxPower*/, frame);

            FRC_SnifferTransmitFrame(frame);
            FrameSent?.Invoke(this, frame);

            MODEM_txPreambleSentInterrupt.Value = true;
            MODEM_seqTxPreambleSentInterrupt.Value = true;
            MODEM_txSyncSentInterrupt.Value |= !MODEM_syncData.Value;
            MODEM_seqTxSyncSentInterrupt.Value = MODEM_txSyncSentInterrupt.Value;

            UpdateInterrupts();

            RAC_internalTxState = RAC_InternalTxState.Tx;
            txTimer.Frequency = PROTIMER_GetPreCntOverflowFrequency();
            txTimer.Limit = PROTIMER_UsToPreCntOverflowTicks(timerDelayUs);
            txTimer.Enabled = true;

            return true;
        }

        private void PROTIMER_ListenBeforeTalkCcaCompleted(bool forceFailure = false)
        {
            if(PROTIMER_listenBeforeTalkState == PROTIMER_ListenBeforeTalkState.Idle)
            {
                this.Log(LogLevel.Error, "PROTIMER_ListenBeforeTalkCcaCompleted while LBT_STATE=idle");
                return;
            }

            if(forceFailure)
            {
                AGC_cca.Value = false;
            }
            else
            {
                // While the radio is receiving a preamble/sync word or a frame the channel is, by definition,
                // busy: real hardware's AGC/RSSI is locked onto that signal. The interference-queue RSSI is
                // reports the channel clear once the sender's over-the-air entry goes stale (the sender marks 
                // it stale at its own TX-end). Because each node runs on an independent time source that only 
                // syncs at quantum boundaries, the sender's entry can go stale while this receiver is still 
                // in the RX process, so the RSSI snapshot reads below the CCA threshold mid-frame. 
                // Gating CCA on the internal Rx state restores the "RX in progress => busy" invariant.
                AGC_cca.Value = (AGC_RssiIntegerPartAdjusted < (sbyte)AGC_ccaThreshold.Value)
                                && (RAC_internalRxState == RAC_InternalRxState.Idle);
            }

            // If the channel is clear, nothing to do here, we let CCADELAY complete.

            // Channel not clear    
            if(!AGC_cca.Value)
            {
                PROTIMER_timeoutCounter[0].Stop();
                PROTIMER_TriggerEvent(PROTIMER_Event.ClearChannelAssessmentMeasurementCompleted);

                // RETRYCNT == RETRYLIMIT?
                if(PROTIMER_listenBeforeTalkRetryCounter.Value == PROTIMER_retryLimit.Value)
                {
                    PROTIMER_listenBeforeTalkState = PROTIMER_ListenBeforeTalkState.Idle;
                    PROTIMER_listenBeforeTalkSync.Value = false;
                    PROTIMER_listenBeforeTalkRunning.Value = false;
                    PROTIMER_listenBeforeTalkFailureInterrupt.Value = true;
                    PROTIMER_seqListenBeforeTalkFailureInterrupt.Value = true;
                    UpdateInterrupts();
                    PROTIMER_TriggerEvent(PROTIMER_Event.ListenBeforeTalkFailure);
                }
                else
                {
                    PROTIMER_listenBeforeTalkRetryInterrupt.Value = true;
                    PROTIMER_seqListenBeforeTalkRetryInterrupt.Value = true;
                    UpdateInterrupts();
                    PROTIMER_TriggerEvent(PROTIMER_Event.ListenBeforeTalkRetry);

                    PROTIMER_listenBeforeTalkState = PROTIMER_ListenBeforeTalkState.Backoff;

                    // RETRYCNT++
                    PROTIMER_listenBeforeTalkRetryCounter.Value += 1;

                    // EXP - min(EXP+1, MAXEXP). With FIXEDBACKOFF, hardware does not update EXP.
                    if(!PROTIMER_fixedBackoff.Value)
                    {
                        if(PROTIMER_listenBeforeTalkExponent.Value + 1 <= PROTIMER_listenBeforeTalkMaxExponent.Value)
                        {
                            PROTIMER_listenBeforeTalkExponent.Value += 1;
                        }
                        else
                        {
                            PROTIMER_listenBeforeTalkExponent.Value = PROTIMER_listenBeforeTalkMaxExponent.Value;
                        }
                    }

                    // BACKOFF = RANDOM & (2^EXP – 1)
                    var rand = PROTIMER_fixedBackoff.Value ? PROTIMER_fixedBackoffRandomValue : (uint)random.Next();
                    var backoff = (rand & ((1u << (byte)PROTIMER_listenBeforeTalkExponent.Value) - 1));

                    // Wait for BACKOFF+1 BASECNTOF events
                    PROTIMER_timeoutCounter[0].CounterTop.Value = backoff;
                    PROTIMER_timeoutCounter[0].Start();
                }
            }
        }

        private void PROTIMER_ListenBeforeTalkStopCommand()
        {
            PROTIMER_timeoutCounter[0].Stop();
            PROTIMER_listenBeforeTalkState = PROTIMER_ListenBeforeTalkState.Idle;
            PROTIMER_listenBeforeTalkSync.Value = false;
            PROTIMER_listenBeforeTalkRunning.Value = false;
        }

        private void PROTIMER_ListenBeforeTalkPauseCommand()
        {
            this.Log(LogLevel.Error, "LBT Pausing not supported");
        }

        private void PROTIMER_ListenBeforeTalkStartCommand()
        {
            if(PROTIMER_timeoutCounter[0].Running.Value || PROTIMER_timeoutCounter[0].Synchronizing.Value)
            {
                PROTIMER_listenBeforeTalkPending = true;
                return;
            }

            PROTIMER_listenBeforeTalkSync.Value = false;
            PROTIMER_listenBeforeTalkRunning.Value = false;
            PROTIMER_listenBeforeTalkPaused.Value = false;
            // EXP = STARTEXP. With FIXEDBACKOFF, hardware does not load EXP from STARTEXP.
            if(!PROTIMER_fixedBackoff.Value)
            {
                PROTIMER_listenBeforeTalkExponent.Value = PROTIMER_listenBeforeTalkStartExponent.Value;
            }
            // RETRYCNT = 0
            PROTIMER_listenBeforeTalkRetryCounter.Value = 0;

            PROTIMER_listenBeforeTalkState = PROTIMER_ListenBeforeTalkState.Backoff;

            if(PROTIMER_timeoutCounter[0].SyncSource.Value == PROTIMER_TimeoutCounterSource.Disabled)
            {
                PROTIMER_listenBeforeTalkRunning.Value = true;
            }
            else
            {
                PROTIMER_listenBeforeTalkSync.Value = true;
            }

            // BACKOFF = RANDOM & (2^EXP – 1)
            var rand = PROTIMER_fixedBackoff.Value ? PROTIMER_fixedBackoffRandomValue : (uint)random.Next();
            var backoff = (rand & ((1u << (byte)PROTIMER_listenBeforeTalkExponent.Value) - 1));

            // Wait for BACKOFF+1 BASECNTOF events
            PROTIMER_timeoutCounter[0].CounterTop.Value = backoff;
            PROTIMER_timeoutCounter[0].Start();
        }

        private void PROTIMER_TimeoutCounter0HandleFinish()
        {
            if(PROTIMER_listenBeforeTalkPending)
            {
                PROTIMER_listenBeforeTalkPending = false;
                PROTIMER_ListenBeforeTalkStartCommand();
            }
        }

        // A compare match requires every comparator enabled in CCn_CTRL to match on the same tick.
        // BASE and WRAP are the low and high words of a single 64-bit compare, not two independent
        // compares: sequencer firmware arms a channel from a 64-bit ptick (PROTIMER_CCTimerStartPtick
        // writes WRAP=ptick>>32, BASE=ptick&0xFFFFFFFF with WRAPMATCHEN|BASEMATCHEN), so matching on
        // either half on its own fires up to a full BASECNT cycle early.
        // PREMATCHEN is not modelled here, see PROTIMER_UpdateCompareTimer.
        private bool PROTIMER_CaptureCompareMatches(uint index)
        {
            var channel = PROTIMER_captureCompareChannel[index];

            if(!channel.Enable.Value || channel.Mode.Value != PROTIMER_CaptureCompareMode.Compare)
            {
                return false;
            }

            // With no comparator enabled there is nothing to match against.
            if(!channel.BaseMatchEnable.Value && !channel.WrapMatchEnable.Value)
            {
                return false;
            }

            if(channel.BaseMatchEnable.Value && channel.BaseValue.Value != PROTIMER_baseCounterValue)
            {
                return false;
            }

            if(channel.WrapMatchEnable.Value && channel.WrapValue.Value != PROTIMER_wrapCounterValue)
            {
                return false;
            }

            return true;
        }

        // Latches the live BASECNT/WRAPCNT into every enabled capture channel whose input source matches the
        // detected sync word.
#pragma warning disable IDE0060
        private void PROTIMER_CaptureOnSyncWordsDetected(bool foundSyncWord0, bool foundSyncWord1, bool foundSyncWord2, bool foundSyncWord3)
#pragma warning restore IDE0060
        {
            for(var i = 0; i < PROTIMER_NumberOfCaptureCompareChannels; ++i)
            {
                var channel = PROTIMER_captureCompareChannel[i];
                if(!channel.Enable.Value || channel.Mode.Value != PROTIMER_CaptureCompareMode.Capture)
                {
                    continue;
                }

                var triggered = false;
                switch(channel.CaptureInputSource.Value)
                {
                case PROTIMER_CaptureInputSource.DemodulatorFoundSyncWord0:
                    triggered = foundSyncWord0;
                    break;
                case PROTIMER_CaptureInputSource.DemodulatorFoundSyncWord1:
                    triggered = foundSyncWord1;
                    break;
                case PROTIMER_CaptureInputSource.DemodulatorFoundSyncWord0or1:
                    triggered = foundSyncWord0 || foundSyncWord1;
                    break;
                }

                if(triggered)
                {
                    channel.Capture(PROTIMER_BaseCounterValue, PROTIMER_WrapCounterValue);
                    PROTIMER_TriggerEvent((PROTIMER_Event)((uint)PROTIMER_Event.CaptureCompareChannel0Event + i));
                }
            }
        }

        // Evaluates the channels whose compare includes BASECNT. Compound BASE+WRAP compares are
        // only evaluated here (and never from PROTIMER_IncrementWrapCounter) because BASECNT is the
        // comparator that advances last: WRAPCNT is a qualifier that has to already hold the target
        // value by the time BASECNT reaches it.
        private void PROTIMER_CheckCaptureCompareBaseMatches()
        {
            for(uint i = 0; i < PROTIMER_NumberOfCaptureCompareChannels; ++i)
            {
                if(PROTIMER_captureCompareChannel[i].BaseMatchEnable.Value && PROTIMER_CaptureCompareMatches(i))
                {
                    PROTIMER_HandleCaptureCompareMatch(i);
                }
            }
        }

        private void PROTIMER_HandleCaptureCompareMatch(uint index)
        {
            PROTIMER_captureCompareChannel[index].InterruptField.Value = true;
            PROTIMER_captureCompareChannel[index].SeqInterruptField.Value = true;
            UpdateInterrupts();
            PROTIMER_TriggerEvent((PROTIMER_Event)((uint)PROTIMER_Event.CaptureCompareChannel0Event + index));
        }

        private void PROTIMER_IncrementBaseCounter(uint increment = 1)
        {
            if(proTimer.Enabled)
            {
                this.Log(LogLevel.Error, "PROTIMER_IncrementBaseCounter invoked while the proTimer running");
                return;
            }

            PROTIMER_baseCounterValue += (ushort)increment;

            // BASECNT == BASECNTTOP is only observable here, before the counter rolls over.
            PROTIMER_CheckCaptureCompareBaseMatches();

            if(PROTIMER_baseCounterValue >= PROTIMER_baseCounterTop.Value)
            {
                PROTIMER_HandleBaseCounterOverflow();
                PROTIMER_baseCounterValue = 0x0;

                // Re-evaluate against the rolled-over BASECNT and the WRAPCNT the overflow advanced,
                // so a target sitting at the start of a wrap is not missed.
                PROTIMER_CheckCaptureCompareBaseMatches();
            }
        }

        private void PROTIMER_TimeoutCounter0HandleSynchronize()
        {
            if(PROTIMER_listenBeforeTalkSync.Value)
            {
                PROTIMER_listenBeforeTalkSync.Value = false;
                PROTIMER_listenBeforeTalkRunning.Value = true;
            }
        }

        private void PROTIMER_UpdateCompareTimer(int index)
        {
            // We don't support preMatch in Compare Timers, instead we checks that preMatch is not enabled, 
            // and if base/wrap match are enabled, we recalculate the protimer limit.
            if(PROTIMER_captureCompareChannel[index].Enable.Value
                && PROTIMER_captureCompareChannel[index].Mode.Value == PROTIMER_CaptureCompareMode.Compare
                && PROTIMER_captureCompareChannel[index].PreMatchEnable.Value)
            {
                this.Log(LogLevel.Error, "CC{0} PRE match enabled, NOT SUPPORTED!", index);
            }

            PROTIMER_HandleChangedParams();
        }

        private void PROTIMER_IncrementWrapCounter(uint increment = 1)
        {
            if(proTimer.Enabled)
            {
                this.Log(LogLevel.Error, "PROTIMER_IncrementWrapCounter invoked while the proTimer running");
                return;
            }

            PROTIMER_wrapCounterValue += increment;

            for(var i = 0; i < PROTIMER_NumberOfCaptureCompareChannels; ++i)
            {
                // Channels that also compare BASECNT are handled by PROTIMER_CheckCaptureCompareBaseMatches().
                if(!PROTIMER_captureCompareChannel[i].BaseMatchEnable.Value
                    && PROTIMER_captureCompareChannel[i].WrapMatchEnable.Value
                    && PROTIMER_CaptureCompareMatches((uint)i))
                {
                    PROTIMER_HandleCaptureCompareMatch((uint)i);
                }
            }

            if(PROTIMER_wrapCounterValue >= PROTIMER_wrapCounterTop.Value)
            {
                PROTIMER_HandleWrapCounterOverflow();
                PROTIMER_wrapCounterValue = 0x0;
            }
        }

        private void PROTIMER_UpdateRxRequestState()
        {
            if(PROTIMER_rxClearEvent1.Value == PROTIMER_Event.Always
                && PROTIMER_rxClearEvent2.Value == PROTIMER_Event.Always)
            {
                PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.Idle;
            }
            else if(PROTIMER_rxSetEvent1.Value == PROTIMER_Event.Always
                     && PROTIMER_rxSetEvent2.Value == PROTIMER_Event.Always)
            {
                PROTIMER_TriggerEvent(PROTIMER_Event.InternalTrigger);
            }
        }

        private void PROTIMER_HandleWrapCounterOverflow()
        {
            //this.Log(LogLevel.Debug, "PROTIMER_HandleWrapCounterOverflow wrapValue={0} at {1}", PROTIMER_WrapCounterValue, GetTime());

            PROTIMER_TriggerEvent(PROTIMER_Event.WrapCounterOverflow);

            Array.ForEach(PROTIMER_timeoutCounter, x => x.Update(PROTIMER_TimeoutCounterSource.WrapCounterOverflow));
        }

        private void PROTIMER_HandleBaseCounterOverflow()
        {
            // this.Log(LogLevel.Debug, "PROTIMER_HandleBaseCounterOverflow baseValue={0} topValue={1} at {2}", 
            //          PROTIMER_BaseCounterValue, PROTIMER_baseCounterTop.Value, GetTime());

            PROTIMER_TriggerEvent(PROTIMER_Event.BaseCounterOverflow);

            if(PROTIMER_wrapCounterSource.Value == PROTIMER_WrapCounterSource.BaseCounterOverflow)
            {
                PROTIMER_IncrementWrapCounter();
            }

            Array.ForEach(PROTIMER_timeoutCounter, x => x.Update(PROTIMER_TimeoutCounterSource.BaseCounterOverflow));
        }

        private void PROTIMER_HandlePreCounterOverflow()
        {
            PROTIMER_TriggerEvent(PROTIMER_Event.PreCounterOverflow);

            if(PROTIMER_baseCounterSource.Value == PROTIMER_BaseCounterSource.PreCounterOverflow)
            {
                PROTIMER_IncrementBaseCounter();
            }
            if(PROTIMER_wrapCounterSource.Value == PROTIMER_WrapCounterSource.PreCounterOverflow)
            {
                PROTIMER_IncrementWrapCounter();
            }

            Array.ForEach(PROTIMER_timeoutCounter, x => x.Update(PROTIMER_TimeoutCounterSource.PreCounterOverflow));

            UpdateInterrupts();
        }

        private void PROTIMER_HandleTimerLimitReached()
        {
            proTimer.Enabled = false;

            // In lightweight mode the timer fires when N PRECNT overflows have occurred. 
            // The number N is set when we start/restart the proTimer

            //this.Log(LogLevel.Debug, "proTimer overflow limit={0} baseTop={1} wrapTop={2}", proTimer.Limit, PROTIMER_baseCounterTop.Value, PROTIMER_wrapCounterTop.Value);

            PROTIMER_HandlePreCntOverflows((uint)proTimer.Limit);

            proTimer.Value = 0;
            proTimer.Limit = PROTIMER_ComputeTimerLimit();
            proTimer.Enabled = true;
        }

        private void PROTIMER_HandlePreCntOverflows(uint overflowCount)
        {
            if(proTimer.Enabled)
            {
                this.Log(LogLevel.Error, "PROTIMER_HandlePreCntOverflows invoked while the proTimer running");
                return;
            }

            // this.Log(LogLevel.Debug, "PROTIMER_HandlePreCntOverflows cnt={0} mask=0x{1:X} base={2}", 
            //          overflowCount, PROTIMER_preCounterSourcedBitmask,
            //          (PROTIMER_preCounterSourcedBitmask & (uint)PROTIMER_PreCountOverflowSourced.BaseCounter));

            if((PROTIMER_preCounterSourcedBitmask & (uint)PROTIMER_PreCountOverflowSourced.BaseCounter) > 0)
            {
                PROTIMER_IncrementBaseCounter(overflowCount);
            }
            if((PROTIMER_preCounterSourcedBitmask & (uint)PROTIMER_PreCountOverflowSourced.WrapCounter) > 0)
            {
                PROTIMER_IncrementWrapCounter(overflowCount);
            }

            for(int i = 0; i < PROTIMER_NumberOfTimeoutCounters; i++)
            {
                if((PROTIMER_preCounterSourcedBitmask & ((uint)PROTIMER_PreCountOverflowSourced.TimeoutCounter0 << i)) > 0)
                {
                    PROTIMER_timeoutCounter[i].Update(PROTIMER_TimeoutCounterSource.PreCounterOverflow, overflowCount);
                }
            }

            // TODO: for now we don't handle CaptureCompare channels being sourced by PreCount
        }

        private uint PROTIMER_ComputeTimerLimit()
        {
            if(proTimer.Enabled)
            {
                this.Log(LogLevel.Error, "PROTIMER_ComputeTimerLimit invoked while the proTimer running");
                return uint.MaxValue;
            }

            uint limit = PROTIMER_DefaultLightWeightTimerLimit;
            PROTIMER_preCounterSourcedBitmask = 0;

            if(PROTIMER_baseCounterSource.Value == PROTIMER_BaseCounterSource.PreCounterOverflow)
            {
                if(PROTIMER_baseCounterValue > PROTIMER_baseCounterTop.Value)
                {
                    this.Log(LogLevel.Error, "BASECNT > BASECNTTOP {0} {1}", PROTIMER_baseCounterValue, PROTIMER_baseCounterTop.Value);
                    return uint.MaxValue;
                }

                uint temp = (uint)PROTIMER_baseCounterTop.Value - PROTIMER_baseCounterValue;
                if(temp != 0 && temp < limit)
                {
                    limit = temp;
                }
                PROTIMER_preCounterSourcedBitmask |= (uint)PROTIMER_PreCountOverflowSourced.BaseCounter;
            }

            if(PROTIMER_wrapCounterSource.Value == PROTIMER_WrapCounterSource.PreCounterOverflow)
            {
                if(PROTIMER_wrapCounterValue > PROTIMER_wrapCounterTop.Value)
                {
                    this.Log(LogLevel.Error, "WRAPCNT > WRAPCNTTOP {0} {1}", PROTIMER_wrapCounterValue, PROTIMER_wrapCounterTop.Value);
                    return uint.MaxValue;
                }

                uint temp = (uint)PROTIMER_wrapCounterTop.Value - PROTIMER_wrapCounterValue;
                if(temp != 0 && temp < limit)
                {
                    limit = temp;
                }
                PROTIMER_preCounterSourcedBitmask |= (uint)PROTIMER_PreCountOverflowSourced.WrapCounter;
            }

            // RENODE-19: for now if a Timeout Timer is active and sourced by PRE overflow, 
            // we switch to a minimum ticks interval.
            // What we really want is to compute the number of PRE overflows to the timeout
            // or the match event.
            for(int i = 0; i < PROTIMER_NumberOfTimeoutCounters; i++)
            {
                if((PROTIMER_timeoutCounter[i].Synchronizing.Value
                     && PROTIMER_timeoutCounter[i].SyncSource.Value == PROTIMER_TimeoutCounterSource.PreCounterOverflow)
                    || (PROTIMER_timeoutCounter[i].Running.Value
                        && PROTIMER_timeoutCounter[i].Source.Value == PROTIMER_TimeoutCounterSource.PreCounterOverflow))
                {
                    limit = PROTIMER_MinimumTimeoutCounterDelay;
                    PROTIMER_preCounterSourcedBitmask |= ((uint)PROTIMER_PreCountOverflowSourced.TimeoutCounter0 << i);
                }
            }

            // Check for Capture/Compare channels that are enabled and set in Compare mode
            for(int i = 0; i < PROTIMER_NumberOfCaptureCompareChannels; i++)
            {
                var channel = PROTIMER_captureCompareChannel[i];

                if(!channel.Enable.Value || channel.Mode.Value != PROTIMER_CaptureCompareMode.Compare)
                {
                    continue;
                }

                var compoundCompare = channel.BaseMatchEnable.Value && channel.WrapMatchEnable.Value;

                // A compound BASE+WRAP compare cannot match before WRAPCNT reaches the target wrap,
                // so while the wrap words differ the BASE target is not an instant of interest. The
                // limit is still bounded by the BASECNT overflow that advances WRAPCNT, which brings
                // us back here once the wrap words match.
                if(compoundCompare && channel.WrapValue.Value != PROTIMER_wrapCounterValue)
                {
                    continue;
                }

                // Base match enabled and base counter is sourced by pre counter overflows
                if(channel.BaseMatchEnable.Value
                    && PROTIMER_baseCounterSource.Value == PROTIMER_BaseCounterSource.PreCounterOverflow
                    && channel.BaseValue.Value > PROTIMER_baseCounterValue)
                {
                    uint temp = (uint)(channel.BaseValue.Value - PROTIMER_baseCounterValue);
                    if(temp < limit)
                    {
                        limit = temp;
                    }
                    PROTIMER_preCounterSourcedBitmask |= ((uint)PROTIMER_PreCountOverflowSourced.CaptureCompareChannel0 << i);
                }

                // Wrap match enabled and wrap counter is sourced by pre counter overflows. For a
                // compound compare the BASE target above already covers the match instant.
                if(channel.WrapMatchEnable.Value && !compoundCompare
                    && PROTIMER_wrapCounterSource.Value == PROTIMER_WrapCounterSource.PreCounterOverflow
                    && channel.WrapValue.Value > PROTIMER_wrapCounterValue)
                {
                    uint temp = (uint)(channel.WrapValue.Value - PROTIMER_wrapCounterValue);
                    if(temp < limit)
                    {
                        limit = temp;
                    }
                    PROTIMER_preCounterSourcedBitmask |= ((uint)PROTIMER_PreCountOverflowSourced.CaptureCompareChannel0 << i);
                }
            }

            return limit;
        }

        private void PROTIMER_TimeoutCounter0HandleUnderflow()
        {
            if(!PROTIMER_listenBeforeTalkRunning.Value)
            {
                return;
            }

            PROTIMER_timeoutCounter[0].Stop();

            switch(PROTIMER_listenBeforeTalkState)
            {
            case PROTIMER_ListenBeforeTalkState.Backoff:
            {
                // CCACNT = 0
                PROTIMER_ccaCounter.Value = 0;

                PROTIMER_listenBeforeTalkState = PROTIMER_ListenBeforeTalkState.CcaDelay;

                // If the RSSI_START command failed to start (for example, radio is not in RX), we don't 
                // start TOUT0 here, we expect to be back in backoff or to be done with retry attempts.
                if(AGC_RssiStartCommand(true))
                {
                    // Wait for CCDELAY+1 BASECNTOF events
                    PROTIMER_timeoutCounter[0].CounterTop.Value = PROTIMER_ccaDelay.Value;
                    PROTIMER_timeoutCounter[0].Start();
                }

                break;
            }
            case PROTIMER_ListenBeforeTalkState.CcaDelay:
            {
                // If we get here is because CCA was successful, otherwise we would have retried the backoff or failed LBT

                // RX in progress => channel busy. The RSSI-based CcaCompleted path already enforces this
                // invariant (gating CCA on RxFrame), but the CCADELAY TOUT0 underflow can race ahead of it.
                if(RAC_internalRxState != RAC_InternalRxState.Idle)
                {
                    PROTIMER_ListenBeforeTalkCcaCompleted(forceFailure: true);
                    break;
                }

                // CCACNT == CCAREPEAT-1
                if(PROTIMER_ccaCounter.Value == (PROTIMER_ccaRepeat.Value - 1))
                {
                    PROTIMER_listenBeforeTalkState = PROTIMER_ListenBeforeTalkState.Idle;
                    PROTIMER_listenBeforeTalkSync.Value = false;
                    PROTIMER_listenBeforeTalkRunning.Value = false;
                    PROTIMER_listenBeforeTalkSuccessInterrupt.Value = true;
                    PROTIMER_seqListenBeforeTalkSuccessInterrupt.Value = true;
                    UpdateInterrupts();
                    PROTIMER_TriggerEvent(PROTIMER_Event.ListenBeforeTalkSuccess);
                    // Trigger the CCA cmpleted event after the LBT success event so that the radio 
                    // does not leave RX and goes directly to RX2TX.
                    PROTIMER_TriggerEvent(PROTIMER_Event.ClearChannelAssessmentMeasurementCompleted);
                }
                else
                {
                    // CCACNT++
                    PROTIMER_ccaCounter.Value += 1;

                    // If the RSSI_START command failed to start (for example, radio is not in RX), we don't 
                    // start TOUT0 here, we expect to be back in backoff or to be done with retry attempts.
                    if(AGC_RssiStartCommand(true))
                    {
                        // Wait for CCDELAY+1 BASECNTOF events
                        PROTIMER_timeoutCounter[0].CounterTop.Value = PROTIMER_ccaDelay.Value;
                        PROTIMER_timeoutCounter[0].Start();
                    }
                }

                break;
            }
            default:
            {
                this.Log(LogLevel.Error, "Unreachable. Invalid LBT state in PROTIMER_TimeoutCounter0HandleUnderflow");
                break;
            }
            }
        }

        private void PROTIMER_UpdateTxRequestState()
        {
            if(PROTIMER_txSetEvent1.Value == PROTIMER_Event.Disabled
                && PROTIMER_txSetEvent2.Value == PROTIMER_Event.Disabled)
            {
                PROTIMER_txRequestState = PROTIMER_TxRxRequestState.Idle;
            }
        }

        private void PROTIMER_RtcSyncStart()
        {
            this.Log(LogLevel.Debug, "PROTIMER RTC Start at {0}, halting PROTIMER and entering RTCWAIT state", GetTime());
            PROTIMER_Enabled = false;
            PROTIMER_rtcWait = true;
        }

        private void PROTIMER_RtcCapture(uint channel)
        {
            if(channel > 1)
            {
                this.Log(LogLevel.Error, "PROTIMER_RtcCapture: invalid channel {0}", channel);
                return;
            }

            for(var i = 0; i < PROTIMER_NumberOfCaptureCompareChannels; ++i)
            {
                if(PROTIMER_captureCompareChannel[i].Enable.Value
                    && PROTIMER_captureCompareChannel[i].Mode.Value == PROTIMER_CaptureCompareMode.Capture
                    && PROTIMER_captureCompareChannel[i].CaptureInputSource.Value == (PROTIMER_CaptureInputSource)((uint)PROTIMER_CaptureInputSource.ProRtcCaptureCompare0 + channel))
                {
                    PROTIMER_captureCompareChannel[i].Capture(PROTIMER_BaseCounterValue, PROTIMER_WrapCounterValue);
                    PROTIMER_TriggerEvent((PROTIMER_Event)((uint)PROTIMER_Event.CaptureCompareChannel0Event + i));
                }
            }
        }

        private uint PROTIMER_GetPreCntOverflowFrequency()
        {
            // The PRECNTTOP value is 1 less than the intended value.
            double frequency = (double)HfxoFrequency / (PROTIMER_preCounterTopInteger.Value + 1 + ((double)PROTIMER_preCounterTopFractional.Value / 65536));
            return Convert.ToUInt32(frequency);
        }

        private ulong PROTIMER_UsToPreCntOverflowTicks(double timeUs)
        {
            return Convert.ToUInt64((timeUs * (double)PROTIMER_GetPreCntOverflowFrequency()) / (double)MicrosecondFrequency);
        }

        private uint MODEM_GetDataRate()
        {
            double numerator = MODEM_txBaudrateNumerator.Value * 1.0;
            double denominator = MODEM_txBaudrateDenominator.Value * 1.0;
            double ratio = numerator / denominator;
            double txBaudrate = (double)HfxoFrequency /  (8.0 * ratio);
            double chipsPerSymbol = MODEM_dsssLength.Value + 1;
            double symbolsPerBit = 1.0;
            uint dsssShiftedSymbols = 0;

            // TODO - If want to add Dynamic rate selection support:
            // Case statement selecting brdiv to be brdiva + 1 or brdivb + 1 from control1 register.
            // The condition to make distinction upon is based on Modem_rateSelectMode also in control1.
            // May need to find a way to identify whether data is header or payload and there is one situation where the brdiv can switch per symbol.
            // In ocelot radio calculator they do not do this:
            // double txBaudrate = (double)HfxoFrequency /  (8.0 * ratio * brdiv);

            // Find shifted symbols
            if(MODEM_dsssShifts.Value == 1)
            {
                // Dsslen will be max 8 due to DSSS setup restrictions
                // A bitslice as is shown in dsssShiftedSymbols = dssslen[CORRINDEX_WIDTH-1:0]; where corrindex_width is 5
                dsssShiftedSymbols = (uint)MODEM_dsssLength.Value & 0x1F;
            }
            else if(MODEM_dsssShifts.Value > 1)
            {
                dsssShiftedSymbols = (uint)MODEM_dsssLength.Value >> (int)(MODEM_dsssShifts.Value - 1);
            }

            if((MODEM_modulationFormat.Value == MODEM_ModulationFormat.FSK4))
            {
                symbolsPerBit = 2.0;
            }
            else if(MODEM_symbolCoding.Value == MODEM_SymbolCoding.Dsss)
            {
                // Convert to bits per symbol
                switch((MODEM_DsssShiftedSymbols)dsssShiftedSymbols)
                {
                case MODEM_DsssShiftedSymbols.ShiftedSymbol0:
                {
                    symbolsPerBit = 1.0;
                    break;
                }
                case MODEM_DsssShiftedSymbols.ShiftedSymbol1:
                {
                    if(MODEM_dsssDoublingMode.Value == MODEM_DsssDoublingMode.Disabled)
                    {
                        symbolsPerBit = 1.0;
                    }
                    else
                    {
                        symbolsPerBit = 2.0;
                    }
                    break;
                }
                case MODEM_DsssShiftedSymbols.ShiftedSymbol3:
                {
                    symbolsPerBit = 2.0;
                    break;
                }
                case MODEM_DsssShiftedSymbols.ShiftedSymbol7:
                {
                    symbolsPerBit = 4.0;
                    break;
                }
                default:
                    this.Log(LogLevel.Error, "MODEM_GetDataRate: default DSSS Shifted Symbol switch case");
                    symbolsPerBit = 1.0;
                    break;
                }
            }
            else
            {
                symbolsPerBit = 1.0;
            }

            double txBitrate = txBaudrate / (chipsPerSymbol / symbolsPerBit);

            this.Log(LogLevel.Noisy, "MODEM_GetDataRate: txBaudrate={0} chipsPerSymbol={1} symbolsPerBit={2} txBitrate={3}",
                     txBaudrate, chipsPerSymbol, symbolsPerBit, txBitrate);

            return (uint)txBitrate;
        }

        // Register-derived PHY signature used for over-the-air sensing/decoding compatibility.
        private PhySignature MODEM_GetPhySignature()
        {
            var coding = MODEM_GetRadioSymbolCoding();
            bool dsss = coding == RadioSymbolCoding.Dsss;
            uint dsssLength = dsss ? (uint)MODEM_dsssLength.Value : 0u;
            uint dsssShifts = dsss ? (uint)MODEM_dsssShifts.Value : 0u;
            uint dsssDoublingMode = dsss ? (uint)MODEM_dsssDoublingMode.Value : 0u;
            return new PhySignature(MODEM_GetModulationFamily(), coding, MODEM_GetDataRate(),
                dsssLength, dsssShifts, dsssDoublingMode, MODEM_GetCodedSymbolsPerInfoBit());
        }

        private RadioSymbolCoding MODEM_GetRadioSymbolCoding()
        {
            switch(MODEM_symbolCoding.Value)
            {
            case MODEM_SymbolCoding.Nrz:
                return RadioSymbolCoding.Nrz;
            case MODEM_SymbolCoding.Manchester:
                return RadioSymbolCoding.Manchester;
            case MODEM_SymbolCoding.Dsss:
                return RadioSymbolCoding.Dsss;
            case MODEM_SymbolCoding.Linecode:
                return RadioSymbolCoding.Linecode;
            default:
                return RadioSymbolCoding.Nrz;
            }
        }

        private RadioModulationFamily MODEM_GetModulationFamily()
        {
            switch(MODEM_modulationFormat.Value)
            {
            case MODEM_ModulationFormat.FSK2:
            case MODEM_ModulationFormat.FSK4:
                return RadioModulationFamily.Fsk;
            case MODEM_ModulationFormat.MSK:
                return RadioModulationFamily.Msk;
            case MODEM_ModulationFormat.OQPSK:
                return RadioModulationFamily.Oqpsk;
            case MODEM_ModulationFormat.BPSK:
            case MODEM_ModulationFormat.DBPSK:
                return RadioModulationFamily.Bpsk;
            case MODEM_ModulationFormat.OOKASK:
            case MODEM_ModulationFormat.MOOK:
                return RadioModulationFamily.Ook;
            default:
                return RadioModulationFamily.Unknown;
            }
        }

        // BLE LE Coded (Long Range) spreads each information bit into C on-air symbols (the FEC convolutional
        // encoder plus the CI-selected pattern mapper): C=8 for 125 kbps (S=8) and C=2 for 500 kbps (S=2).
        // The symbol/baud rate stays at 1 Msym/s so the information rate is baud_rate / C and the on-air
        // duration of the FEC-coded part of the frame is C times longer than the uncoded byte count would suggest.
        // Both sub-rates share an identical MODEM analog config; they are distinguished only by the FRC, which
        // selects the alternate FEC spreading path (CODINGB) for S=2 via FRC.CTRL.RATESELECT (0 => S=8, 2 => S=2).
        private uint MODEM_GetCodedSymbolsPerInfoBit()
        {
            if(MODEM_GetRadioSymbolCoding() != RadioSymbolCoding.Linecode)
            {
                return 1u;
            }
            // While receiving, mirror the coding sub-rate the sender actually used. The receiver's own
            // FRC.CTRL.RATESELECT does not reflect the incoming frame's sub-rate (the receiver only learns
            // it from the on-air CI).
            if(RAC_internalRxState != RAC_InternalRxState.Idle && currentFrameCodedSymbolsPerInfoBit != 0)
            {
                return currentFrameCodedSymbolsPerInfoBit;
            }

            return FRC_rateSelect.Value == FRC_RateSelectCodingB ? 2u : 8u;
        }

        // The passed frame is assumed to NOT include the preamble and to include the SYNC WORD.
        private double MODEM_GetFrameOverTheAirTimeUs(byte[] frame, bool includePreamble, bool includeSyncWord)
        {
            // For the LE Coded PHY the frame bytes (SYNC WORD + PDU + CRC) are FEC-coded, so each information
            // bit occupies C on-air symbols. The preamble is not FEC-coded and stays at the symbol rate.
            uint payloadCodingFactor = MODEM_GetCodedSymbolsPerInfoBit();
            uint syncBits = MODEM_GetSyncWordLengthInBits();
            uint payloadBits = (uint)frame.Length*8 - syncBits;

            double symbols = (double)payloadBits * payloadCodingFactor;

            if(includeSyncWord)
            {
                symbols += MODEM_GetSyncWordOverTheAirTimeUs() * MODEM_GetDataRate() / 1000000.0;
            }

            if(includePreamble)
            {
                symbols += MODEM_GetPreambleOverTheAirTimeUs() * MODEM_GetDataRate() / 1000000.0;
            }

            return symbols * 1000000 / (double)MODEM_GetDataRate();
        }

        private double MODEM_GetByteOverTheAirTimeUs()
        {
            return 8.0 * MODEM_GetCodedSymbolsPerInfoBit() * 1000000 / MODEM_GetDataRate();
        }

        private double MODEM_GetSyncWordOverTheAirTimeUs()
        {
            // LE Coded PHY: the access address (sync word) is part of FEC Block 1, which is ALWAYS coded at
            // S=8, independent of the payload sub-rate (S=2 for 500k, S=8 for 125k).
            uint syncCodingFactor = MODEM_GetRadioSymbolCoding() == RadioSymbolCoding.Linecode
                ? 8u
                : MODEM_GetCodedSymbolsPerInfoBit();
            return (double)MODEM_GetSyncWordLengthInBits() * syncCodingFactor * 1000000 / (double)MODEM_GetDataRate();
        }

        private double MODEM_GetPreambleOverTheAirTimeUs()
        {
            // LE Coded PHY: the preamble is a fixed 80us, sent at the 1 Msym/s base rate and independent
            // of the S=2/S=8 payload sub-rate (BLE spec).
            if(MODEM_GetRadioSymbolCoding() == RadioSymbolCoding.Linecode)
            {
                return MODEM_BleCodedPhyPreambleOverTheAirTimeUs;
            }
            return (double)MODEM_GetPreambleLengthInBits() * 1000000 / (double)MODEM_GetDataRate();
        }

        private double MODEM_GetTxChainDoneDelayUs()
        {
            return ((double)MODEM_GetTxChainDoneDelayNanoS()) / 1000;
        }

        private uint MODEM_GetTxChainDoneDelayNanoS()
        {
            return MODEM_viterbiDemodulatorEnable.Value ? MODEM_BlePhyTxDoneChainDelayNanoS : MODEM_802154PhyTxDoneChainDelayNanoS;
        }

        private uint MODEM_GetTxChainDelayNanoS()
        {
            return MODEM_viterbiDemodulatorEnable.Value ? MODEM_BlePhyTxChainDelayNanoS : MODEM_802154PhyTxChainDelayNanoS;
        }

        private uint MODEM_GetRxDoneDelayNanoS()
        {
            return MODEM_viterbiDemodulatorEnable.Value ? MODEM_BlePhyRxDoneDelayNanoS : MODEM_802154PhyRxDoneDelayNanoS;
        }

        private double MODEM_GetRxChainDelayUs()
        {
            return ((double)MODEM_GetRxChainDelayNanoS()) / 1000;
        }

        private uint MODEM_GetRxChainDelayNanoS()
        {
            return MODEM_viterbiDemodulatorEnable.Value ? MODEM_BlePhyRxChainDelayNanoS : MODEM_802154PhyRxChainDelayNanoS;
        }

        private uint MODEM_GetSyncWordLengthInBits()
        {
            return MODEM_SyncWordLength;
        }

        private uint MODEM_GetPreambleLengthInBits()
        {
            uint preambleLength = (uint)((MODEM_baseBits.Value + 1)*MODEM_txBases.Value);
            return preambleLength;
        }

        private double MODEM_GetRxDoneDelayUs()
        {
            return ((double)MODEM_GetRxDoneDelayNanoS()) / 1000;
        }

        private double MODEM_GetTxChainDelayUs()
        {
            return ((double)MODEM_GetTxChainDelayNanoS()) / 1000;
        }

        private void AGC_UpdateRssiState()
        {
            if(AGC_rssiStartCommandOngoing)
            {
                AGC_rssiState.Value = AGC_RssiState.Command;
            }
            else if(RAC_currentRadioState == RAC_RadioState.RxSearch)
            {
                AGC_rssiState.Value = AGC_RssiState.Period;
            }
            else if(RAC_currentRadioState == RAC_RadioState.RxFrame)
            {
                AGC_rssiState.Value = AGC_RssiState.FameDetection;
            }
            else
            {
                AGC_rssiState.Value = AGC_RssiState.Idle;
            }
        }

        private void AGC_RssiUpdateTimerHandleLimitReached()
        {
            rssiUpdateTimer.Enabled = false;
            AGC_UpdateRssi();
            if(AGC_rssiStartCommandOngoing)
            {
                AGC_rssiDoneInterrupt.Value = true;
                AGC_seqRssiDoneInterrupt.Value = true;

                if(AGC_rssiStartCommandFromProtimer)
                {
                    if(PROTIMER_listenBeforeTalkState != PROTIMER_ListenBeforeTalkState.Idle)
                    {
                        PROTIMER_ListenBeforeTalkCcaCompleted();
                        // Only keep the RSSI timer free-running while the CCA measurement is still
                        // in progress (LBT in CCADELAY). If the channel was busy, CcaCompleted moved
                        // the LBT back to BACKOFF and re-armed the randomized CSMA-CA backoff (TOUT0);
                        // the next CCA must wait for that backoff to expire (driven by
                        // PROTIMER_TimeoutCounter0HandleUnderflow -> AGC_RssiStartCommand). Restarting
                        // the RSSI timer here would re-trigger CCA every RSSI period and bypass the
                        // backoff entirely, collapsing all retries into a few microseconds.
                        if(PROTIMER_listenBeforeTalkState == PROTIMER_ListenBeforeTalkState.CcaDelay)
                        {
                            AGC_RestartRssiTimer();
                        }
                        else
                        {
                            AGC_rssiStartCommandOngoing = false;
                            AGC_rssiStartCommandFromProtimer = false;
                        }
                    }
                    else
                    {
                        AGC_rssiStartCommandOngoing = false;
                        AGC_rssiStartCommandFromProtimer = false;
                    }
                }
                else
                {
                    AGC_rssiStartCommandOngoing = false;
                }

                AGC_UpdateRssiState();
            }
        }

        private void AGC_StopRssiTimer()
        {
            rssiUpdateTimer.Enabled = false;
            AGC_rssiStartCommandOngoing = false;
            AGC_UpdateRssiState();

            if(AGC_rssiStartCommandFromProtimer)
            {
                AGC_rssiStartCommandFromProtimer = false;
                if(PROTIMER_listenBeforeTalkState != PROTIMER_ListenBeforeTalkState.Idle)
                {
                    PROTIMER_ListenBeforeTalkCcaCompleted(true);
                }
            }
        }

        private void AGC_RestartRssiTimer()
        {
            rssiUpdateTimer.Enabled = false;
            rssiUpdateTimer.Value = 0;
            rssiUpdateTimer.Limit = AGC_RssiPeriodUs;
            rssiUpdateTimer.Enabled = true;
        }

        private bool AGC_RssiStartCommand(bool fromProtimer = false)
        {
            if(RAC_currentRadioState != RAC_RadioState.RxSearch && RAC_currentRadioState != RAC_RadioState.RxFrame)
            {
                AGC_RssiIntegerPart = AGC_RssiInvalid;
                if(fromProtimer && PROTIMER_listenBeforeTalkState != PROTIMER_ListenBeforeTalkState.Idle)
                {
                    // Radio is not in RX, fail CCA immediately.
                    PROTIMER_ListenBeforeTalkCcaCompleted(true);
                }
                return false;
            }
            else
            {
                AGC_rssiStartCommandOngoing = true;
                AGC_rssiStartCommandFromProtimer = fromProtimer;
                AGC_UpdateRssiState();
                AGC_RestartRssiTimer();
                return true;
            }
        }

        // Samples the channel at `sampleTime`, or at our own current time if not given.
        private void AGC_UpdateRssi(TimeInterval? sampleTime = null)
        {
            AGC_RssiIntegerPart = (sbyte)InterferenceQueue.GetCurrentRssi(this, Channel, sampleTime ?? GetTime());

            if(AGC_rssiFirstRead)
            {
                AGC_rssiFirstRead = false;
                AGC_rssiValidInterrupt.Value = true;
                AGC_seqRssiValidInterrupt.Value = true;
            }
            if(AGC_RssiIntegerPartAdjusted < (int)AGC_ccaThreshold.Value)
            {
                AGC_ccaInterrupt.Value = true;
                AGC_seqCcaInterrupt.Value = true;
            }
            else
            {
                AGC_ccaNotDetectedInterrupt.Value = true;
                AGC_seqCcaNotDetectedInterrupt.Value = true;
            }

            if(AGC_RssiIntegerPartAdjusted > (int)AGC_rssiHighThreshold.Value)
            {
                AGC_rssiHighInterrupt.Value = true;
                AGC_seqRssiHighInterrupt.Value = true;
            }
            else if(AGC_RssiIntegerPartAdjusted < (int)AGC_rssiLowThreshold.Value)
            {
                AGC_rssiLowInterrupt.Value = true;
                AGC_seqRssiLowInterrupt.Value = true;
            }

            UpdateInterrupts();
        }

        private byte[] CRC_CalculateCRC()
        {
            this.Log(LogLevel.Debug, "CRC mocked with 0x0 bytes.");
            return Enumerable.Repeat<byte>(0x0, (int)CRC_CrcWidth).ToArray();
        }

        // BUFRESTOREFRAMEERROR/BUFRESTORERXABORTED save and restore the active receive buffer
        // selected by ACTIVERXFCD (FCD2 or FCD3).
        private void FRC_RestoreActiveRxDescriptorBufferWriteOffset()
        {
            bufferController.RestoreWriteOffset(FRC_frameDescriptor[FRC_ActiveReceiveFrameDescriptor].BufferIndex);
        }

        // FRC_RXCTRL.BUFCLEAR: the BUFC buffer selected by an RX descriptor is cleared before any frame data is
        // written to it. Both descriptors of the RX pair are cleared, since either can be selected while the
        // frame is disassembled, and this runs before the write offsets are saved so that a BUFRESTORE on a
        // frame error rewinds to the cleared buffer rather than to a stale offset.
        private void FRC_ClearRxDescriptorsBuffers()
        {
            if(!FRC_rxBufferClear.Value)
            {
                return;
            }

            for(var index = FRC_FirstReceiveFrameDescriptor; index < FRC_NumberOfFrameDescriptors; ++index)
            {
                bufferController.Clear(FRC_frameDescriptor[index].BufferIndex);
            }
        }

        private void FRC_SaveActiveRxDescriptorBufferWriteOffset()
        {
            bufferController.UpdateWriteStartOffset(FRC_frameDescriptor[FRC_ActiveReceiveFrameDescriptor].BufferIndex);
        }

        // Descriptor sequencing within a frame, per the FCD modes in the design book: mode 0 always uses the
        // low descriptor of the pair, mode 3 always the high one, mode 1 alternates between them, and mode 2
        // uses the low descriptor for the first subframe and the high one for all the remaining subframes.
        // 'firstDescriptor' selects the pair (FCD0/FCD1 for TX, FCD2/FCD3 for RX) and 'completedSubframes' is
        // the number of subframes already handled in the frame, so passing 0 yields the descriptor a frame
        // starts on.
        private int FRC_SequenceFrameDescriptor(FRC_FrameDescriptorMode mode, int firstDescriptor, int completedSubframes)
        {
            switch(mode)
            {
            case FRC_FrameDescriptorMode.FrameDescriptorMode0:
                return firstDescriptor;
            case FRC_FrameDescriptorMode.FrameDescriptorMode1:
                return firstDescriptor + (completedSubframes % 2);
            case FRC_FrameDescriptorMode.FrameDescriptorMode2:
                return firstDescriptor + ((completedSubframes > 0) ? 1 : 0);
            case FRC_FrameDescriptorMode.FrameDescriptorMode3:
                return firstDescriptor + 1;
            default:
                this.Log(LogLevel.Error, "Invalid FCD mode ({0}).", mode);
                return firstDescriptor;
            }
        }

        // Resets WCNT at the start of a frame. A compare channel programmed to 0 matches immediately, which is
        // the usual setting for LENGTHFIELDLOC when the length field is the first word of the frame.
        private void FRC_ResetWordCounter()
        {
            FRC_wordCounter.Value = 0;
            FRC_UpdateWordCounterCompareInterrupts(0, 0);
        }

        // Advances WCNT by a number of words and raises the compare interrupts of every value the counter
        // reaches on the way. Frame assembly and disassembly move WCNT a subframe or a fragment at a time rather
        // than word by word, so a single advance can cross several compare values.
        private void FRC_AdvanceWordCounter(uint words)
        {
            var previous = (uint)FRC_wordCounter.Value;
            FRC_wordCounter.Value = previous + words;
            FRC_UpdateWordCounterCompareInterrupts(previous + 1, previous + words);
        }

        // The WCNT compare channels hold FRAMELENGTH, LENGTHFIELDLOC and ADDRFIELDLOC, and their flags let
        // software (or the sequencer) act on the corresponding frame boundary: the end of the frame, the arrival
        // of the length field, and the arrival of the address field -- the last one doubling as a "N bytes
        // received" trigger for software frame filtering when hardware address filtering is not used.
        private void FRC_UpdateWordCounterCompareInterrupts(uint firstWordReached, uint lastWordReached, bool includeFrameLengthCompares = true)
        {
            var compareValues = new uint[]
            {
                (uint)FRC_frameLength.Value,
                (uint)FRC_lengthFieldLocation.Value,
                (uint)FRC_addressFieldLocation.Value,
            };

            var raised = false;
            for(var channel = 0; channel < FRC_NumberOfWordCounterCompareChannels; ++channel)
            {
                // WCNTCMP0 compares against FRAMELENGTH. A rejected length does not update that register,
                // so the compare must not fire for a frame whose length was never accepted.
                if(channel == 0 && !includeFrameLengthCompares)
                {
                    continue;
                }
                if(compareValues[channel] < firstWordReached || compareValues[channel] > lastWordReached)
                {
                    continue;
                }

                FRC_wordCounterCompareInterrupt[channel].Value = true;
                FRC_seqWordCounterCompareInterrupt[channel].Value = true;
                raised = true;
            }

            if(raised)
            {
                UpdateInterrupts();
            }
        }

        // FRC_FCDn.EXCLUDESUBFRAMEWCNT keeps a subframe's words out of the word counter, so that FRAMELENGTH and
        // LENGTHFIELDLOC do not count them -- the register description gives the sync word sent as the first
        // subframe as the example. Such a subframe is bounded by WORDS alone, which makes the unlimited WORDS
        // sentinel (0xFF, "the rest of the frame") meaningless: it would never terminate, so it is reported and
        // the subframe is counted normally instead.
        private bool FRC_SubframeExcludedFromWordCounter(FRC_FrameDescriptor descriptor, int index)
        {
            if(!descriptor.ExcludeSubframeFromWcnt.Value)
            {
                return false;
            }

            if(descriptor.UnlimitedWords)
            {
                this.Log(LogLevel.Error, "FCD{0} sets EXCLUDESUBFRAMEWCNT together with an unlimited WORDS, counting the subframe in WCNT instead.", index);
                return false;
            }

            return true;
        }

        // True when frame descriptor 'index' is the dedicated leading sync FCD of the BLE LE Coded layout rather
        // than a payload descriptor: the sync word travels in the data stream (SYNCDATA=1) yet is emitted through
        // the modulator sync path because the FRC generates the access address for the demodulator in RXSEARCH
        // (Linecode), TXFCDMODE=2 (FCD0 once, then FCD1...), and the subframe is CRC-less with a length equal to
        // the sync word.
        private bool FRC_TxLeadingSyncDescriptor(int index)
        {
            return index == 0
                   && MODEM_syncData.Value
                   && MODEM_GetRadioSymbolCoding() == RadioSymbolCoding.Linecode
                   && FRC_txFrameDescriptorMode.Value == FRC_FrameDescriptorMode.FrameDescriptorMode2
                   && !FRC_frameDescriptor[0].IncludeCrc.Value
                   && FRC_frameDescriptor[0].Words == MODEM_SyncWordBytes;
        }

        // True when the leading descriptor of a frame cannot contribute any transmit words of its own, so
        // hardware moves straight on to the next descriptor in the FCD sequence. Two cases occur in practice:
        // the descriptor holds the BLE LE Coded access address, which the modulator sync path emits instead, or
        // the PHY points it at a BUFC buffer the stack never fills because both the payload and the dynamic
        // length field live in the next descriptor's buffer. This only applies while resolving the first
        // descriptor of a frame: an empty buffer encountered once assembly is under way is a real underflow.
        private bool FRC_TxLeadingDescriptorContributesNoData(int index)
        {
            return FRC_TxLeadingSyncDescriptor(index)
                   || bufferController.BytesAvailable(FRC_frameDescriptor[index].BufferIndex) == 0;
        }

        // Advances the active TX descriptor past any leading descriptors that contribute no data, so that both
        // the dynamic frame length decoding and the subframe loop start at the descriptor which actually holds
        // the frame. Bounded by the number of descriptors so a mode that cycles between them cannot spin.
        private void FRC_ResolveLeadingTransmitFrameDescriptor()
        {
            for(var skipped = 0; skipped < FRC_NumberOfFrameDescriptors; ++skipped)
            {
                var current = FRC_ActiveTransmitFrameDescriptor;
                if(!FRC_TxLeadingDescriptorContributesNoData(current))
                {
                    return;
                }

                var next = FRC_SequenceFrameDescriptor(FRC_txFrameDescriptorMode.Value, FRC_FirstTransmitFrameDescriptor, skipped + 1);
                if(next == current)
                {
                    return;
                }

                this.Log(LogLevel.Debug, "FRC assemble: FCD{0} contributes no data, starting the frame at FCD{1}", current, next);
                FRC_ActiveTransmitFrameDescriptor = next;
            }
        }

        // RX counterpart of FRC_TxLeadingSyncDescriptor: the sync word travels in the data stream (SYNCDATA=1)
        // but the Demodulator has already removed it before the FRC sees the frame, so a leading descriptor that
        // only describes that sync word must not consume payload bytes into its buffer. Hardware marks such a
        // subframe by keeping it out of the word counter (FRC_FCDn.EXCLUDESUBFRAMEWCNT names this exact case) or,
        // for BLE LE Coded, by having the FRC generate the access address itself.
        private bool FRC_RxLeadingSyncDescriptor(int index)
        {
            if(index != FRC_FirstReceiveFrameDescriptor
               || FRC_rxFrameDescriptorMode.Value != FRC_FrameDescriptorMode.FrameDescriptorMode2
               || !MODEM_syncData.Value)
            {
                return false;
            }

            var descriptor = FRC_frameDescriptor[index];
            return !descriptor.IncludeCrc.Value
                   && descriptor.Words == MODEM_SyncWordBytes
                   && (descriptor.ExcludeSubframeFromWcnt.Value
                       || MODEM_GetRadioSymbolCoding() == RadioSymbolCoding.Linecode);
        }

        // Advances the active RX descriptor past a leading descriptor that receives nothing, so the first
        // payload bytes are written to the buffer that is meant to hold them. Bounded like the transmit
        // counterpart so a mode that cycles between descriptors cannot spin.
        private void FRC_ResolveLeadingReceiveFrameDescriptor()
        {
            for(var skipped = 0; skipped < FRC_NumberOfFrameDescriptors; ++skipped)
            {
                var current = FRC_ActiveReceiveFrameDescriptor;
                if(!FRC_RxLeadingSyncDescriptor(current))
                {
                    return;
                }

                var next = FRC_SequenceFrameDescriptor(FRC_rxFrameDescriptorMode.Value, FRC_FirstReceiveFrameDescriptor, skipped + 1);
                if(next == current)
                {
                    return;
                }

                this.Log(LogLevel.Debug, "FRC disassemble: FCD{0} receives no data, starting the frame at FCD{1}", current, next);
                FRC_ActiveReceiveFrameDescriptor = next;
            }
        }

        private byte[] FRC_AssembleFrame()
        {
            var frame = Enumerable.Empty<byte>();

            // Per the FRC design the FRC operates on payload data only: insertion of the sync word on the
            // transmit path is owned by the Modulator (and removal on RX by the Demodulator). This model
            // represents that modulator sync insertion by emitting the sync word as a prefix here, and
            // the RX side correspondingly checks/strips MODEM_SyncWordBytes.
            //
            // MODEM_CONTROL1->SYNCDATA selects how the sync reaches the air: SYNCDATA=0 means the modulator
            // inserts it (so it is not in the FRC buffer), while SYNCDATA=1 means it is carried within the data
            // stream. BLE LE Coded ("LongRange" / "Linecode") uses SYNCDATA=1 but is the documented special case:
            // in RXSEARCH the FRC itself generates the access address for the demodulator, so the coded TX PHY 
            // is programmed with a dedicated leading sync FCD ahead of the payload FCD. We still surface the
            // access address through the modulator sync path below, so it is emitted exactly once.
            var prependSync = !MODEM_syncData.Value || MODEM_GetRadioSymbolCoding() == RadioSymbolCoding.Linecode;
            if(prependSync)
            {
                frame = frame.Concat(BitHelper.GetBytesFromValue(MODEM_TxSyncWord, (int)MODEM_SyncWordBytes, reverse: true));
            }

            // The access address of the BLE LE Coded layout lives in a dedicated leading sync FCD, not in the
            // payload, and a PHY may equally point the leading FCD at a buffer the stack never fills. Either way
            // assembly must start at the descriptor that actually holds the frame instead of re-emitting the
            // sync FCD as data or underflowing on an empty buffer. This also makes the dynamic length field and
            // payload read from the payload buffer, matching the hardware WCNT / FRAMELENGTH which span the
            // payload only (DFLOFFSET accounts for the PDU header, not the access address).
            FRC_ResolveLeadingTransmitFrameDescriptor();

            FRC_frameOk = true;
            FRC_frameLengthError = false;
            var descriptor = FRC_frameDescriptor[FRC_ActiveTransmitFrameDescriptor];
            var frameLength = 0u;
            var dynamicFrameLength = true;

            switch(FRC_dynamicFrameLengthMode.Value)
            {
            case FRC_DynamicFrameLengthMode.Disable:
                dynamicFrameLength = false;
                break;
            case FRC_DynamicFrameLengthMode.SingleByte:
            case FRC_DynamicFrameLengthMode.SingleByteMSB:
                frameLength = bufferController.Peek(descriptor.BufferIndex, (uint)FRC_lengthFieldLocation.Value);
                break;
            case FRC_DynamicFrameLengthMode.DualByteLSBFirst:
            case FRC_DynamicFrameLengthMode.DualByteMSBFirst:
                frameLength = ((bufferController.Peek(descriptor.BufferIndex, (uint)FRC_lengthFieldLocation.Value + 1) << 8)
                               | (bufferController.Peek(descriptor.BufferIndex, (uint)FRC_lengthFieldLocation.Value)));
                break;
            default:
                this.Log(LogLevel.Error, "Unimplemented DFL mode.");
                this.Log(LogLevel.Error, "Failed to assemble a frame, partial frame: {0}", BitConverter.ToString(frame.ToArray()));
                return new byte[0];
            }

            if(dynamicFrameLength && !FRC_TrySetFrameLength(frameLength, receiving: false))
            {
                this.Log(LogLevel.Error, "Failed to assemble a frame, partial frame: {0}", BitConverter.ToString(frame.ToArray()));
                return new byte[0];
            }

            FRC_ResetWordCounter();
            for(var subframe = 0; FRC_wordCounter.Value < FRC_FrameLength; ++subframe)
            {
                // Per FRC_FCDn, INCLUDECRC is the bit that appends the calculated CRC after this subframe's
                // data, while CALCCRC only selects whether the subframe feeds the CRC accumulator. A subframe
                // is (WORDS+1) words excluding any CRC it includes, so the CRC is reserved out of the remaining
                // FRAMELENGTH budget rather than out of WORDS, and only when DFLINCLUDECRC says FRAMELENGTH
                // counts the CRC bytes at all.
                var crcLength = descriptor.IncludeCrc.Value ? CRC_CrcWidth : 0;
                var crcCountsInFrameLength = (crcLength > 0) && dynamicFrameLength && FRC_dynamicFrameCrcIncluded.Value;
                var remaining = (uint)(FRC_FrameLength - FRC_wordCounter.Value);
                if(crcCountsInFrameLength && remaining < crcLength)
                {
                    this.Log(LogLevel.Error, "Failed to assemble a frame (CRC would exceed DFL), partial frame: {0}", BitConverter.ToString(frame.ToArray()));
                    return new byte[0];
                }

                var excludeFromWordCounter = FRC_SubframeExcludedFromWordCounter(descriptor, FRC_ActiveTransmitFrameDescriptor);
                // Assemble subframe. An excluded subframe is sized by WORDS alone, since FRAMELENGTH does not
                // account for its words. Otherwise the subframe is bounded by the remaining FRAMELENGTH budget,
                // further clamped to (WORDS+1) unless WORDS is the unlimited sentinel.
                var payloadBudget = crcCountsInFrameLength ? (remaining - crcLength) : remaining;
                var length = excludeFromWordCounter
                             ? descriptor.Words
                             : (descriptor.UnlimitedWords ? payloadBudget : Math.Min(payloadBudget, descriptor.Words));
                if(!bufferController.TryReadBytes(descriptor.BufferIndex, length, out var payload))
                {
                    this.Log(LogLevel.Error, "Failed to assemble a frame, partial frame: {0}", BitConverter.ToString(frame.Concat(payload).ToArray()));
                    FRC_txUnderflowInterrupt.Value = true;
                    FRC_seqTxUnderflowInterrupt.Value = true;
                    FRC_frameOk = false;
                    return new byte[0];
                }
                frame = frame.Concat(payload);
                if(!excludeFromWordCounter)
                {
                    FRC_AdvanceWordCounter(length);
                }

                if(descriptor.IncludeCrc.Value)
                {
                    frame = frame.Concat(CRC_CalculateCRC());
                    if(crcCountsInFrameLength)
                    {
                        FRC_AdvanceWordCounter(crcLength);
                    }
                }

                // Select next frame descriptor
                var nextDescriptor = FRC_SequenceFrameDescriptor(FRC_txFrameDescriptorMode.Value, FRC_FirstTransmitFrameDescriptor, subframe + 1);
                if(excludeFromWordCounter && nextDescriptor == FRC_ActiveTransmitFrameDescriptor)
                {
                    this.Log(LogLevel.Error, "FCD{0} is excluded from WCNT but the FCD mode never advances past it, aborting frame assembly.",
                             FRC_ActiveTransmitFrameDescriptor);
                    return new byte[0];
                }
                FRC_ActiveTransmitFrameDescriptor = nextDescriptor;
                descriptor = FRC_frameDescriptor[FRC_ActiveTransmitFrameDescriptor];
            }

            // Prepare for next frame
            FRC_ActiveTransmitFrameDescriptor = FRC_SequenceFrameDescriptor(FRC_txFrameDescriptorMode.Value, FRC_FirstTransmitFrameDescriptor, 0);

            this.Log(LogLevel.Noisy, "Frame assembled: {0}", BitConverter.ToString(frame.ToArray()));
            return frame.ToArray();
        }

        private void FRC_UpdateRawMode()
        {
            if(!FRC_enableRawDataRandomNumberGenerator.Value || FRC_rxRawBlocked.Value || RAC_currentRadioState != RAC_RadioState.RxSearch)
            {
                return;
            }

            switch(FRC_rxRawDataSelect.Value)
            {
            case FRC_RxRawDataMode.SingleItem:
                if(FRC_rxRawDataTriggerSelect.Value == FRC_RxRawDataTriggerMode.Immediate)
                {
                    FRC_rxRawEventInterrupt.Value = true;
                    FRC_rxRawBlocked.Value = true;
                }
                break;
            default:
                return;
            }
            UpdateInterrupts();
        }

        // Decodes the dynamic frame length: the extracted field is shifted right by DFLSHIFT, masked to DFLBITS
        // bits and then compensated by DFLOFFSET, a signed 4-bit two's complement value. The result must lie
        // within [MINLENGTH, MAXLENGTH]; a negative result is always an error. An invalid length sets
        // STATUS.FRAMELENGTHERROR and discards the frame: FRAMEERROR in RX, TXABORTED in TX.
        private bool FRC_TrySetFrameLength(uint frameLength, bool receiving)
        {
            switch(FRC_dynamicFrameLengthMode.Value)
            {
            case FRC_DynamicFrameLengthMode.SingleByte:
            case FRC_DynamicFrameLengthMode.DualByteLSBFirst:
                break;
            case FRC_DynamicFrameLengthMode.SingleByteMSB:
            case FRC_DynamicFrameLengthMode.DualByteMSBFirst:
                frameLength = (frameLength >> 8) | ((frameLength & 0xFF) << 8);
                break;
            default:
                this.Log(LogLevel.Error, "Invalid DFL mode.");
                return false;
            }
            frameLength >>= (byte)FRC_dynamicFrameLengthBitShift.Value;
            frameLength &= (1u << (byte)FRC_dynamicFrameLengthBits.Value) - 1;

            var offset = (int)FRC_dynamicFrameLengthOffset.Value;
            if(offset >= 8)
            {
                offset -= 16;
            }
            var decodedLength = (long)frameLength + offset;

            FRC_frameLengthError = decodedLength < 0
                                   || decodedLength < (long)FRC_minDecodedLength.Value
                                   || decodedLength > (long)FRC_maxDecodedLength.Value;
            if(FRC_frameLengthError)
            {
                FRC_frameOk = false;
                if(receiving)
                {
                    FRC_frameErrorInterrupt.Value = true;
                    FRC_seqFrameErrorInterrupt.Value = true;
                }
                else
                {
                    FRC_txAbortedInterrupt.Value = true;
                    FRC_seqTxAbortedInterrupt.Value = true;
                }
                UpdateInterrupts();
                return false;
            }

            FRC_frameLength.Value = (ulong)decodedLength;
            return true;
        }

        private void FRC_SnifferReceiveFrame(byte[] frame)
        {
            PtiFrameStart?.Invoke(SiLabs_PacketTraceFrameType.Receive);
            // NOTE the sync word makes up the first 4 bytes of the frame
            int syncWordBytes = Math.Min((int) MODEM_SyncWordBytes, SniffSyncWordSerialLength);
            var syncWord = frame.Take(syncWordBytes).ToArray();
            // NOTE the rest of the frame is the actual data
            var frameData = frame.Skip(syncWordBytes).ToArray();
            if(FRC_ptiEmitState.Value)
            {
                PtiDataOut?.Invoke(new byte[] { (byte)PacketTraceFrameDelimiters.RxStart });
            }
            if(FRC_ptiEmitSyncWord.Value)
            {
                var syncWordPadded = new byte[SniffSyncWordSerialLength];
                Array.Copy(syncWord, syncWordPadded, syncWord.Length);
                PtiDataOut?.Invoke(syncWordPadded);
            }
            if(FRC_ptiEmitRx.Value)
            {
                PtiDataOut?.Invoke(frameData);
            }
            if(FRC_ptiEmitState.Value)
            {
                PtiDataOut?.Invoke(new byte[] { (byte)PacketTraceFrameDelimiters.RxEndSuccess });
            }
            if(FRC_ptiEmitRssi.Value)
            {
                PtiDataOut?.Invoke(new byte[] { (byte)AGC_FrameRssiIntegerPart });
            }
        }

        private void FRC_SnifferTransmitFrame(byte[] frame)
        {
            PtiFrameStart?.Invoke(SiLabs_PacketTraceFrameType.Transmit);
            // NOTE the sync word makes up the first 4 bytes of the frame
            int syncWordBytes = Math.Min((int)MODEM_SyncWordBytes, SniffSyncWordSerialLength);
            var syncWord = frame.Take(syncWordBytes).ToArray();
            // NOTE the rest of the frame is the actual data
            var frameData = frame.Skip(syncWordBytes).ToArray();
            if(FRC_ptiEmitState.Value)
            {
                PtiDataOut?.Invoke(new byte[] { (byte)PacketTraceFrameDelimiters.TxStart });
            }
            if(FRC_ptiEmitSyncWord.Value)
            {
                var syncWordPadded = new byte[SniffSyncWordSerialLength];
                Array.Copy(syncWord, syncWordPadded, syncWord.Length);
                PtiDataOut?.Invoke(syncWordPadded);
            }
            if(FRC_ptiEmitTx.Value)
            {
                PtiDataOut?.Invoke(frameData);
            }
            if(FRC_ptiEmitState.Value)
            {
                PtiDataOut?.Invoke(new byte[] { (byte)PacketTraceFrameDelimiters.TxEndSuccess });
            }
        }

        private bool FRC_CheckSyncWords(byte[] frame)
        {
            if(frame.Length < (int)MODEM_SyncWordBytes)
            {
                return false;
            }
            var syncWord = BitHelper.ToUInt32(frame, 0, (int)MODEM_SyncWordBytes, true);
            var foundSyncWord0 = (syncWord == MODEM_SyncWord0);
            var foundSyncWord1 = (MODEM_dualSync.Value && (syncWord == MODEM_SyncWord1));

            if(!foundSyncWord0 && !foundSyncWord1)
            {
                return false;
            }

            MODEM_rxPreambleDetectedInterrupt.Value = true;
            MODEM_seqRxPreambleDetectedInterrupt.Value = true;

            if(foundSyncWord0)
            {
                PROTIMER_TriggerEvent(PROTIMER_Event.Syncword0Detected);
            }

            if(foundSyncWord1)
            {
                PROTIMER_TriggerEvent(PROTIMER_Event.Syncword1Detected);
            }
            PROTIMER_TriggerEvent(PROTIMER_Event.Syncword0Or1Detected);

            PROTIMER_CaptureOnSyncWordsDetected(foundSyncWord0, foundSyncWord1, false, false);

            MODEM_rxFrameWithSyncWord0DetectedInterrupt.Value |= foundSyncWord0;
            MODEM_seqRxFrameWithSyncWord0DetectedInterrupt.Value = MODEM_rxFrameWithSyncWord0DetectedInterrupt.Value;
            MODEM_rxFrameWithSyncWord1DetectedInterrupt.Value |= foundSyncWord1;
            MODEM_seqRxFrameWithSyncWord1DetectedInterrupt.Value = MODEM_rxFrameWithSyncWord1DetectedInterrupt.Value;
            MODEM_frameDetectedId.Value = !foundSyncWord0 && foundSyncWord1;
            UpdateInterrupts();

            return true;
        }

        // Disassembles a slice of the received frame into the host buffer (BUFC) and the FRC packet-capture
        // buffer. The slice is [startOffset, startOffset + numBytes) within currentFrame (offsets include
        // the leading sync word). The method derives the frame boundaries from currentFrame itself:
        //   - beginning of frame (startOffset at the first MAC byte, i.e. just past the sync word): parse
        //     the dynamic frame length and reset the per-frame disassemble state (WCNT, subframe index);
        //   - end of frame (the slice reaches currentFrame.Length): append the RX trailer and prepare the
        //     descriptors for the next frame.
        // Called once with the whole payload range it reproduces the original one-shot behaviour; called
        // repeatedly with successive ranges it delivers the frame incrementally (BUFC, packet-capture
        // buffer and WCNT advance in lockstep), mirroring how hardware fills the buffers as words arrive.
        private void FRC_DisassembleCurrentFrame(uint startOffset, uint numBytes)
        {
            var dynamicFrameLength = (FRC_dynamicFrameLengthMode.Value != FRC_DynamicFrameLengthMode.Disable);
            var isFrameStart = (startOffset <= MODEM_SyncWordBytes);
            var endOffset = Math.Min(startOffset + numBytes, (uint)currentFrame.Length);
            var isFrameEnd = (endOffset >= (uint)currentFrame.Length);

            if(!isFrameStart && FRC_rxFrameDiscarded)
            {
                // The rest of a discarded frame is still on air but never reaches the buffers.
                currentFrameOffset = endOffset;
                return;
            }

            if(isFrameStart)
            {
                // STATUS.FRAMEOK is set when a frame starts and cleared by any error in it.
                FRC_frameOk = true;
                FRC_frameLengthError = false;
                FRC_rxFrameDiscarded = false;
                FRC_crcErrorTolerated = false;
                var frameLength = 0u;
                switch(FRC_dynamicFrameLengthMode.Value)
                {
                case FRC_DynamicFrameLengthMode.Disable:
                    break;
                case FRC_DynamicFrameLengthMode.SingleByte:
                case FRC_DynamicFrameLengthMode.SingleByteMSB:
                    frameLength = (uint)currentFrame[startOffset + FRC_lengthFieldLocation.Value];
                    break;
                case FRC_DynamicFrameLengthMode.DualByteLSBFirst:
                case FRC_DynamicFrameLengthMode.DualByteMSBFirst:
                    frameLength = (uint)currentFrame[startOffset + FRC_lengthFieldLocation.Value + 1] << 8
                                  | (uint)currentFrame[startOffset + FRC_lengthFieldLocation.Value];
                    break;
                default:
                    this.Log(LogLevel.Error, "Unimplemented DFL mode.");
                    return;
                }
                if(dynamicFrameLength && !FRC_TrySetFrameLength(frameLength, receiving: true))
                {
                    // An invalid length discards the frame: FRAMEERROR is raised instead of RXDONE and none
                    // of the frame is written to the buffers.
                    this.Log(LogLevel.Error, "DisassembleFrame FRAMEERROR");
                    // The length field had to arrive before it could be decoded, so WCNT has already run up to
                    // its last word and the position compares on the way, WCNTCMP1 included, have fired.
                    // FRAMELENGTH was not accepted, so the end-of-frame compare stays clear.
                    var lengthFieldLastWord = (uint)FRC_lengthFieldLocation.Value
                                              + ((FRC_dynamicFrameLengthMode.Value == FRC_DynamicFrameLengthMode.DualByteLSBFirst
                                                  || FRC_dynamicFrameLengthMode.Value == FRC_DynamicFrameLengthMode.DualByteMSBFirst) ? 1u : 0u);
                    FRC_wordCounter.Value = lengthFieldLastWord;
                    FRC_UpdateWordCounterCompareInterrupts(0, lengthFieldLastWord, includeFrameLengthCompares: false);
                    FRC_rxFrameDiscarded = true;
                    currentFrameOffset = endOffset;
                    return;
                }

                FRC_ResetWordCounter();
                currentDisassembleSubFrame = 0;
                currentDisassembleSubFrameStartWord = 0;
                currentDisassembleSubFrameBytes = 0;
                currentFrameOffset = startOffset;

                // The Demodulator has already stripped the sync word from the bytes handed to us, so a leading
                // descriptor that only describes that sync word must not swallow the first payload bytes.
                FRC_ResolveLeadingReceiveFrameDescriptor();
            }

            // Deliver the payload of this slice: from currentFrameOffset up to endOffset, or until the whole
            // frame has been disassembled, whichever comes first. A slice may end in the middle of a subframe;
            // in that case we keep our place (currentDisassembleSubFrameStartWord / currentDisassembleSubFrame
            // persist) and resume on the next call.
            while(FRC_wordCounter.Value < FRC_FrameLength && currentFrameOffset < endOffset)
            {
                var descriptor = FRC_frameDescriptor[FRC_ActiveReceiveFrameDescriptor];

                // Disassemble subframe.
                // The loop is symmetric with FRC_AssembleFrame() on the TX side: INCLUDECRC is the bit that
                // places the CRC after this subframe's data and CALCCRC only feeds the CRC accumulator. A
                // subframe is (WORDS+1) words excluding that CRC, so the CRC bytes are not part of the payload
                // written into the host buffer -- they are read separately below -- and they are reserved out of
                // the remaining FRAMELENGTH budget only when DFLINCLUDECRC says FRAMELENGTH counts them.
                var crcLength = descriptor.IncludeCrc.Value ? CRC_CrcWidth : 0;
                var crcCountsInFrameLength = (crcLength > 0) && dynamicFrameLength && FRC_dynamicFrameCrcIncluded.Value;
                var remaining = FRC_FrameLength - currentDisassembleSubFrameStartWord;
                if(crcCountsInFrameLength && remaining < crcLength)
                {
                    this.Log(LogLevel.Error, "frame too small to contain CRC");
                    return;
                }

                var excludeFromWordCounter = FRC_SubframeExcludedFromWordCounter(descriptor, FRC_ActiveReceiveFrameDescriptor);
                // An excluded subframe is sized by WORDS alone, since FRAMELENGTH does not account for its words.
                // Otherwise it is bounded by the remaining FRAMELENGTH budget, further clamped to (WORDS+1) unless
                // WORDS is the unlimited sentinel, matching FRC_AssembleFrame().
                var payloadBudget = crcCountsInFrameLength ? (remaining - crcLength) : remaining;
                var payloadTotal = excludeFromWordCounter
                                   ? descriptor.Words
                                   : (descriptor.UnlimitedWords ? payloadBudget : Math.Min(payloadBudget, descriptor.Words));
                var bytesToWrite = (uint)Math.Min((long)(payloadTotal - currentDisassembleSubFrameBytes), (long)(endOffset - currentFrameOffset));
                if(currentFrameOffset + bytesToWrite > (uint)currentFrame.Length)
                {
                    this.Log(LogLevel.Error, "frame too small, payload");
                    return;
                }

                if(bytesToWrite > 0)
                {
                    var payload = currentFrame.Skip((int)currentFrameOffset).Take((int)bytesToWrite);
                    var skipCount = (FRC_wordCounter.Value < FRC_packetBufferStartAddress.Value)
                                    ? Math.Min(bytesToWrite, FRC_packetBufferStartAddress.Value - FRC_wordCounter.Value) : 0;
                    var pktCaptureBuff = currentFrame.Skip((int)(currentFrameOffset + skipCount)).Take((int)(bytesToWrite - skipCount));
                    // Position of these captured bytes within the capture window (WCNT - PKTBUFSTARTLOC),
                    // computed before WCNT advances so chunked delivery lands at the right offset.
                    var captureOffset = (FRC_wordCounter.Value + skipCount) - FRC_packetBufferStartAddress.Value;
                    currentFrameOffset += bytesToWrite;
                    if(!excludeFromWordCounter)
                    {
                        FRC_AdvanceWordCounter(bytesToWrite);
                    }
                    currentDisassembleSubFrameBytes += bytesToWrite;

                    FRC_WritePacketCaptureBuffer(pktCaptureBuff.ToArray(), (uint)captureOffset);

                    if(!bufferController.TryWriteBytes(descriptor.BufferIndex, payload.ToArray(), out var written))
                    {
                        this.Log(LogLevel.Error, "Written only {0} bytes from {1}", written, bytesToWrite);
                        if(bufferController.Overflow(descriptor.BufferIndex))
                        {
                            FRC_rxOverflowInterrupt.Value = true;
                            FRC_seqRxOverflowInterrupt.Value = true;
                            UpdateInterrupts();
                            RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxOverflow);
                        }
                    }
                }

                if(currentDisassembleSubFrameBytes < payloadTotal)
                {
                    // The slice ends inside this subframe: resume from here on the next call.
                    break;
                }

                // The subframe payload is fully delivered: read (and optionally store) its CRC.
                if(descriptor.IncludeCrc.Value)
                {
                    var crc = currentFrame.Skip((int)currentFrameOffset).Take((int)CRC_CrcWidth).ToArray();
                    // TODO: Check CRC
                    // FRC_frameErrorInterrupt.Value |= crc check failed
                    currentFrameOffset += (uint)crc.Length;
                    if(crc.Length != CRC_CrcWidth)
                    {
                        this.Log(LogLevel.Error, "frame too small, crc");
                    }
                    if(crcCountsInFrameLength)
                    {
                        FRC_AdvanceWordCounter((uint)crc.Length);
                    }
                    if(FRC_rxStoreCrc.Value)
                    {
                        if(!bufferController.TryWriteBytes(descriptor.BufferIndex, crc, out var written))
                        {
                            this.Log(LogLevel.Error, "Written only {0} bytes from {1}", written, crc.Length);
                            if(bufferController.Overflow(descriptor.BufferIndex))
                            {
                                FRC_rxOverflowInterrupt.Value = true;
                                FRC_seqRxOverflowInterrupt.Value = true;
                                UpdateInterrupts();
                                RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxOverflow);
                            }
                        }
                    }
                }

                // Select next frame descriptor
                var nextDescriptor = FRC_SequenceFrameDescriptor(FRC_rxFrameDescriptorMode.Value, FRC_FirstReceiveFrameDescriptor, currentDisassembleSubFrame + 1);
                if(excludeFromWordCounter && nextDescriptor == FRC_ActiveReceiveFrameDescriptor)
                {
                    this.Log(LogLevel.Error, "FCD{0} is excluded from WCNT but the FCD mode never advances past it, aborting frame disassembly.",
                             FRC_ActiveReceiveFrameDescriptor);
                    return;
                }
                FRC_ActiveReceiveFrameDescriptor = nextDescriptor;
                currentDisassembleSubFrame++;
                currentDisassembleSubFrameStartWord = (uint)FRC_wordCounter.Value;
                currentDisassembleSubFrameBytes = 0;
            }

            if(!isFrameEnd)
            {
                // More of the frame is still to come on a later call: the trailer and descriptor
                // preparation happen only once, at the true end of frame.
                return;
            }

            // RX TRAIL DATA
            {
                // Appended ascending order of field's bit index
                var descriptor = FRC_frameDescriptor[FRC_ActiveReceiveFrameDescriptor];
                if(FRC_rxAppendRssi.Value)
                {
                    // The RSSI appended to the frame is the frame RSSI latched at sync detection, i.e. the same
                    // value AGC_FRAMERSSI.FRAMERSSIINT reports, as a signed two's complement dBm integer.
                    bufferController.WriteData(descriptor.BufferIndex, (byte)AGC_FrameRssiIntegerPartAdjusted);
                }
                if(FRC_rxAppendStatus.Value)
                {
                    // For now we treat all RX failures as CRC errors
                    bufferController.WriteData(descriptor.BufferIndex, (RAC_RxWillFail) ? 0x00U : 0x80U);
                }
                if(FRC_rxAppendProtimerCc0base.Value)
                {
                    bufferController.WriteData(descriptor.BufferIndex, ((uint)PROTIMER_captureCompareChannel[0].BaseValue.Value & 0xFF));
                    bufferController.WriteData(descriptor.BufferIndex, (((uint)PROTIMER_captureCompareChannel[0].BaseValue.Value >> 8) & 0xFF));
                }
                if(FRC_rxAppendProtimerCc0LowWrap.Value)
                {
                    bufferController.WriteData(descriptor.BufferIndex, ((uint)PROTIMER_captureCompareChannel[0].WrapValue.Value & 0xFF));
                    bufferController.WriteData(descriptor.BufferIndex, (((uint)PROTIMER_captureCompareChannel[0].WrapValue.Value >> 8) & 0xFF));
                }
                if(FRC_rxAppendProtimerCc0HighWrap.Value)
                {
                    bufferController.WriteData(descriptor.BufferIndex, (((uint)PROTIMER_captureCompareChannel[0].WrapValue.Value >> 16) & 0xFF));
                    bufferController.WriteData(descriptor.BufferIndex, (((uint)PROTIMER_captureCompareChannel[0].WrapValue.Value >> 24) & 0xFF));
                }
            }

            // Prepare for next frame
            FRC_ActiveReceiveFrameDescriptor = FRC_SequenceFrameDescriptor(FRC_rxFrameDescriptorMode.Value, FRC_FirstReceiveFrameDescriptor, 0);

            this.Log(LogLevel.Noisy, "Frame disassembled, frame descriptor now is {0}", FRC_ActiveReceiveFrameDescriptor);
        }

        private void FRC_CheckPacketCaptureBufferThreshold()
        {
            if(FRC_packetBufferThresholdEnable.Value
                && FRC_packetBufferCount.Value >= FRC_packetBufferThreshold.Value)
            {
                FRC_packetBufferThresholdInterrupt.Value = true;
                FRC_seqPacketBufferThresholdInterrupt.Value = true;
            }
        }

        // Writes data into the FRC packet-capture buffer starting at the given offset (the byte position
        // within the capture window, i.e. WCNT - PKTBUFSTARTLOC). PKTBUFCOUNT tracks the highest captured
        // position so that incremental (chunked) delivery fills the buffer at the correct location instead
        // of always at the start.
        private void FRC_WritePacketCaptureBuffer(byte[] data, uint offset)
        {
            var wroteAny = false;
            for(var i = 0; i < data.Length; i++)
            {
                var position = offset + (uint)i;
                if(position >= FRC_PacketBufferCaptureSize)
                {
                    break;
                }

                FRC_packetBufferCapture[position] = data[i];
                if(position + 1 > FRC_packetBufferCount.Value)
                {
                    FRC_packetBufferCount.Value = position + 1;
                }
                wroteAny = true;

                if(FRC_packetBufferCount.Value == 1)
                {
                    FRC_packetBufferStartInterrupt.Value = true;
                    FRC_seqPacketBufferStartInterrupt.Value = true;
                }
            }

            if(wroteAny)
            {
                // Re-evaluate interrupts as the capture buffer fills so PKTBUFSTART (first byte) and
                // PKTBUFTHRESHOLD (PKTBUFCOUNT reached the configured threshold) are raised promptly,
                // including when the frame is delivered incrementally. UpdateInterrupts() runs
                // FRC_CheckPacketCaptureBufferThreshold(), which sets PKTBUFTHRESHOLDIF/SEQIF.
                UpdateInterrupts();
            }
        }

        private void FRC_CompleteRxAbort()
        {
            // RXABORTINPROGRESS clears when the abort finishes and RXABORTEDIF is set.
            // Clear first so that flag's interrupt sees the status bit already clear.
            // TRACKABFRAME (RENODE-354) can postpone this; that delay is not modeled.
            FRC_rxAbortInProgress = false;
            FRC_rxAbortedInterrupt.Value = true;
            FRC_seqRxAbortedInterrupt.Value = true;
        }

        private void FRC_RxAbortCommand()
        {
            // CMD_RXABORT takes effect when FRC is active. When RAC in RXWARM and RXSEARCH state, 
            // since FRC is still in IDLE state this command doesn't do anything. 
            if(RAC_currentRadioState != RAC_RadioState.RxFrame)
            {
                return;
            }

            // When set, the active receive BUFC buffer is restored for received frames that trigger the RXABORTED interrupt flag. This
            // means that the WRITEOFFSET is restored to the value prior to receiving this frame. READOFFSET is not modified.
            if(FRC_rxBufferRestoreOnRxAborted.Value)
            {
                FRC_RestoreActiveRxDescriptorBufferWriteOffset();
            }

            // RENODE-354
            //if (TRACKABFRAME)
            //{
            // TODO: hold RXABORTINPROGRESS until the aborted frame finishes on air
            //}

            // Set for the abort. TRACKABFRAME (RENODE-354) is not modeled, so the abort
            // finishes in this command and the status bit clears as RXABORTEDIF is set.
            FRC_rxAbortInProgress = true;
            FRC_CompleteRxAbort();
            RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxAbort);
            UpdateInterrupts();
        }

        private void RAC_SeqTimerStart() => RAC_SeqTimerRestart(0, RAC_SeqTimerLimit);

        private void RAC_RxTimerLimitReached()
        {
            rxTimer.Enabled = false;
            CheckOngoingRx();
            if(RAC_internalRxState == RAC_InternalRxState.Idle)
            {
                return;
            }

            // We went through the preamble delay which was scheduled in TryReceiveFrame().
            // We can now check radio state and sync word.
            if(RAC_internalRxState == RAC_InternalRxState.PreambleAndSyncWord)
            {
                if(RAC_currentRadioState != RAC_RadioState.RxSearch || Channel != currentFrameChannel)
                {
                    RAC_ClearOngoingRx();
                    this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "Dropping (not in RXSEARCH): at {0} (channel {1}) ({2}): {3}", GetTime(), LogicalChannel, SYNTH_GetRfFrequencyString(), BitConverter.ToString(currentFrame));
                    return;
                }

                if(RAC_RxWillFail)
                {
                    RAC_ClearOngoingRx();
                    this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "Dropping (PRE/SYNC COLLISION): at {0} (channel {1}) ({2}): {3}", GetTime(), LogicalChannel, SYNTH_GetRfFrequencyString(), BitConverter.ToString(currentFrame));
                    return;
                }

                if(!FRC_CheckSyncWords(currentFrame))
                {
                    RAC_ClearOngoingRx();
                    this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "Dropping (SYNC MISMATCH): at {0} (channel {1}) ({2}): {3}", GetTime(), LogicalChannel, SYNTH_GetRfFrequencyString(), BitConverter.ToString(currentFrame));
                    return;
                }

                // After having transitioned to RX_FRAME as result of the "beginning" of a packet being
                // received, we stay in RX_FRAME for the duration of the transmission. We deliver the frame to
                // BUFC + the FRC capture buffer in fixed-size fragments (FRC_RxFragmentLengthInBytes) spaced by
                // their airtime, approximating the hardware delivering the frame byte-by-byte.
                // The per-byte airtime is measured from the first payload byte: we don't include the preamble
                // and sync word time since we already delayed for them in TryReceiveFrame(). We also account for
                // any extra quantum slack in currentFrameElapsedAirtimeUs).
                RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.FrameDetected);
                RAC_internalRxState = RAC_InternalRxState.Frame;
                FRC_ClearRxDescriptorsBuffers();
                FRC_SaveActiveRxDescriptorBufferWriteOffset();
                currentFrameOffset = MODEM_SyncWordBytes;
                currentFrameByteOverTheAirTimeUs = MODEM_GetByteOverTheAirTimeUs();

                // Arm the timer for the first delivery boundary (the first fragment, or the end of the frame if
                // it fits in a single fragment). Only if the quantum slack already covers the whole frame's
                // airtime do we skip the else branches and fall through to the end-of-frame code below to
                // finish RX immediately (delivering the entire frame at once).
                if(RAC_TryArmRxDeliveryTimer())
                {
                    return;
                }
            }
            else if(RAC_internalRxState == RAC_InternalRxState.Frame)
            {
                // A delivery boundary elapsed. Unless this is the final fragment, deliver one fragment from the
                // current read cursor and arm the timer for the next boundary.
                // currentFrameOffset advances by the bytes consumed (payload plus any CRC bytes read at a
                // subframe boundary); reaching the final fragment falls through to finish below.
                if(((uint)currentFrame.Length - currentFrameOffset) > FRC_RxFragmentLengthInBytes)
                {
                    FRC_DisassembleCurrentFrame(currentFrameOffset, FRC_RxFragmentLengthInBytes);
                }
                if(RAC_TryArmRxDeliveryTimer())
                {
                    return;
                }
            }
            else
            {
                this.Log(LogLevel.Error, "RAC_RxTimerLimitReached: unexpected RX state");
                return;
            }

            // Packet is fully received now and we have already delayed for the RX done chain delay.
            RAC_internalRxState = RAC_InternalRxState.Idle;

            for(var i = 0; i < PROTIMER_NumberOfCaptureCompareChannels; ++i)
            {
                if(PROTIMER_captureCompareChannel[i].Enable.Value && PROTIMER_captureCompareChannel[i].Mode.Value == PROTIMER_CaptureCompareMode.Capture)
                {
                    switch(PROTIMER_captureCompareChannel[i].CaptureInputSource.Value)
                    {
                    case PROTIMER_CaptureInputSource.RxAtEndOfFrameFromDemodulator:
                    case PROTIMER_CaptureInputSource.RxDone:
                    case PROTIMER_CaptureInputSource.TxOrRxDone:
                        PROTIMER_captureCompareChannel[i].Capture(PROTIMER_BaseCounterValue, PROTIMER_WrapCounterValue);
                        PROTIMER_TriggerEvent((PROTIMER_Event)((uint)PROTIMER_Event.CaptureCompareChannel0Event + i));
                        break;
                    }
                }
            }
            PROTIMER_TriggerEvent(PROTIMER_Event.RxDone);
            PROTIMER_TriggerEvent(PROTIMER_Event.TxOrRxDone);

            if(RAC_RxWillFail)
            {
                // The frame on air is not intact (collision or the sender aborted). Finish the reception as a CRC error.
                FRC_frameErrorInterrupt.Value = true;
                FRC_seqFrameErrorInterrupt.Value = true;

                this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "Dropping at {0}: RX fail ({1})", GetTime(), RAC_rxFailReason);

                if(FRC_rxBufferRestoreOnFrameError.Value & !FRC_rxAcceptCrcErrors.Value)
                {
                    FRC_RestoreActiveRxDescriptorBufferWriteOffset();
                }
            }

            // When a CRC error is detected, the FRAMEERROR interrupt flag is set. Normally, frames with CRC errors are discarded. 
            // However, by setting ACCEPTCRCERRORS in FRC_RXCTRL, frames with CRC errors are still accepted and a RXDONE interrupt 
            // is generated at the end of the frame reception, in addition to the FRAMERROR interrupt. 
            if(!RAC_RxWillFail || FRC_rxAcceptCrcErrors.Value)
            {
                this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "Received at {0} on channel {1} ({2}) (RSSI={3}, crcFail={4}): {5}",
                         GetTime(), LogicalChannel, SYNTH_GetRfFrequencyString(), AGC_FrameRssiIntegerPart, RAC_rxFailReason, BitConverter.ToString(currentFrame));

                // No CRC failure, or ACCEPTCRCERRORS is set: disassemble the remaining bytes of the
                // frame and raise the RXDONE interrupt. Earlier slices were already delivered while chunking;
                // currentFrameOffset points at the first byte not yet delivered (it equals MODEM_SyncWordBytes
                // when chunking did not run, so the whole frame is delivered here). Reaching the end of
                // currentFrame also appends the RX trailer, now that the RxDone PROTIMER captures above have run.
                FRC_DisassembleCurrentFrame(currentFrameOffset, (uint)currentFrame.Length - currentFrameOffset);

                if(!FRC_rxFrameDiscarded)
                {
                    FRC_rxDoneInterrupt.Value = true;
                    FRC_seqRxDoneInterrupt.Value = true;
                }
            }

            // The failure outcome is applied last: FRC_DisassembleCurrentFrame is also the frame start when
            // the frame was not delivered in chunks, and it rearms FRAMEOK for the new frame.
            if(RAC_RxWillFail)
            {
                FRC_frameOk = false;
                FRC_crcErrorTolerated = FRC_rxAcceptCrcErrors.Value && !FRC_rxFrameDiscarded;
            }

            // TXAFTERFRAMEPEND only survives a reception that completes successfully, CRC verification
            // included, so a failed or discarded frame does not start the pending transmission.
            if(RAC_RxWillFail || FRC_rxFrameDiscarded)
            {
                RAC_txAfterFramePending.Value = false;
            }

            UpdateInterrupts();
            RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxDone);
        }

        // Arms rxTimer for the next staggered RX delivery boundary, relative to the airtime already elapsed
        // (currentFrameElapsedAirtimeUs). While more than one fragment of payload remains, that boundary is the end
        // of the next fragment; for the final fragment it is the end of the frame, so RXDONE lands at
        // OTA + rxDoneDelay. Returns false (timer not armed) when the target has already elapsed, i.e. RX
        // should finish immediately.
        private bool RAC_TryArmRxDeliveryTimer()
        {
            uint frameEnd = (uint)currentFrame.Length;
            if((frameEnd - currentFrameOffset) > FRC_RxFragmentLengthInBytes)
            {
                double fragmentTargetUs = ((currentFrameOffset + FRC_RxFragmentLengthInBytes) - MODEM_SyncWordBytes) * currentFrameByteOverTheAirTimeUs;
                if(RAC_TryArmRxTimerAt(fragmentTargetUs))
                {
                    return true;
                }
            }
            double endOfFrameUs = MODEM_GetFrameOverTheAirTimeUs(currentFrame, false, false) + MODEM_GetRxDoneDelayUs();
            return RAC_TryArmRxTimerAt(endOfFrameUs);
        }

        private bool RAC_TryArmRxTimerAt(double targetUs)
        {
            double delayUs = targetUs - currentFrameElapsedAirtimeUs;
            if(delayUs > 0 && PROTIMER_UsToPreCntOverflowTicks(delayUs) > 0)
            {
                currentFrameElapsedAirtimeUs = targetUs;
                rxTimer.Frequency = PROTIMER_GetPreCntOverflowFrequency();
                rxTimer.Limit = PROTIMER_UsToPreCntOverflowTicks(delayUs);
                rxTimer.Enabled = true;
                return true;
            }
            return false;
        }

        private void RAC_TxTimerLimitReached()
        {
            txTimer.Enabled = false;

            // Normal TX completion: the whole frame went on air. Retain the queue entry briefly (stale) so a
            // receiver whose deferred TryReceiveFrame runs just after we finished can still resolve us.
            RAC_ClearOngoingTx(transmissionCompleted: true);

            MODEM_txFrameSentInterrupt.Value = true;
            MODEM_seqTxFrameSentInterrupt.Value = true;
            // TODO: how to check if TXAFTERFRAMEDONE IRQ should be set instead, TXAFTERFRAME command cannot be set (TX is instantaneous)
            FRC_txDoneInterrupt.Value = true;
            FRC_seqTxDoneInterrupt.Value = true;
            if(RAC_txAfterFramePending.Value)
            {
                FRC_txAfterFrameDoneInterrupt.Value = true;
                FRC_seqTxAfterFrameDoneInterrupt.Value = true;
            }

            for(var i = 0; i < PROTIMER_NumberOfCaptureCompareChannels; ++i)
            {
                if(PROTIMER_captureCompareChannel[i].Enable.Value && PROTIMER_captureCompareChannel[i].Mode.Value == PROTIMER_CaptureCompareMode.Capture)
                {
                    switch(PROTIMER_captureCompareChannel[i].CaptureInputSource.Value)
                    {
                    case PROTIMER_CaptureInputSource.TxDone:
                    case PROTIMER_CaptureInputSource.TxOrRxDone:
                        PROTIMER_captureCompareChannel[i].Capture(PROTIMER_BaseCounterValue, PROTIMER_WrapCounterValue);
                        PROTIMER_TriggerEvent((PROTIMER_Event)((uint)PROTIMER_Event.CaptureCompareChannel0Event + i));
                        break;
                    }
                }
            }
            PROTIMER_TriggerEvent(PROTIMER_Event.TxDone);
            PROTIMER_TriggerEvent(PROTIMER_Event.TxOrRxDone);

            RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.TxDone);
        }

        private void RAC_HandleTxAfterFrameCommand()
        {
            if(RAC_currentRadioState == RAC_RadioState.Tx || RAC_currentRadioState == RAC_RadioState.RxFrame)
            {
                RAC_txAfterFramePending.Value = true;
            }
        }

        private void RAC_PaRampingTimerHandleLimitReached()
        {
            paRampingTimer.Enabled = false;
            this.Log(LogLevel.Noisy, "PA ramping up/down DONE at {0}", GetTime());
            // Done ramping the PA up or down, set TXRAMPDONE level interrupt back high.
            MODEM_TxRampingDoneInterrupt = true;
            RAC_paRampingDone.Value = RAC_paOutputLevelRamping;
            UpdateInterrupts();
        }

        private void RAC_SeqTimerHandleLimitReached()
        {
            // Handle 16-bit wrap around
            if(seqTimer.Limit == 0xFFFF)
            {
                RAC_SeqTimerRestart(0, RAC_seqTimerLimit);

                // We actually hit the compare value, fire the interrupt.
                if(RAC_seqTimerLimit == 0 || RAC_seqTimerLimit == 0xFFFF)
                {
                    RAC_seqStimerCompareEventInterrupt.Value = true;
                    UpdateInterrupts();
                }
            }
            else
            {
                if(RAC_seqTimerCompareAction.Value == RAC_SeqTimerCompareAction.Continue)
                {
                    RAC_SeqTimerRestart((ushort)seqTimer.Limit, 0xFFFF);
                }
                else if(RAC_seqTimerCompareAction.Value == RAC_SeqTimerCompareAction.Wrap)
                {
                    RAC_SeqTimerRestart(0, RAC_seqTimerLimit);
                }
                else
                {
                    this.Log(LogLevel.Error, "RAC_SeqTimerHandleLimitReached(), invalid compare action mode {0}", RAC_seqTimerCompareAction.Value);
                    return;
                }

                RAC_seqStimerCompareEventInterrupt.Value = true;
                UpdateInterrupts();
            }
        }

        private void RAC_SeqTimerRestart(ushort startValue, ushort limit)
        {
            // Little hack: when the limit is set to 0, we actually set to 0xFFFF.
            // STimer triggers on value transition, so it the event will fire when a full wrap around occurs.
            // In practice, by setting the limit to 0xFFFF, it will expire one tick earlier.
            if(limit == 0)
            {
                limit = 0xFFFF;
            }

            seqTimer.Enabled = false;

            seqTimer.Frequency = HfxoFrequency;
            seqTimer.Divider = RAC_seqTimerPrescaler.Value + 1;
            seqTimer.Limit = limit;
            seqTimer.Enabled = true;
            seqTimer.Value = startValue;
        }

        private void RAC_ClearOngoingTx(bool transmissionCompleted = false)
        {
            // Retires our InterferenceQueue entry, which other radios read: keep it under the radio lock.
            machine.ClockSource.ExecuteInLock(delegate
            {
                txTimer.Enabled = false;
                if(RAC_internalTxState != RAC_InternalTxState.Idle)
                {
                    InterferenceQueue.Remove(this, transmissionCompleted);
                }
                RAC_internalTxState = RAC_InternalTxState.Idle;
            });
        }

        // The sequencer clears STATERXWARMIF when its own waits finish. The
        // LO-counter calibration it polls between SYNTHSTARTREQ and that
        // clear is unmodeled, so the clear comes well before the RX front
        // end has settled and a search window armed at RXSEARCH entry can
        // expire during the preamble. Hold the transition until the measured
        // settle time has elapsed. Returns true when entry was deferred.
        private bool RAC_DeferRxSearchForSynthSettle()
        {
            if(!SYNTH_startValid)
            {
                return false;
            }
            double elapsedUs = (GetTime() - SYNTH_startTime).TotalMicroseconds;
            double remainingUs = MinimumRxSearchDelayAfterSynthStartUs - elapsedUs;
            if(remainingUs <= 1.0)
            {
                return false;
            }
            rxWarmSettleTimer.Enabled = false;
            rxWarmSettleTimer.Limit = (ulong)Math.Ceiling(remainingUs);
            rxWarmSettleTimer.Enabled = true;
            rxWarmSettleTimer.Value = 0;
            return true;
        }

        private void RAC_RxWarmSettleTimerLimitReached()
        {
            rxWarmSettleTimer.Enabled = false;
            if(RAC_currentRadioState == RAC_RadioState.RxWarm && !RAC_seqStateRxWarmInterrupt.Value)
            {
                RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.RxWarmIrqCleared);
            }
        }

        private void RAC_ChangeRadioState(RAC_RadioState newState)
        {
            if(newState != RAC_currentRadioState)
            {
                RAC_previous3RadioState = RAC_previous2RadioState;
                RAC_previous2RadioState = RAC_previous1RadioState;
                RAC_previous1RadioState = RAC_currentRadioState;
                RAC_currentRadioState = newState;
                RAC_UpdateEm1pRequest();
            }
        }

        private void RAC_UpdateEm1pRequest()
        {
            Emu?.SetEm1pRequest(this, RAC_Em1pRequest);
            Emu?.SetEm1pHostClockRequest(this, RAC_Em1pHostClockRequest);
        }

        // CPUWAITDIS releases the core at the vector table in sequencer RAM. The same path restarts
        // it after EM2, once software sets the bit again.
        private void RAC_ReleaseSequencer()
        {
            if(!sequencer.CpuWaitSignal.IsSet)
            {
                return;
            }
            if(RAC_sequencerResetPending)
            {
                // The EM2 reset has not run yet. Release the core right after it.
                machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => RAC_ReleaseSequencer());
                return;
            }
            sequencer.VectorTableOffset = SequencerMemoryBaseAddress;
            sequencer.SP = machine.SystemBus.ReadDoubleWord(SequencerMemoryBaseAddress);
            sequencer.PC = machine.SystemBus.ReadDoubleWord(SequencerMemoryBaseAddress + 0x4);
            RAC_SeqTimerStart();
            sequencer.CpuWaitSignal.Set(false);
            this.Log(LogLevel.Noisy, "Sequencer resumed, isHalted={0} VTOR={1:X} SP={2:X} PC={3:X} CPUWAIT={4}.", sequencer.IsHalted, sequencer.VectorTableOffset, sequencer.SP, sequencer.PC, sequencer.CpuWaitSignal.IsSet);
        }

        // EM2 powers PD1 down: its retained state survives, but non-retained flops are reset and the radio
        // is inactive. The core stays in CPUWAIT afterwards, until software sets CPUWAITDIS. The enter hook
        // can run on a CPU thread (the host's or the sequencer's), where a core cannot be reset, so the
        // reset runs at the next synced state, when no CPU is executing.
        private void RAC_PowerDownSequencer()
        {
            if(sequencer.CpuWaitSignal.IsSet)
            {
                return;
            }
            RAC_sequencerResetPending = true;
            sequencer.CpuWaitSignal.Set(true);
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => RAC_ResetSequencer());
        }

        private void RAC_ResetSequencer()
        {
            if(!RAC_sequencerResetPending)
            {
                return;
            }
            RAC_sequencerResetPending = false;
            sequencer.Reset();
            // Reset leaves the core with a pause request. Clear it, so the core is just halted by CPUWAIT.
            sequencer.Resume();
            RAC_sequencerInWfi = false;
            RAC_UpdateEm1pRequest();
        }

        private void RAC_ClearOngoingRx()
        {
            FRC_rxFrameIrqClearedPending = false;
            FRC_rxDonePending = false;
            rxTimer.Enabled = false;
            RAC_rxFailReason = RAC_RxFailReason.None;
            RAC_internalRxState = RAC_InternalRxState.Idle;
            currentFrameSender = null;
            currentFrameTxStart = TimeInterval.Empty;
            currentFrameChannel = 0;
            currentFrameCodedSymbolsPerInfoBit = 0;
        }

        // Returns true while the current radio state is a sequencer-serviced transitional state
        // whose STATExxxIF has not yet been cleared.
        private bool RAC_TransitionalStateSeqIfPending()
        {
            switch(RAC_currentRadioState)
            {
            case RAC_RadioState.RxWarm:
                return RAC_seqStateRxWarmInterrupt.Value;
            case RAC_RadioState.Rx2Rx:
                return RAC_seqStateRx2RxInterrupt.Value;
            case RAC_RadioState.Rx2Tx:
                return RAC_seqStateRx2TxInterrupt.Value;
            case RAC_RadioState.RxPoweringDown:
                return RAC_seqStateRxPoweringDownInterrupt.Value;
            case RAC_RadioState.TxWarm:
                return RAC_seqStateTxWarmInterrupt.Value;
            case RAC_RadioState.Tx2Rx:
                return RAC_seqStateTx2RxInterrupt.Value;
            case RAC_RadioState.Tx2Tx:
                return RAC_seqStateTx2TxInterrupt.Value;
            case RAC_RadioState.TxPoweringDown:
                return RAC_seqStateTxPoweringDownInterrupt.Value;
            default:
                return false;
            }
        }

        private void RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal signal = RAC_RadioStateMachineSignal.None)
        {
            //this.Log(LogLevel.Debug, "RAC_UpdateRadioStateMachine signal={0} current state={1} at {2}", signal, RAC_currentRadioState, GetTime());

            machine.ClockSource.ExecuteInLock(delegate
            {
                var previousState = RAC_currentRadioState;

                // Super State Transition Priority
                // 1. RESET
                // 2. Sequencer breakpoint triggered
                // 3. FORCESTATE
                // 4. FORCEDISABLE
                // 5. FORCETX
                if(signal == RAC_RadioStateMachineSignal.Reset)
                {
                    RAC_ClearOngoingTxOrRx();
                    // This should go through PowerOnReset->Shutdown but there is really no difference in going to Shutdown directly.
                    RAC_ChangeRadioState(RAC_RadioState.Shutdown);
                }
                //else if (sequencer breakpoint triggered)
                //{
                // When a sequencer breakpoint is triggered, the RSM will not change state. This allows debugging and
                // single stepping to be performed, without state transitions changing the sequencer program flow.
                //}
                else if(RAC_forceStateActive.Value)
                {
                    RAC_ClearOngoingTxOrRx();
                    RAC_ChangeRadioState(RAC_forceStateTransition.Value);
                    RAC_forceStateActive.Value = false;
                }
                else if(RAC_forceDisable.Value && RAC_currentRadioState == RAC_RadioState.Off)
                {
                    // The RSM remains in OFF as long as the FORCEDISABLE bit is set.
                }
                else if(RAC_forceDisable.Value && RAC_currentRadioState != RAC_RadioState.Shutdown)
                {
                    // Like every normal state-exit in this RSM, the FORCEDISABLE-driven shutdown must
                    // wait for the sequencer to clear a transitional state's handshake before tearing 
                    // the radio down.
                    if(!RAC_TransitionalStateSeqIfPending())
                    {
                        RAC_ClearOngoingTxOrRx();
                        RAC_ChangeRadioState(RAC_RadioState.Shutdown);
                    }
                }
                // FORCETX will make the RSM enter TX. If already in TX, TX is entered through TX2TX. For any other state, 
                // the transition goes through the TXWARM state. The FORCETX is active by issuing the FORCETX command in RAC_CMD 
                // or by triggering by Peripheral Reflex System (PRS). PRS triggering is configured by setting the PRSFORCETX 
                // and setting the PRSFORCETXSEL, both in RAC_CTRL.
                else if(signal == RAC_RadioStateMachineSignal.ForceTx /* TODO: || (PRSFORCETX && PRSFORCETXSEL)*/)
                {
                    if(RAC_currentRadioState == RAC_RadioState.Tx)
                    {
                        RAC_ClearOngoingTx();
                        RAC_ChangeRadioState(RAC_RadioState.Tx2Tx);
                    }
                    else
                    {
                        RAC_ChangeRadioState(RAC_RadioState.TxWarm);
                    }
                }
                else
                {
                    switch(RAC_currentRadioState)
                    {
                    case RAC_RadioState.Shutdown:
                    {
                        // Like every normal state-exit in this RSM, the Shutdown->Off transition must wait
                        // for the sequencer to service (clear) its STATESHUTDOWN handshake.
                        if(!RAC_exitShutdownDisable.Value
                           && !(RAC_seqStateShutDownInterruptEnable.Value && RAC_seqStateShutDownInterrupt.Value))
                        {
                            RAC_ChangeRadioState(RAC_RadioState.Off);
                        }
                        break;
                    }
                    case RAC_RadioState.Off:
                    {
                        if(signal == RAC_RadioStateMachineSignal.TxEnable)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.TxWarm);
                        }
                        else if(RAC_RxEnable)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.RxWarm);
                        }
                        break;
                    }
                    case RAC_RadioState.TxWarm:
                    {
                        if(signal == RAC_RadioStateMachineSignal.TxWarmIrqCleared)
                        {
                            if(RAC_TxEnable)
                            {
                                RAC_ChangeRadioState(RAC_RadioState.Tx);
                            }
                            else
                            {
                                RAC_ChangeRadioState(RAC_RadioState.TxPoweringDown);
                            }
                        }
                        break;
                    }
                    case RAC_RadioState.RxWarm:
                    {
                        if(signal == RAC_RadioStateMachineSignal.RxWarmIrqCleared)
                        {
                            if(!RAC_DeferRxSearchForSynthSettle())
                            {
                                RAC_ChangeRadioState(RAC_RadioState.RxSearch);
                            }
                        }
                        break;
                    }
                    case RAC_RadioState.RxPoweringDown:
                    {
                        if(signal == RAC_RadioStateMachineSignal.RxPowerDownIrqCleared)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.Off);
                        }
                        break;
                    }
                    case RAC_RadioState.TxPoweringDown:
                    {
                        if(signal == RAC_RadioStateMachineSignal.TxPowerDownIrqCleared)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.Off);
                        }
                        break;
                    }
                    case RAC_RadioState.RxOverflow:
                    {
                        if(signal == RAC_RadioStateMachineSignal.ClearRxOverflow)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.Off);
                        }
                        break;
                    }
                    case RAC_RadioState.Rx2Rx:
                    {
                        if(signal == RAC_RadioStateMachineSignal.Rx2RxIrqCleared)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.RxSearch);
                        }
                        break;
                    }
                    case RAC_RadioState.Rx2Tx:
                    {
                        if(signal == RAC_RadioStateMachineSignal.RxOverflow)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.RxOverflow);
                        }
                        else if(signal == RAC_RadioStateMachineSignal.Rx2TxIrqCleared)
                        {
                            if(RAC_TxEnable || RAC_txAfterFrameActive.Value)
                            {
                                RAC_ChangeRadioState(RAC_RadioState.Tx);
                            }
                            else if(RAC_RxEnable)
                            {
                                RAC_ChangeRadioState(RAC_RadioState.Tx2Rx);
                            }
                            else
                            {
                                RAC_ChangeRadioState(RAC_RadioState.TxPoweringDown);
                            }
                        }
                        break;
                    }
                    case RAC_RadioState.Tx2Rx:
                    {
                        if(signal == RAC_RadioStateMachineSignal.Tx2RxIrqCleared)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.RxSearch);
                        }
                        break;
                    }
                    case RAC_RadioState.Tx2Tx:
                    {
                        if(signal == RAC_RadioStateMachineSignal.Tx2TxIrqCleared)
                        {
                            if(RAC_TxEnable)
                            {
                                RAC_ChangeRadioState(RAC_RadioState.Tx);
                            }
                            else if(RAC_RxEnable)
                            {
                                RAC_ChangeRadioState(RAC_RadioState.Tx2Rx);
                            }
                            else
                            {
                                RAC_ChangeRadioState(RAC_RadioState.TxPoweringDown);
                            }
                        }
                        break;
                    }
                    case RAC_RadioState.RxSearch:
                    {
                        if(signal == RAC_RadioStateMachineSignal.FrameDetected)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.RxFrame);
                        }
                        else if(signal == RAC_RadioStateMachineSignal.TxEnable)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.Rx2Tx);
                        }
                        else if(signal == RAC_RadioStateMachineSignal.RxCalibration)
                        {
                            RAC_ChangeRadioState(RAC_RadioState.RxWarm);
                        }
                        else
                        {
                            // RAC->CMD.RSDIS causes the RXEN signal to pulse low for a clock cycle
                            if(!RAC_RxEnable || signal == RAC_RadioStateMachineSignal.RxDisable)
                            {
                                RAC_ChangeRadioState(RAC_RadioState.RxPoweringDown);
                            }
                        }
                        break;
                    }
                    case RAC_RadioState.RxFrame:
                    {
                        if(signal == RAC_RadioStateMachineSignal.RxOverflow)
                        {
                            RAC_ClearOngoingRx();
                            RAC_ChangeRadioState(RAC_RadioState.RxOverflow);
                        }
                        // Here we should just move to the next state when the FRC fully received a frame.
                        // However, we have a race condition for which firing the RXFRAME_IrqHandler() with the radio state 
                        // already transitioned to the next state results in unexpected behavior from a software perspective 
                        // (which results in the  RX_Complete() firing from RXFRAME_IrqHandler()).
                        // To cope with this we progress to the next state only if both the RXFRAME IRQ flag has been cleared
                        // and the FRC has completed RXing a frame.
                        else if(signal == RAC_RadioStateMachineSignal.RxFrameIrqCleared
                                 || signal == RAC_RadioStateMachineSignal.RxDone
                                 || signal == RAC_RadioStateMachineSignal.RxAbort)
                        {
                            if(signal == RAC_RadioStateMachineSignal.RxFrameIrqCleared)
                            {
                                FRC_rxFrameIrqClearedPending = true;
                            }
                            if(signal == RAC_RadioStateMachineSignal.RxDone)
                            {
                                FRC_rxDonePending = true;
                            }

                            if(signal == RAC_RadioStateMachineSignal.RxAbort
                                || (FRC_rxFrameIrqClearedPending && FRC_rxDonePending))
                            {
                                RAC_ClearOngoingRx();

                                if(RAC_TxEnable || RAC_txAfterFramePending.Value)
                                {
                                    RAC_ChangeRadioState(RAC_RadioState.Rx2Tx);
                                }
                                else if(RAC_RxEnable)
                                {
                                    RAC_ChangeRadioState(RAC_RadioState.Rx2Rx);
                                }
                                else
                                {
                                    RAC_ChangeRadioState(RAC_RadioState.RxPoweringDown);
                                }
                            }
                        }
                        break;
                    }
                    case RAC_RadioState.Tx:
                    {
                        if(signal == RAC_RadioStateMachineSignal.TxDisable)
                        {
                            // The FRC reports a TX stopped by the TXDIS command as aborted.
                            FRC_txAbortedInterrupt.Value = true;
                            FRC_seqTxAbortedInterrupt.Value = true;
                            FRC_frameOk = false;
                            RAC_ClearOngoingTx();

                            if(RAC_RxEnable)
                            {
                                RAC_ChangeRadioState(RAC_RadioState.Tx2Rx);
                            }
                            else
                            {
                                RAC_ChangeRadioState(RAC_RadioState.TxPoweringDown);
                            }
                        }
                        else if(signal == RAC_RadioStateMachineSignal.TxDone) // FRC ends TX
                        {
                            if(RAC_TxEnable || RAC_txAfterFramePending.Value)
                            {
                                RAC_ChangeRadioState(RAC_RadioState.Tx2Tx);
                            }
                            else if(RAC_RxEnable)
                            {
                                RAC_ChangeRadioState(RAC_RadioState.Tx2Rx);
                            }
                            else
                            {
                                RAC_ChangeRadioState(RAC_RadioState.TxPoweringDown);
                            }
                        }
                        break;
                    }
                    default:
                    {
                        this.Log(LogLevel.Error, "RAC Radio State Machine Update, invalid state");
                        break;
                    }
                    }
                }

                if(previousState != RAC_currentRadioState)
                {
                    this.Log(LogBasicRadioActivityAsError ? LogLevel.Error : LogLevel.Info, "RSM Update at {0}, signal:{1} seqTimer={2} channel={3} ({4}), transition: {5}->{6} (TX={7} RX={8}) Lbt={9}",
                             GetTime(), signal, seqTimer.Value, LogicalChannel, MODEM_GetPhySignature(), previousState, RAC_currentRadioState, RAC_internalTxState, RAC_internalRxState, PROTIMER_listenBeforeTalkState);

                    RAC_seqRadioStateChangeInterrupt.Value = true;

                    // On a state transition, the STimer is always restarted from 0
                    RAC_SeqTimerRestart(0, RAC_seqTimerLimit);

                    if(previousState == RAC_RadioState.RxWarm)
                    {
                        rxWarmSettleTimer.Enabled = false;
                    }

                    switch(RAC_currentRadioState)
                    {
                    case RAC_RadioState.Off:
                        RAC_seqStateOffInterrupt.Value = true;
                        break;
                    case RAC_RadioState.RxWarm:
                        RAC_seqStateRxWarmInterrupt.Value = true;
                        SYNTH_startValid = false;
                        break;
                    case RAC_RadioState.RxSearch:
                        // Reset PKTBUFCOUNT when entering in RxSearch
                        FRC_packetBufferCount.Value = 0;
                        RAC_seqStateRxSearchInterrupt.Value = true;
                        MODEM_demodulatorState.Value = MODEM_DemodulatorState.PreambleSearch;
                        FRC_UpdateRawMode();
                        TryAcquireOnAirFrames();
                        break;
                    case RAC_RadioState.RxFrame:
                        RAC_seqStateRxFrameInterrupt.Value = true;
                        MODEM_demodulatorState.Value = MODEM_DemodulatorState.RxFrame;
                        break;
                    case RAC_RadioState.RxPoweringDown:
                        RAC_seqStateRxPoweringDownInterrupt.Value = true;
                        break;
                    case RAC_RadioState.Rx2Rx:
                        RAC_seqStateRx2RxInterrupt.Value = true;
                        break;
                    case RAC_RadioState.RxOverflow:
                        RAC_seqStateRxOverflowInterrupt.Value = true;
                        break;
                    case RAC_RadioState.Rx2Tx:
                        RAC_seqStateRx2TxInterrupt.Value = true;
                        break;
                    case RAC_RadioState.TxWarm:
                        RAC_seqStateTxWarmInterrupt.Value = true;
                        break;
                    case RAC_RadioState.Tx:
                        RAC_seqStateTxInterrupt.Value = true;
                        // We assemble and "transmit" the frame immediately so that receiver nodes 
                        // can transition from RX_SEARCH to RX_FRAME immediately. 
                        // We use timers to properly time the completion of the transmission process.
                        var frame = FRC_AssembleFrame();
                        TransmitFrame(frame);
                        break;
                    case RAC_RadioState.TxPoweringDown:
                        RAC_seqStateTxPoweringDownInterrupt.Value = true;
                        break;
                    case RAC_RadioState.Tx2Rx:
                        RAC_seqStateTx2RxInterrupt.Value = true;
                        break;
                    case RAC_RadioState.Tx2Tx:
                        RAC_seqStateTx2TxInterrupt.Value = true;
                        break;
                    case RAC_RadioState.Shutdown:
                        RAC_seqStateShutDownInterrupt.Value = true;
                        break;
                    default:
                        this.Log(LogLevel.Error, "Invalid Radio State ({0}).", RAC_currentRadioState);
                        break;
                    }

                    if(RAC_currentRadioState != RAC_RadioState.RxSearch && RAC_currentRadioState != RAC_RadioState.RxFrame)
                    {
                        MODEM_demodulatorState.Value = MODEM_DemodulatorState.Off;
                    }

                    AGC_UpdateRssiState();

                    // If we just entered RxSearch state, we should restart RSSI sampling.
                    // However, for performance reasons, we rely on the InterferenceQueue notifications,
                    // so we simply update the RSSI here.
                    if(RAC_currentRadioState == RAC_RadioState.RxSearch)
                    {
                        AGC_UpdateRssi();
                    }
                    // If entered a state other than RxSearch or RxFrame, we stop the Rssi sampling.
                    else if(RAC_currentRadioState != RAC_RadioState.RxSearch && RAC_currentRadioState != RAC_RadioState.RxFrame)
                    {
                        AGC_StopRssiTimer();
                    }
                }

                UpdateInterrupts();
            });
        }

        private void RAC_ClearOngoingTxOrRx()
        {
            RAC_ClearOngoingRx();
            RAC_ClearOngoingTx();
        }

        // SYNTH-tuned RF center frequency in Hz, quantized to the nearest 10 kHz
        // or 0 if unprogrammed.
        private ulong SYNTH_GetQuantizedRfFrequency()
        {
            var loDiv = SYNTH_DecodeLoDiv(SYNTH_loDivFreqCtrl.Value);
            if(loDiv == 0)
            {
                return 0;
            }
            var frequency = SYNTH_frequency.Value * HfxoFrequency / (1UL << 19) / loDiv;
            return (frequency + 5_000UL) / 10_000UL * 10_000UL;
        }

        private string SYNTH_GetRfFrequencyString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F2}MHz", SYNTH_GetQuantizedRfFrequency() / 1_000_000.0);
        }

        private uint SYNTH_DecodeLoDiv(SYNTH_LoDivFreqCtrl encoded)
        {
            switch(encoded)
            {
            case SYNTH_LoDivFreqCtrl.LoDiv1: return 1;
            case SYNTH_LoDivFreqCtrl.LoDiv2: return 2;
            case SYNTH_LoDivFreqCtrl.LoDiv3: return 3;
            case SYNTH_LoDivFreqCtrl.LoDiv4: return 4;
            case SYNTH_LoDivFreqCtrl.LoDiv5: return 5;
            case SYNTH_LoDivFreqCtrl.LoDiv6: return 6;
            case SYNTH_LoDivFreqCtrl.LoDiv7: return 7;
            case SYNTH_LoDivFreqCtrl.LoDiv8: return 8;
            case SYNTH_LoDivFreqCtrl.LoDiv9: return 9;
            case SYNTH_LoDivFreqCtrl.LoDiv10: return 10;
            case SYNTH_LoDivFreqCtrl.LoDiv12: return 12;
            case SYNTH_LoDivFreqCtrl.LoDiv14: return 14;
            case SYNTH_LoDivFreqCtrl.LoDiv15: return 15;
            case SYNTH_LoDivFreqCtrl.LoDiv16: return 16;
            case SYNTH_LoDivFreqCtrl.LoDiv18: return 18;
            case SYNTH_LoDivFreqCtrl.LoDiv20: return 20;
            case SYNTH_LoDivFreqCtrl.LoDiv24: return 24;
            default: return 0; // unknown / not yet programmed
            }
        }

        private ulong HfxoFrequency
        {
            get
            {
                if(Object.ReferenceEquals(cmu, null))
                {
                    foreach(var peripheral in machine.GetPeripheralsOfType<ISiLabs_CMU>())
                    {
                        cmu = peripheral;
                        break;
                    }
                }

                return cmu.HfxoFrequency;
            }
        }

        // Last value RAIL wrote to SYNTH.CHSP. RAIL stores the protocol's logical channel
        // index there (e.g. 11..26 for IEEE 802.15.4). Used only for human-readable log
        // strings; routing equality goes through Channel.
        private int LogicalChannel => (int)SYNTH_channelSpacing.Value;

        private uint CRC_CrcWidth => (uint)CRC_crcWidthMode.Value + 1;

        // 0:13 correspond to RAC_RXENSRCEN[13:0]
        // Bit 14 indicates that the PROTIMER is requesting RXEN
        // Bit 15 indicates that SPI is requesting RXEN
        private uint RAC_RxEnableMask => ((uint)RAC_softwareRxEnable.Value
                                          /*
                                          | (RAC_channelBusyRxEnable.Value << 8)
                                          | (RAC_timingDetectedRxEnable.Value << 9)
                                          | (RAC_preambleDetectedRxEnable.Value << 10)
                                          | (RAC_frameDetectedRxEnable.Value << 11)
                                          | (RAC_demodRxRequestRxEnable.Value << 12)
                                          | (RAC_prsRxEnable.Value << 13)
                                          */
                                          | ((PROTIMER_RxEnable ? 1U : 0U) << 14)
                                        /*
                                        | (SPI_RxEnable << 15)
                                        */
                                        );

        private bool RAC_RxEnable => RAC_RxEnableMask != 0;

        private bool RAC_TxEnable
        {
            get => RAC_txEnable;
            set
            {
                // FORCEDISABLE is a level input to the TXEN clear, so TXEN cannot be set while it is asserted.
                value &= !RAC_forceDisable.Value;
                var risingEdge = value && !RAC_txEnable;
                RAC_txEnable = value;

                if(risingEdge)
                {
                    RAC_UpdateRadioStateMachine(RAC_RadioStateMachineSignal.TxEnable);
                }
            }
        }

        private uint RAC_TxEnableMask
        {
            get => 0;
            set
            {
                // TODO
            }
        }

        private bool RAC_PaOutputLevelRampingInProgress => paRampingTimer.Enabled;

        private bool RAC_PaOutputLevelRamping
        {
            get
            {
                return RAC_paOutputLevelRamping;
            }

            set
            {
                // Ramping the PA up or down
                if(value != RAC_paOutputLevelRamping)
                {
                    RAC_paOutputLevelRamping = value;

                    // TODO: if the MODEM PA ramping is disabled, we should ramp up using the MODEM->RAMPCTRL.RAMPVAL value.
                    if(!MODEM_rampDisable.Value)
                    {
                        // TXRAMPDONE is a level interrupt, it goes low during ramping, otherwise is always high.
                        MODEM_TxRampingDoneInterrupt = false;
                        UpdateInterrupts();

                        paRampingTimer.Enabled = false;
                        this.Log(LogLevel.Noisy, "Starting PA ramping up/down at {0}", GetTime());
                        // This is initially set 0 if ramping up, 1 if ramping down. Once PA ramping completed, it gets flipped.
                        RAC_paRampingDone.Value = !value;
                        paRampingTimer.Value = 0;
                        paRampingTimer.Limit = RAC_PowerAmplifierRampingTimeUs;
                        paRampingTimer.Enabled = true;
                    }
                }
            }
        }

        private ushort RAC_SeqTimerLimit
        {
            get
            {
                return RAC_seqTimerLimit;
            }

            set
            {
                RAC_seqTimerLimit = value;
                ushort limit;

                TrySyncTime();

                if(RAC_seqTimerLimit == 0 || RAC_seqTimerLimit < seqTimer.Value)
                {
                    limit = 0xFFFF;
                }
                else
                {
                    limit = RAC_seqTimerLimit;
                }

                RAC_SeqTimerRestart((ushort)seqTimer.Value, limit);
            }
        }

        private uint RAC_SeqTimerValue
        {
            get
            {
                TrySyncTime();
                return (uint)seqTimer.Value;
            }
        }

        private uint MODEM_SyncWordLength => (uint)MODEM_syncBits.Value + 1;

        private uint MODEM_TxSyncWord => (MODEM_dualSync.Value && MODEM_txSync.Value) ? (uint)MODEM_syncWord1.Value : (uint)MODEM_syncWord0.Value;

        private uint MODEM_SyncWord0 => (uint)MODEM_syncWord0.Value & (uint)((1UL << (byte)MODEM_SyncWordLength) - 1);

        private uint MODEM_SyncWord1 => (uint)MODEM_syncWord1.Value & (uint)((1UL << (byte)MODEM_SyncWordLength) - 1);

        private uint MODEM_SyncWordBytes => ((uint)MODEM_syncBits.Value >> 3) + 1;

        private bool MODEM_TxRampingDoneInterrupt
        {
            set
            {
                MODEM_txRampingDoneInterrupt = value;
            }

            get
            {
                return MODEM_txRampingDoneInterrupt;
            }
        }

        private uint FRC_FrameLength => (uint)FRC_frameLength.Value + 1;

        private int FRC_ActiveReceiveFrameDescriptor
        {
            // ACTIVERXFCD == 0 => FCD2
            // ACTIVERXFCD == 1 => FCD3
            get
            {
                return FRC_activeReceiveFrameDescriptor.Value ? 3 : 2;
            }

            set
            {
                if(value != 2 && value != 3)
                {
                    this.Log(LogLevel.Error, "Setting illegal FRC_ActiveReceiveFrameDescriptor value.");
                    return;
                }

                FRC_activeReceiveFrameDescriptor.Value = (value == 3);
            }
        }

        private int FRC_ActiveTransmitFrameDescriptor
        {
            // ACTIVETXFCD == 0 => FCD0
            // ACTIVETXFCD == 1 => FCD1
            get
            {
                return FRC_activeTransmitFrameDescriptor.Value ? 1 : 0;
            }

            set
            {
                if(value != 0 && value != 1)
                {
                    this.Log(LogLevel.Error, "Setting illegal FRC_ActiveTransmitFrameDescriptor value.");
                    return;
                }

                FRC_activeTransmitFrameDescriptor.Value = (value == 1);
            }
        }

        private uint AGC_RssiPeriodUs
        {
            get
            {
                // RSSI measure period is defined as 2^RSSIPERIOD sub-periods.
                // SUBPERIOD controls if the subperiod is one baud-period or separately specified. 
                // If SUBPERIOD is set, the sub-period is defined by SUBINT + SUBNUM/SUBDEM (TODO: for now we only consider SUBINT). 
                // Otherwise subperiod is equal to 1 baud.
                // If ENCCARSSIPERIOD is set, enable the use of a separate RSSI PERIOD (CCARSSIPERIOD) during CCA measurements.
                //var rssiPeriod = (AGC_ccaRssiPeriodEnable.Value && PROTIMER_listenBeforeTalkState == PROTIMER_ListenBeforeTalkState.CcaDelay) ? AGC_ccaRssiPeriod.Value : AGC_rssiMeasurePeriod.Value;
                //var subPeriod = AGC_subPeriod.Value ? AGC_subPeriodInteger.Value : 1;

                // TODO: we assume 250kbps OQPSK PHY baud period for now.
                //uint ret = (uint)(((1 << (int)rssiPeriod))*(double)subPeriod*AGC_OQPSK250KbpsPhyBaudPeriodUs);
                //return ret;

                // RENODE-371: Hard-coding RSSI measure period and CCA RSSI measure period until we get to the bottom of this.
                if((AGC_ccaRssiPeriodEnable.Value && PROTIMER_listenBeforeTalkState == PROTIMER_ListenBeforeTalkState.CcaDelay))
                {
                    return AGC_OQPSK250KbpsPhyCcaRssiMeasurePeriodUs;
                }
                else if(AGC_rssiStartCommandOngoing)
                {
                    return AGC_OQPSK250KbpsPhyRssiMeasurePeriodUs;
                }
                else
                {
                    // For background RSSI sampling we use a longer period for performance reasons.
                    return AGC_OQPSK250KbpsPhyBackgroundRssiMeasurePeriodUs;
                }
            }
        }

        private sbyte AGC_FrameRssiIntegerPartAdjusted => (sbyte)(AGC_FrameRssiIntegerPart + AGC_RssiWrapCompensationOffsetDbm);

        private sbyte AGC_FrameRssiIntegerPart
        {
            set
            {
                AGC_frameRssiIntegerPart = value;
            }

            get
            {
                return AGC_frameRssiIntegerPart;
            }
        }

        // Channel energy at the moment of the read, while the AGC is sampling. Threshold
        // interrupts and CCA keep the cached sample in AGC_RssiIntegerPart, which the queue
        // callback refreshes. Outside RxSearch and RxFrame the register keeps that sample.
        private sbyte AGC_LiveRssiIntegerPartAdjusted
        {
            get
            {
                if(RAC_currentRadioState != RAC_RadioState.RxSearch && RAC_currentRadioState != RAC_RadioState.RxFrame)
                {
                    return AGC_RssiIntegerPartAdjusted;
                }

                TrySyncTime();
                var rssi = (sbyte)InterferenceQueue.GetCurrentRssi(this, Channel, GetTime());
                return (sbyte)(rssi + AGC_RssiWrapCompensationOffsetDbm);
            }
        }

        private sbyte AGC_RssiIntegerPartAdjusted => (sbyte)(AGC_RssiIntegerPart + AGC_RssiWrapCompensationOffsetDbm);

        private sbyte AGC_RssiIntegerPart { get; set; }

        private byte AGC_RssiFractionalPart
        {
            get
            {
                // TODO: for now AGC fractional part is always 0
                return 0;
            }
        }

        private byte AGC_FrameRssiFractionalPart
        {
            get
            {
                // TODO: for now Frame AGC fractional part is always 0
                return 0;
            }
        }

        // TODOs: 
        // 1. The PROTIMER is started by either using the START command in the PROTIMER_CMD register or through 
        //    a PRS event on a selectable PRS channel. 
        // 2. PRECNTTOPADJ
        // 3. RTCC sync for EM2 support
        // 4. LBT/CSMA logic
        // 5. TX/RX requests
        // 6. Protocol timer Events
        private bool PROTIMER_Enabled
        {
            get
            {
                return proTimer.Enabled;
            }

            set
            {
                if(value)
                {
                    // First stop the timer
                    proTimer.Enabled = false;

                    // TODO: If it is already running, do we need to update the BASECNT,WRAPCNT,etc?

                    // The proTimer timer is configured so that each tick corresponds to a PRECNT overflow.
                    // The PRECNTTOP value is 1 less than the intended value.
                    double frequency = (double)HfxoFrequency / (PROTIMER_preCounterTopInteger.Value + 1 + ((double)PROTIMER_preCounterTopFractional.Value / 256));

                    proTimer.Frequency = (uint)frequency;
                    proTimer.Limit = PROTIMER_ComputeTimerLimit();
                    proTimer.Value = 0;
                    proTimer.Enabled = true;
                }
                else
                {
                    if(proTimer.Enabled)
                    {
                        TrySyncTime();
                        uint currentIncrement = (uint)proTimer.Value;
                        proTimer.Enabled = false;

                        // Handle the current increment
                        if(currentIncrement > 0)
                        {
                            PROTIMER_HandlePreCntOverflows(currentIncrement);
                        }
                    }
                    else
                    {
                        proTimer.Enabled = false;
                    }
                }
            }
        }

        private bool PROTIMER_RxEnable => (PROTIMER_rxRequestState == PROTIMER_TxRxRequestState.Set
                                           || PROTIMER_rxRequestState == PROTIMER_TxRxRequestState.ClearEvent1);

        private bool PROTIMER_TxEnable
        {
            get => PROTIMER_txEnable;
            set
            {
                PROTIMER_txEnable = value;
                if(PROTIMER_txEnable)
                {
                    RAC_TxEnable = true;
                }
            }
        }

        private ISiLabs_Emu Emu
        {
            get
            {
                if(emu == null)
                {
                    emu = machine.GetPeripheralsOfType<ISiLabs_Emu>().FirstOrDefault();
                    emu?.RegisterRadioSequencerCpu(sequencer);
                    // The vector table in sequencer RAM is retained, but the core stays in CPUWAIT
                    // until software sets CPUWAITDIS again.
                    emu?.AddEnterDeepSleepHook(RAC_PowerDownSequencer);
                }
                return emu;
            }
        }

        // The sequencer requests EM1P in hardware unless it is halted (CPUWAIT) or in WFI, with the radio state machine off.
        private bool RAC_Em1pHardwareRequest => (!sequencer.CpuWaitSignal.IsSet && !RAC_sequencerInWfi)
                                                || RAC_currentRadioState != RAC_RadioState.Off;

        private bool RAC_Em1pRequest => (RAC_em1pRadioSwControl.Value && !RAC_em1pRadioDisableSwRequest.Value)
                                        || RAC_Em1pHardwareRequest;

        // MCUEM1PMODE in software control with the disable bit clear forces the host clock domain on
        // in EM1P. The RAC hardware requestors (FRC, ADC, VDAC) are not modeled, so hardware control
        // leaves those clocks gated; the EM2-capable EUSART raises its own host-clock request.
        private bool RAC_Em1pHostClockRequest => RAC_em1pMcuSwControl.Value && !RAC_em1pMcuDisableSwRequest.Value;

        private bool RAC_RxWillFail => RAC_rxFailReason != RAC_RxFailReason.None;

        private byte[] currentFrame;
        private IRadio currentFrameSender;
        private TimeInterval currentFrameTxStart = TimeInterval.Empty;
        private int currentFrameChannel;
        private uint currentFrameOffset = 0;
        private double currentFrameByteOverTheAirTimeUs = 0;
        private double currentFrameElapsedAirtimeUs = 0;
        private uint currentFrameCodedSymbolsPerInfoBit = 0;
        private int currentDisassembleSubFrame = 0;
        private uint currentDisassembleSubFrameStartWord = 0;
        // Payload bytes of the current RX subframe already delivered. Tracked separately from WCNT because a
        // subframe excluded from the word counter would otherwise never appear to make progress.
        private uint currentDisassembleSubFrameBytes = 0;
        private ISiLabs_CMU cmu;
        private ISiLabs_Emu emu;

        private IFlagRegisterField PROTIMER_seqListenBeforeTalkSuccessInterrupt;
        private IFlagRegisterField PROTIMER_seqWrapCounterOverflowInterrupt;
        private IFlagRegisterField PROTIMER_seqBaseCounterOverflowInterrupt;
        private IFlagRegisterField PROTIMER_seqPreCounterOverflowInterrupt;
        private IFlagRegisterField PROTIMER_seqRtccSynchedInterruptEnable;
        private IFlagRegisterField PROTIMER_seqListenBeforeTalkRetryInterruptEnable;
        private IFlagRegisterField PROTIMER_seqListenBeforeTalkFailureInterruptEnable;
        private IFlagRegisterField PROTIMER_seqListenBeforeTalkSuccessInterruptEnable;
        private IFlagRegisterField PROTIMER_seqWrapCounterOverflowInterruptEnable;
        private IFlagRegisterField PROTIMER_seqBaseCounterOverflowInterruptEnable;
        private IFlagRegisterField PROTIMER_seqPreCounterOverflowInterruptEnable;
        private IFlagRegisterField PROTIMER_rtccSynchedInterrupt;
        private IFlagRegisterField PROTIMER_seqListenBeforeTalkTimeoutCounterMatchInterruptEnable;
        private IFlagRegisterField PROTIMER_seqListenBeforeTalkFailureInterrupt;
        private IFlagRegisterField PROTIMER_seqRtccSynchedInterrupt;
        private IFlagRegisterField PROTIMER_seqListenBeforeTalkTimeoutCounterMatchInterrupt;
        private IFlagRegisterField PROTIMER_listenBeforeTalkTimeoutCounterMatchInterrupt;
        private IFlagRegisterField PROTIMER_seqListenBeforeTalkRetryInterrupt;
        private IFlagRegisterField PROTIMER_listenBeforeTalkRetryInterrupt;
        private IEnumRegisterField<PROTIMER_Event> PROTIMER_txSetEvent1;
        private IFlagRegisterField PROTIMER_listenBeforeTalkSuccessInterrupt;
        private IFlagRegisterField PROTIMER_listenBeforeTalkSync;
        private bool PROTIMER_listenBeforeTalkPending = false;
        private PROTIMER_ListenBeforeTalkState PROTIMER_listenBeforeTalkState = PROTIMER_ListenBeforeTalkState.Idle;
        private PROTIMER_TxRxRequestState PROTIMER_rxRequestState = PROTIMER_TxRxRequestState.Idle;
        private PROTIMER_TxRxRequestState PROTIMER_txRequestState = PROTIMER_TxRxRequestState.Idle;
        private IEnumRegisterField<PROTIMER_Event> PROTIMER_txSetEvent2;
        private IEnumRegisterField<PROTIMER_Event> PROTIMER_rxClearEvent2;
        private IFlagRegisterField PROTIMER_listenBeforeTalkRunning;
        private IEnumRegisterField<PROTIMER_Event> PROTIMER_rxClearEvent1;
        private IEnumRegisterField<PROTIMER_Event> PROTIMER_rxSetEvent1;
        private bool PROTIMER_txEnable = false;
        private IValueRegisterField PROTIMER_wrapCounterTop;
        private IValueRegisterField PROTIMER_baseCounterTop;
        private IValueRegisterField PROTIMER_preCounterTopFractional;
        private IValueRegisterField PROTIMER_preCounterTopInteger;
        private bool PROTIMER_rtcWait = false;
        private ushort PROTIMER_baseCounterValue = 0;
        private uint PROTIMER_wrapCounterValue = 0;
        private IEnumRegisterField<PROTIMER_Event> PROTIMER_rxSetEvent2;
        private IFlagRegisterField PROTIMER_listenBeforeTalkFailureInterrupt;
        private IFlagRegisterField PROTIMER_listenBeforeTalkPaused;
        private IValueRegisterField PROTIMER_listenBeforeTalkMaxExponent;
        private IFlagRegisterField PROTIMER_wrapCounterOverflowInterrupt;
        private IFlagRegisterField PROTIMER_baseCounterOverflowInterrupt;
        private IFlagRegisterField PROTIMER_preCounterOverflowInterrupt;
        private IFlagRegisterField PROTIMER_rtccSynchedInterruptEnable;
        private IFlagRegisterField PROTIMER_listenBeforeTalkTimeoutCounterMatchInterruptEnable;
        private IFlagRegisterField PROTIMER_listenBeforeTalkRetryInterruptEnable;
        private IFlagRegisterField PROTIMER_listenBeforeTalkFailureInterruptEnable;
        private IFlagRegisterField PROTIMER_listenBeforeTalkSuccessInterruptEnable;
        private IValueRegisterField PROTIMER_listenBeforeTalkStartExponent;
        private IFlagRegisterField PROTIMER_wrapCounterOverflowInterruptEnable;
        private IFlagRegisterField PROTIMER_preCounterOverflowInterruptEnable;
        private IValueRegisterField PROTIMER_ccaCounter;
        private IValueRegisterField PROTIMER_retryLimit;
        private IFlagRegisterField PROTIMER_fixedBackoff;
        private IValueRegisterField PROTIMER_ccaRepeat;
        private IValueRegisterField PROTIMER_ccaDelay;
        private IValueRegisterField PROTIMER_listenBeforeTalkRetryCounter;
        private IValueRegisterField PROTIMER_listenBeforeTalkExponent;
        private IFlagRegisterField PROTIMER_baseCounterOverflowInterruptEnable;
        private IEnumRegisterField<PROTIMER_PreCounterSource> PROTIMER_preCounterSource;
        private IEnumRegisterField<PROTIMER_BaseCounterSource> PROTIMER_baseCounterSource;
        private IEnumRegisterField<PROTIMER_WrapCounterSource> PROTIMER_wrapCounterSource;
        private IFlagRegisterField PROTIMER_rtccTriggerFromPrsEnable;
        private IFlagRegisterField PROTIMER_listenBeforeTalkPrsStartEnable;
        private IFlagRegisterField PROTIMER_listenBeforeTalkPrsPauseEnable;
        private IFlagRegisterField PROTIMER_listenBeforeTalkPrsStopEnable;
        private uint PROTIMER_preCounterSourcedBitmask = 0;

        private IFlagRegisterField MODEM_rxFrameWithSyncWord1DetectedInterruptEnable;
        private IFlagRegisterField MODEM_rxPreambleDetectedInterruptEnable;
        private IFlagRegisterField MODEM_txRampingDoneInterruptEnable;
        private IFlagRegisterField MODEM_txPreambleSentInterruptEnable;
        private IFlagRegisterField MODEM_txSyncSentInterruptEnable;
        private IFlagRegisterField MODEM_txFrameSentInterruptEnable;
        private IFlagRegisterField MODEM_rxPreambleLostInterrupt;
        private IFlagRegisterField MODEM_rxFrameWithSyncWord1DetectedInterrupt;
        private IFlagRegisterField MODEM_rxFrameWithSyncWord0DetectedInterrupt;
        private IFlagRegisterField MODEM_rxPreambleDetectedInterrupt;
        private IFlagRegisterField MODEM_txPreambleSentInterrupt;
        private IFlagRegisterField MODEM_txSyncSentInterrupt;
        private IFlagRegisterField MODEM_txFrameSentInterrupt;
        private bool MODEM_txRampingDoneInterrupt = true;
        private IFlagRegisterField MODEM_rxFrameWithSyncWord0DetectedInterruptEnable;
        private IFlagRegisterField MODEM_rxPreambleLostInterruptEnable;
        private IFlagRegisterField MODEM_seqTxFrameSentInterrupt;
        private IEnumRegisterField<MODEM_DemodulatorState> MODEM_demodulatorState;
        private IFlagRegisterField MODEM_seqTxPreambleSentInterrupt;
        private IFlagRegisterField MODEM_syncData;
        private IFlagRegisterField MODEM_txSync;
        private IFlagRegisterField MODEM_dualSync;
        private IValueRegisterField MODEM_syncBits;
        private IValueRegisterField MODEM_txBases;
        private IValueRegisterField MODEM_baseBits;
        private IFlagRegisterField MODEM_seqRxPreambleLostInterruptEnable;
        private IFlagRegisterField MODEM_seqRxFrameWithSyncWord1DetectedInterruptEnable;
        private IValueRegisterField MODEM_syncWord0;
        private IFlagRegisterField MODEM_seqRxFrameWithSyncWord0DetectedInterruptEnable;
        private IFlagRegisterField MODEM_seqTxRampingDoneInterruptEnable;
        private IFlagRegisterField MODEM_seqTxPreambleSentInterruptEnable;
        private IFlagRegisterField MODEM_seqTxSyncSentInterruptEnable;
        private IFlagRegisterField MODEM_seqTxFrameSentInterruptEnable;
        private IFlagRegisterField MODEM_seqRxPreambleLostInterrupt;
        private IFlagRegisterField MODEM_seqRxFrameWithSyncWord1DetectedInterrupt;
        private IFlagRegisterField MODEM_seqRxFrameWithSyncWord0DetectedInterrupt;
        private IFlagRegisterField MODEM_seqRxPreambleDetectedInterrupt;
        private IFlagRegisterField MODEM_seqRxPreambleDetectedInterruptEnable;
        private IValueRegisterField MODEM_syncWord1;
        private IFlagRegisterField MODEM_frameDetectedId;
        private IValueRegisterField MODEM_rampRate0;
        private IEnumRegisterField<MODEM_DsssDoublingMode> MODEM_dsssDoublingMode;
        private IEnumRegisterField<MODEM_SymbolCoding> MODEM_symbolCoding;
        private IEnumRegisterField<MODEM_ModulationFormat> MODEM_modulationFormat;
        private IValueRegisterField MODEM_dsssShifts;
        private IValueRegisterField MODEM_dsssLength;
        private IValueRegisterField MODEM_baudrateDivisionFactorB;
        private IEnumRegisterField<MODEM_RateSelectMode> MODEM_rateSelectMode;
        private IValueRegisterField MODEM_baudrateDivisionFactorA;
        private IValueRegisterField MODEM_txBaudrateDenominator;
        private IValueRegisterField MODEM_txBaudrateNumerator;
        private IFlagRegisterField MODEM_viterbiDemodulatorEnable;
        private IValueRegisterField MODEM_rampValue;
        private IFlagRegisterField MODEM_rampDisable;
        private IValueRegisterField MODEM_rampRate2;
        private IValueRegisterField MODEM_rampRate1;
        private IFlagRegisterField MODEM_seqTxSyncSentInterrupt;

        private IFlagRegisterField AGC_rssiDoneInterrupt;
        private IFlagRegisterField AGC_ccaInterrupt;
        private IFlagRegisterField AGC_rssiValidInterrupt;
        private IValueRegisterField AGC_subPeriodInteger;
        private IFlagRegisterField AGC_subPeriod;
        private IValueRegisterField AGC_rssiShift;
        private IValueRegisterField AGC_rssiLowThreshold;
        private IValueRegisterField AGC_rssiHighThreshold;
        private IFlagRegisterField AGC_rssiHighInterrupt;
        private IFlagRegisterField AGC_ccaRssiPeriodEnable;
        private IFlagRegisterField AGC_ccaSoftwareControl;
        private IEnumRegisterField<AGC_RssiState> AGC_rssiState;
        private IEnumRegisterField<AGC_CcaMode3Logic> AGC_ccaMode3Logic;
        private IEnumRegisterField<AGC_CcaMode> AGC_ccaMode;
        private IValueRegisterField AGC_powerMeasurePeriod;
        private IValueRegisterField AGC_rssiMeasurePeriod;
        private IValueRegisterField AGC_ccaThreshold;
        private IFlagRegisterField AGC_cca;
        private IValueRegisterField AGC_ccaRssiPeriod;
        private sbyte AGC_frameRssiIntegerPart;
        private IFlagRegisterField AGC_rssiLowInterrupt;
        private IFlagRegisterField AGC_rssiValidInterruptEnable;
        private IFlagRegisterField AGC_seqCcaNotDetectedInterruptEnable;
        private IFlagRegisterField AGC_seqRssiLowInterruptEnable;
        private IFlagRegisterField AGC_seqRssiHighInterruptEnable;
        private IFlagRegisterField AGC_seqRssiDoneInterruptEnable;
        private IFlagRegisterField AGC_seqCcaInterruptEnable;
        private IFlagRegisterField AGC_seqRssiValidInterruptEnable;
        private IFlagRegisterField AGC_seqCcaNotDetectedInterrupt;
        private IFlagRegisterField AGC_ccaNotDetectedInterrupt;
        private IFlagRegisterField AGC_seqRssiLowInterrupt;
        private IFlagRegisterField AGC_seqRssiDoneInterrupt;
        private IFlagRegisterField AGC_seqCcaInterrupt;
        private IFlagRegisterField AGC_seqRssiValidInterrupt;
        private IFlagRegisterField AGC_ccaNotDetectedInterruptEnable;
        private IFlagRegisterField AGC_rssiLowInterruptEnable;
        private IFlagRegisterField AGC_rssiHighInterruptEnable;
        private IFlagRegisterField AGC_rssiDoneInterruptEnable;
        private IFlagRegisterField AGC_ccaInterruptEnable;
        private IFlagRegisterField AGC_seqRssiHighInterrupt;
        private bool AGC_rssiStartCommandFromProtimer = false;
        private bool AGC_rssiStartCommandOngoing = false;
        private bool AGC_rssiFirstRead = true;

        private IValueRegisterField CRC_crcBitsPerWord;
        private IFlagRegisterField CRC_reverseCrcByteOrdering;
        private IEnumRegisterField<CRC_CrcWidthMode> CRC_crcWidthMode;

        private IFlagRegisterField FRC_seqPacketBufferStartInterrupt;
        private IFlagRegisterField FRC_packetBufferThresholdInterruptEnable;
        private IFlagRegisterField FRC_packetBufferStartInterruptEnable;
        private IFlagRegisterField FRC_txRawEventInterruptEnable;
        private IFlagRegisterField FRC_rxRawEventInterruptEnable;
        private IFlagRegisterField FRC_rxOverflowInterruptEnable;
        private IFlagRegisterField FRC_frameErrorInterruptEnable;
        private IFlagRegisterField FRC_rxAbortedInterruptEnable;
        private IFlagRegisterField FRC_rxDoneInterruptEnable;
        private IFlagRegisterField FRC_txUnderflowInterruptEnable;
        private IFlagRegisterField FRC_txAfterFrameDoneInterruptEnable;
        private IFlagRegisterField FRC_txDoneInterruptEnable;
        private IFlagRegisterField FRC_packetBufferThresholdInterrupt;
        private IFlagRegisterField FRC_packetBufferStartInterrupt;
        private IFlagRegisterField FRC_txRawEventInterrupt;
        private IFlagRegisterField FRC_rxRawEventInterrupt;
        private IFlagRegisterField FRC_rxOverflowInterrupt;
        private IFlagRegisterField FRC_frameErrorInterrupt;
        private IFlagRegisterField FRC_seqTxDoneInterrupt;
        private IFlagRegisterField FRC_seqTxAfterFrameDoneInterrupt;
        private IFlagRegisterField FRC_seqTxUnderflowInterrupt;
        private IFlagRegisterField FRC_seqRxDoneInterrupt;
        private IFlagRegisterField FRC_ptiEmitRx;
        private IFlagRegisterField FRC_seqPacketBufferThresholdInterruptEnable;
        private IFlagRegisterField FRC_seqPacketBufferStartInterruptEnable;
        private IFlagRegisterField FRC_seqRxRawEventInterruptEnable;
        private IFlagRegisterField FRC_seqRxOverflowInterruptEnable;
        private IFlagRegisterField FRC_seqFrameErrorInterruptEnable;
        private IFlagRegisterField FRC_seqRxAbortedInterruptEnable;
        private IFlagRegisterField FRC_rxAbortedInterrupt;
        private IFlagRegisterField FRC_seqRxDoneInterruptEnable;
        private IFlagRegisterField FRC_seqTxAfterFrameDoneInterruptEnable;
        private IFlagRegisterField FRC_seqTxDoneInterruptEnable;
        private IFlagRegisterField FRC_seqPacketBufferThresholdInterrupt;
        private IFlagRegisterField FRC_seqTxRawEventInterrupt;
        private IFlagRegisterField FRC_seqRxRawEventInterrupt;
        private IFlagRegisterField FRC_seqRxOverflowInterrupt;
        private IFlagRegisterField FRC_seqFrameErrorInterrupt;
        private IFlagRegisterField FRC_seqRxAbortedInterrupt;
        private IFlagRegisterField FRC_seqTxUnderflowInterruptEnable;
        private IFlagRegisterField FRC_ptiEmitTx;
        private IFlagRegisterField FRC_rxDoneInterrupt;
        private IFlagRegisterField FRC_txAfterFrameDoneInterrupt;
        private IValueRegisterField FRC_maxDecodedLength;
        private IFlagRegisterField FRC_dynamicFrameCrcIncluded;
        private IValueRegisterField FRC_minDecodedLength;
        private IValueRegisterField FRC_dynamicFrameLengthBits;
        private IValueRegisterField FRC_dynamicFrameLengthOffset;
        private IValueRegisterField FRC_dynamicFrameLengthBitShift;
        private IEnumRegisterField<FRC_DynamicFrameLengthBitOrder> FRC_dynamicFrameLengthBitOrder;
        private IEnumRegisterField<FRC_DynamicFrameLengthMode> FRC_dynamicFrameLengthMode;
        private IEnumRegisterField<FRC_RxRawDataTriggerMode> FRC_rxRawDataTriggerSelect;
        private IEnumRegisterField<FRC_RxRawDataMode> FRC_rxRawDataSelect;
        private IFlagRegisterField FRC_enableRawDataRandomNumberGenerator;
        private IValueRegisterField FRC_fsmState;
        private IFlagRegisterField FRC_rxRawBlocked;
        private IFlagRegisterField FRC_activeReceiveFrameDescriptor;
        private IFlagRegisterField FRC_activeTransmitFrameDescriptor;
        private bool FRC_rxFrameIrqClearedPending = false;
        private bool FRC_rxDonePending = false;
        private bool FRC_rxAbortInProgress = false;
        private bool FRC_frameOk = false;
        private bool FRC_frameLengthError = false;
        private bool FRC_rxFrameDiscarded = false;
        private bool FRC_crcErrorTolerated = false;
        private IValueRegisterField FRC_initialDecodedFrameLength;
        private IValueRegisterField FRC_wordCounter;
        private IValueRegisterField FRC_frameLength;
        private IValueRegisterField FRC_lengthFieldLocation;
        private IValueRegisterField FRC_addressFieldLocation;
        private IFlagRegisterField FRC_txDoneInterrupt;
        private IValueRegisterField FRC_packetBufferCount;
        private IFlagRegisterField FRC_packetBufferStop;
        private IFlagRegisterField FRC_packetBufferThresholdEnable;
        private IValueRegisterField FRC_packetBufferThreshold;
        private IValueRegisterField FRC_packetBufferStartAddress;
        private IFlagRegisterField FRC_rxAppendProtimerCc0HighWrap;
        private IFlagRegisterField FRC_rxAppendProtimerCc0LowWrap;
        private IFlagRegisterField FRC_txUnderflowInterrupt;
        private IFlagRegisterField FRC_txAbortedInterrupt;
        private IFlagRegisterField FRC_txAbortedInterruptEnable;
        private IFlagRegisterField FRC_seqTxAbortedInterrupt;
        private IFlagRegisterField FRC_seqTxAbortedInterruptEnable;
        private IFlagRegisterField FRC_rxAppendProtimerCc0base;
        private IFlagRegisterField FRC_rxAppendRssi;
        private IFlagRegisterField FRC_rxBufferRestoreOnRxAborted;
        private IFlagRegisterField FRC_rxBufferRestoreOnFrameError;
        private IFlagRegisterField FRC_rxBufferClear;
        private IFlagRegisterField FRC_rxAcceptCrcErrors;
        private IFlagRegisterField FRC_rxStoreCrc;
        private IEnumRegisterField<FRC_FrameDescriptorMode> FRC_rxFrameDescriptorMode;
        private IEnumRegisterField<FRC_FrameDescriptorMode> FRC_txFrameDescriptorMode;
        private IValueRegisterField FRC_rateSelect;
        private IFlagRegisterField FRC_rxAppendStatus;
        private IFlagRegisterField FRC_ptiEmitRssi;
        private IFlagRegisterField FRC_seqTxRawEventInterruptEnable;
        private IFlagRegisterField FRC_ptiEmitAux;

        private IFlagRegisterField RAC_seqStateRx2RxInterruptEnable;
        private IFlagRegisterField RAC_seqStateRxPoweringDownInterruptEnable;
        private IFlagRegisterField RAC_seqStateRxFrameInterruptEnable;
        private IFlagRegisterField RAC_seqStateRxSearchInterruptEnable;
        private IFlagRegisterField RAC_seqStateRxWarmInterruptEnable;
        private IFlagRegisterField RAC_seqStateOffInterruptEnable;
        private IFlagRegisterField RAC_seqPrsEventInterruptEnable;
        private IFlagRegisterField RAC_seqDemodRxRequestClearInterruptEnable;
        private IFlagRegisterField RAC_seqStimerCompareEventInterruptEnable;
        private IFlagRegisterField RAC_seqRadioStateChangeInterruptEnable;
        private IFlagRegisterField RAC_seqStateShutDownInterrupt;
        private IFlagRegisterField FRC_ptiEmitState;
        private IFlagRegisterField RAC_seqStateTx2RxInterrupt;
        private IFlagRegisterField RAC_seqStateTxPoweringDownInterrupt;
        private IFlagRegisterField RAC_seqStateTxInterrupt;
        private IFlagRegisterField RAC_seqStateTxWarmInterrupt;
        private IFlagRegisterField RAC_seqStateRx2TxInterrupt;
        private IFlagRegisterField RAC_seqStateRxOverflowInterruptEnable;
        private IFlagRegisterField RAC_seqStateRxOverflowInterrupt;
        private IFlagRegisterField RAC_seqStateRx2TxInterruptEnable;
        private IFlagRegisterField RAC_seqStateTxInterruptEnable;
        private bool RAC_txEnable;
        private ushort RAC_seqTimerLimit = 0xFFFF;
        private bool RAC_20DbmSupport = false;
        private bool RAC_10DbmSupport = false;
        private bool RAC_0DbmSupport = false;
        private IFlagRegisterField RAC_unlocked;
        private IFlagRegisterField RAC_seqTimerAlwaysRun;
        private IFlagRegisterField RAC_seqTimerCompareRelative;
        private IEnumRegisterField<RAC_SeqTimerCompareInvalidMode> RAC_seqTimerCompareInvalidMode;
        private IEnumRegisterField<RAC_SeqTimerCompareAction> RAC_seqTimerCompareAction;
        private IValueRegisterField RAC_seqTimerCompareValue;
        private IValueRegisterField RAC_seqTimerPrescaler;
        private IFlagRegisterField RAC_seqStateShutDownInterruptEnable;
        private IFlagRegisterField RAC_seqStateTx2TxInterruptEnable;
        private IFlagRegisterField RAC_seqStateTx2RxInterruptEnable;
        private IFlagRegisterField RAC_seqStateTxPoweringDownInterruptEnable;
        private IFlagRegisterField RAC_seqStateTxWarmInterruptEnable;
        private IFlagRegisterField RAC_seqStateRx2RxInterrupt;
        private IFlagRegisterField RAC_seqStateTx2TxInterrupt;
        private IFlagRegisterField RAC_seqStateRxFrameInterrupt;
        private IFlagRegisterField RAC_sequencerActive;
        private IFlagRegisterField RAC_sequencerInDeepSleep;
        private IFlagRegisterField RAC_sequencerInSleeping;
        private IFlagRegisterField RAC_txAfterFrameActive;
        private IFlagRegisterField RAC_seqStateRxPoweringDownInterrupt;
        private IFlagRegisterField RAC_forceStateActive;
        private IValueRegisterField RAC_softwareRxEnable;
        private IEnumRegisterField<RAC_RadioState> RAC_forceStateTransition;
        private RAC_RadioState RAC_previous3RadioState = RAC_RadioState.Off;
        private RAC_RadioState RAC_previous1RadioState = RAC_RadioState.Off;
        private RAC_RadioState RAC_currentRadioState = RAC_RadioState.Off;
        private RAC_RxFailReason RAC_rxFailReason = RAC_RxFailReason.None;

        private RAC_InternalRxState RAC_internalRxState = RAC_InternalRxState.Idle;
        private RAC_InternalTxState RAC_internalTxState = RAC_InternalTxState.Idle;
        private IFlagRegisterField FRC_ptiEmitSyncWord;
        private RAC_RadioState RAC_previous2RadioState = RAC_RadioState.Off;
        private IFlagRegisterField RAC_prsForceTx;
        private IFlagRegisterField RAC_txAfterFramePending;
        private IFlagRegisterField RAC_exitShutdownDisable;
        private IFlagRegisterField RAC_seqStateRxSearchInterrupt;
        private IFlagRegisterField RAC_seqStateRxWarmInterrupt;
        private IFlagRegisterField RAC_seqStateOffInterrupt;
        private IFlagRegisterField RAC_forceDisable;
        private IFlagRegisterField RAC_seqDemodRxRequestClearInterrupt;
        private IFlagRegisterField RAC_seqStimerCompareEventInterrupt;
        private IFlagRegisterField RAC_seqRadioStateChangeInterrupt;
        private IFlagRegisterField RAC_stimerCompareEventInterruptEnable;
        private IFlagRegisterField RAC_radioStateChangeInterruptEnable;
        private IFlagRegisterField RAC_seqPrsEventInterrupt;
        private IFlagRegisterField RAC_stimerCompareEventInterrupt;
        private IFlagRegisterField RAC_radioStateChangeInterrupt;
        private IValueRegisterField RAC_mainCoreSeqInterrupts;
        private bool RAC_paOutputLevelRamping = false;
        private IFlagRegisterField RAC_paRampingDone;
        private IFlagRegisterField RAC_em1pRadioSwControl;
        private IFlagRegisterField RAC_em1pRadioDisableSwRequest;
        private IFlagRegisterField RAC_em1pMcuSwControl;
        private IFlagRegisterField RAC_em1pMcuDisableSwRequest;
        private bool RAC_sequencerInWfi;
        private bool RAC_sequencerResetPending;
        private IFlagRegisterField RAC_txAfterRx;
        private IValueRegisterField RAC_mainCoreSeqInterruptsEnable;
        private IValueRegisterField RAC_softwareIrq;

        private bool SYNTH_startValid;
        private TimeInterval SYNTH_startTime;
        private IValueRegisterField SYNTH_frequency;
        private IEnumRegisterField<SYNTH_LoDivFreqCtrl> SYNTH_loDivFreqCtrl;
        private IValueRegisterField SYNTH_channelSpacing;
        // Last value software wrote to the PROTIMER RANDOM register. While FIXEDBACKOFF is set this is
        // frozen (writes are ignored) to mirror hardware.
        private uint PROTIMER_fixedBackoffRandomValue = 1;

        private readonly Machine machine;
        private readonly MappedMemory ram;
        private readonly CortexM sequencer;
        private readonly PseudorandomNumberGenerator random;
        private readonly SiLabs_BUFC_4 bufferController;
        private readonly LimitTimer seqTimer;
        private readonly LimitTimer proTimer;
        private readonly LimitTimer paRampingTimer;
        private readonly LimitTimer rssiUpdateTimer;
        private readonly LimitTimer txTimer;
        private readonly LimitTimer rxTimer;
        private readonly LimitTimer rxWarmSettleTimer;
        private readonly DoubleWordRegisterCollection automaticGainControlRegistersCollection;
        private readonly DoubleWordRegisterCollection cyclicRedundancyCheckRegistersCollection;
        private readonly DoubleWordRegisterCollection frameControllerRegistersCollection;
        private readonly DoubleWordRegisterCollection modulatorAndDemodulatorRegistersCollection;
        private readonly DoubleWordRegisterCollection protocolTimerRegistersCollection;
        private readonly DoubleWordRegisterCollection radioControllerRegistersCollection;
        private readonly DoubleWordRegisterCollection radioMailboxRegistersCollection;
        private readonly DoubleWordRegisterCollection hostMailboxRegistersCollection;
        private readonly DoubleWordRegisterCollection synthesizerRegistersCollection;
        private readonly IValueRegisterField[] RAC_scratch = new IValueRegisterField[RAC_NumberOfScratchRegisters];
        private readonly IValueRegisterField[] RAC_seqStorage = new IValueRegisterField[RAC_NumberOfSequencerStorageRegisters];
        private readonly IFlagRegisterField[] RFMAILBOX_messageInterruptEnable = new IFlagRegisterField[MailboxMessageNumber];
        private readonly IFlagRegisterField[] RFMAILBOX_messageInterrupt = new IFlagRegisterField[MailboxMessageNumber];
        private readonly IValueRegisterField[] RFMAILBOX_messagePointer = new IValueRegisterField[MailboxMessageNumber];
        private readonly IFlagRegisterField[] HOSTMAILBOX_messageInterruptEnable = new IFlagRegisterField[MailboxMessageNumber];
        private readonly IFlagRegisterField[] HOSTMAILBOX_messageInterrupt = new IFlagRegisterField[MailboxMessageNumber];
        private readonly IValueRegisterField[] HOSTMAILBOX_messagePointer = new IValueRegisterField[MailboxMessageNumber];
        private readonly PROTIMER_TimeoutCounter[] PROTIMER_timeoutCounter;
        private readonly PROTIMER_CaptureCompareChannel[] PROTIMER_captureCompareChannel;
        private readonly byte[] FRC_packetBufferCapture;
        private readonly FRC_FrameDescriptor[] FRC_frameDescriptor;
        private readonly IFlagRegisterField[] FRC_wordCounterCompareInterrupt = new IFlagRegisterField[FRC_NumberOfWordCounterCompareChannels];
        private readonly IFlagRegisterField[] FRC_wordCounterCompareInterruptEnable = new IFlagRegisterField[FRC_NumberOfWordCounterCompareChannels];
        private readonly IFlagRegisterField[] FRC_seqWordCounterCompareInterrupt = new IFlagRegisterField[FRC_NumberOfWordCounterCompareChannels];
        private readonly IFlagRegisterField[] FRC_seqWordCounterCompareInterruptEnable = new IFlagRegisterField[FRC_NumberOfWordCounterCompareChannels];
        private const uint SetRegisterOffset = 0x1000;
        private const uint ClearRegisterOffset = 0x2000;
        private const uint ToggleRegisterOffset = 0x3000;
        private const uint SequencerMemoryBaseAddress = 0xB0000000;
        private const uint MailboxMessageNumber = 4;
        private const ulong MicrosecondFrequency = 1000000UL;
        private const ulong HalfMicrosecondFrequency = 2000000UL;
        private const double MinimumRxSearchDelayAfterSynthStartUs = 108.0;
        private const int SniffSyncWordSerialLength = 4;
        private const uint FRC_NumberOfFrameDescriptors = 4;
        // FCD0/FCD1 form the transmit descriptor pair, FCD2/FCD3 the receive pair.
        private const int FRC_FirstTransmitFrameDescriptor = 0;
        private const int FRC_FirstReceiveFrameDescriptor = 2;
        // Only the compare channels backed by a register in this model: WCNTCMP0/1/2.
        private const int FRC_NumberOfWordCounterCompareChannels = 3;
        private const uint FRC_PacketBufferCaptureSize = 48;
        private const uint FRC_RxFragmentLengthInBytes = 4;
        // FRC.CTRL.RATESELECT value that selects the alternate convolutional coding (CODINGB => BLE S=2 / 500 kbps).
        private const uint FRC_RateSelectCodingB = 2u;
        // RENODE-53
        // TODO: calculate the ramping time from registers
        private const uint RAC_PowerAmplifierRampingTimeUs = 5;
        private const uint RAC_NumberOfSequencerStorageRegisters = 4;
        private const uint RAC_NumberOfScratchRegisters = 8;
        private const uint PROTIMER_DefaultLightWeightTimerLimit = 0xFFFFFFFF;
        private const uint PROTIMER_MinimumTimeoutCounterDelay = 2;
        private const uint PROTIMER_NumberOfTimeoutCounters = 2;
        private const uint PROTIMER_NumberOfCaptureCompareChannels = 8;
        private const uint PROTIMER_NumberOfCaptureCompareChannelPrsEvents = 5;
        // LE Coded PHY preamble is a fixed 80us (sent at the 1 Msym/s base rate, independent of S=2/S=8).
        private const double MODEM_BleCodedPhyPreambleOverTheAirTimeUs = 80.0;
        private const double MODEM_MinimumHeardPreambleFraction = 0.5;
        private const uint MODEM_BlePhyRxChainDelayNanoS = 50000;
        private const uint MODEM_BlePhyRxDoneDelayNanoS = 11250;
        private const uint MODEM_BlePhyTxChainDelayNanoS = 750;
        // TODO: verify this
        private const uint MODEM_BlePhyTxDoneChainDelayNanoS = 750;
        private const uint MODEM_802154PhyRxChainDelayNanoS = 6625;
        private const uint MODEM_802154PhyRxDoneDelayNanoS = 6625;
        private const uint MODEM_802154PhyTxChainDelayNanoS = 600;
        // TODO: verify this
        private const uint MODEM_802154PhyTxDoneChainDelayNanoS = 0;
        // The PHYs are configured in a way that the produced RSSI values are shifted by this value.
        // This is to avoid some wrap issue with very small values.
        // RAIL then subtracts this offset to all RSSI values coming from the hardware.
        // TODO: this value should be able to be inferred from the overall PHY Configurator registers setup,
        // for now we just hard-code the offset.
        private const uint AGC_RssiWrapCompensationOffsetDbm = 50;
        // RENODE-371: these values are a temporary work-around until the ticket is addressed.
        private const uint AGC_OQPSK250KbpsPhyCcaRssiMeasurePeriodUs = 128;
        private const uint AGC_OQPSK250KbpsPhyRssiMeasurePeriodUs = 8;
        private const uint AGC_OQPSK250KbpsPhyBackgroundRssiMeasurePeriodUs = 50;
        private const int AGC_RssiInvalid = -128;

        public enum PROTIMER_Event
        {
            Disabled = 0,
            Always = 1,
            PreCounterOverflow = 2,
            BaseCounterOverflow = 3,
            WrapCounterOverflow = 4,
            TimeoutCounter0Underflow = 5,
            TimeoutCounter1Underflow = 6,
            TimeoutCounter0Match = 7,
            TimeoutCounter1Match = 8,
            CaptureCompareChannel0Event = 9,
            CaptureCompareChannel1Event = 10,
            CaptureCompareChannel2Event = 11,
            CaptureCompareChannel3Event = 12,
            CaptureCompareChannel4Event = 13,
            TxDone = 14,
            RxDone = 15,
            TxOrRxDone = 16,
            Syncword0Detected = 17,
            Syncword1Detected = 18,
            Syncword0Or1Detected = 19,
            ListenBeforeTalkSuccess = 20,
            ListenBeforeTalkRetry = 21,
            ListenBeforeTalkFailure = 22,
            AnyListenBeforeTalk = 23,
            ClearChannelAssessmentMeasurementCompleted = 24,
            ClearChannelAssessmentMeasurementCompletedChannelClear = 25,
            ClearChannelAssessmentMeasurementCompletedChannelBusy = 26,
            TimeoutCounter0MatchListenBeforeTalk = 27,
            InternalTrigger = 28,
        }

        public enum MODEM_DemodulatorState
        {
            Off = 0x0,
            TimingSearch = 0x1,
            PreambleSearch = 0x2,
            FrameSearch = 0x3,
            RxFrame = 0x4,
            TimingSearchWithSlidingWindow = 0x5,
        }

        private class PROTIMER_CaptureCompareChannel
        {
            public PROTIMER_CaptureCompareChannel(SiLabs_xG24_LPW parent, uint index)
            {
                this.parent = parent;
                this.index = index;
            }

            public void Capture(ushort baseVal, uint wrapVal)
            {
                if(CaptureValid.Value)
                {
                    OverflowInterrupt.Value = true;
                    SeqOverflowInterrupt.Value = true;
                }

                CaptureValid.Value = true;
                InterruptField.Value = true;
                SeqInterruptField.Value = true;

                PreValue.Value = 0;
                BaseValue.Value = baseVal;
                WrapValue.Value = wrapVal;

                parent.UpdateInterrupts();
            }

            public bool Interrupt => ((InterruptField.Value && InterruptEnable.Value)
                                      || (OverflowInterrupt.Value && OverflowInterruptEnable.Value));

            public bool SeqInterrupt => ((SeqInterruptField.Value && SeqInterruptEnable.Value)
                                         || (SeqOverflowInterrupt.Value && SeqOverflowInterruptEnable.Value));

            public IFlagRegisterField InterruptField;
            public IValueRegisterField BaseValue;
            public IValueRegisterField PreValue;
            public IEnumRegisterField<PROTIMER_CaptureInputSource> CaptureInputSource;
            public IFlagRegisterField WrapMatchEnable;
            public IFlagRegisterField BaseMatchEnable;
            public IFlagRegisterField PreMatchEnable;
            public IEnumRegisterField<PROTIMER_CaptureCompareMode> Mode;
            public IFlagRegisterField Enable;
            public IFlagRegisterField SeqOverflowInterruptEnable;
            public IFlagRegisterField SeqOverflowInterrupt;
            public IFlagRegisterField OverflowInterruptEnable;
            public IFlagRegisterField OverflowInterrupt;
            public IFlagRegisterField SeqInterruptEnable;
            public IFlagRegisterField SeqInterruptField;
            public IFlagRegisterField InterruptEnable;
            public IValueRegisterField WrapValue;
            public IFlagRegisterField CaptureValid;

            private readonly SiLabs_xG24_LPW parent;
            private readonly uint index;
        }

        private class PROTIMER_TimeoutCounter
        {
            public PROTIMER_TimeoutCounter(SiLabs_xG24_LPW parent, uint index)
            {
                this.parent = parent;
                this.index = index;
            }

            public void Start()
            {
                if(SyncSource.Value != PROTIMER_TimeoutCounterSource.Disabled)
                {
                    Synchronizing.Value = true;
                    Running.Value = false;
                }
                else
                {
                    Running.Value = true;
                    Synchronizing.Value = false;
                    // Hardware underflows after exactly CounterTop precounter cycles. The Update()
                    // logic below requires an extra full precounter cycle to detect Counter==0, so we
                    // pre-decrement the loaded count by one to remove that spurious cycle while keeping
                    // the match-at-(0,0) event firing one precounter cycle before the underflow.
                    // TODO: This shifts CounterCompare matches one precounter cycle earlier
                    // (CounterCompare == CounterTop never matches) and makes CounterTop 0 and 1
                    // equivalent. Consider detecting the underflow in Update() instead.
                    Counter.Value = CounterTop.Value > 0 ? CounterTop.Value - 1 : 0;
                    PreCounter.Value = PreCounterTop.Value;
                }
                parent.PROTIMER_HandleChangedParams();
            }

            public void Stop()
            {
                Running.Value = false;
                Synchronizing.Value = false;
                Finished?.Invoke();
                parent.PROTIMER_HandleChangedParams();
            }

            public void Update(PROTIMER_TimeoutCounterSource evt, uint evtCount = 1)
            {
                // TODO: handle evtCount > PROTIMER_MinimumTimeoutCounterDelay
                if((Running.Value || Synchronizing.Value) && evtCount > PROTIMER_MinimumTimeoutCounterDelay)
                {
                    parent.Log(LogLevel.Error, "TOUT{0} Update() passed an evtCount > PROTIMER_MinimumTimeoutCounterDelay ({1})", index, evtCount);
                }

                while(evtCount > 0)
                {
                    if(Running.Value && Source.Value == evt)
                    {
                        if(PreCounter.Value == 0)
                        {
                            PreCounter.Value = PreCounterTop.Value;

                            if(Counter.Value == 0)
                            {
                                UnderflowInterrupt.Value = true;
                                SeqUnderflowInterrupt.Value = true;

                                if(Mode.Value == PROTIMER_RepeatMode.OneShot)
                                {
                                    Running.Value = false;
                                    Finished?.Invoke();
                                }
                                else
                                {
                                    // See note in Start(): pre-decrement to remove the spurious extra precounter cycle.
                                    Counter.Value = CounterTop.Value > 0 ? CounterTop.Value - 1 : 0;
                                    PreCounter.Value = PreCounterTop.Value;
                                }
                                parent.PROTIMER_TriggerEvent((PROTIMER_Event)((uint)PROTIMER_Event.TimeoutCounter0Underflow + this.index));
                                Underflowed?.Invoke();
                            }
                            else
                            {
                                Counter.Value -= 1;
                            }
                        }
                        else
                        {
                            PreCounter.Value -= 1;
                        }

                        var match = (Counter.Value == CounterCompare.Value && PreCounter.Value == PreCounterCompare.Value);

                        if(match)
                        {
                            MatchInterrupt.Value |= match;
                            SeqMatchInterrupt.Value |= match;
                            parent.UpdateInterrupts();
                            parent.PROTIMER_TriggerEvent((PROTIMER_Event)((uint)PROTIMER_Event.TimeoutCounter0Match + this.index));
                        }
                    }

                    if(Synchronizing.Value && SyncSource.Value == evt)
                    {
                        Synchronizing.Value = false;
                        Running.Value = true;
                        // See note in Start(): pre-decrement to remove the spurious extra precounter cycle.
                        Counter.Value = CounterTop.Value > 0 ? CounterTop.Value - 1 : 0;
                        PreCounter.Value = PreCounterTop.Value;
                        Synchronized?.Invoke();
                    }

                    evtCount--;
                }
            }

            public bool Interrupt => (UnderflowInterrupt.Value && UnderflowInterruptEnable.Value)
                                      || (MatchInterrupt.Value && MatchInterruptEnable.Value);

            public bool SeqInterrupt => (SeqUnderflowInterrupt.Value && SeqUnderflowInterruptEnable.Value)
                                         || (SeqMatchInterrupt.Value && SeqMatchInterruptEnable.Value);

            public event Action Synchronized;

            public event Action Underflowed;

            public event Action Finished;

            public IEnumRegisterField<PROTIMER_TimeoutCounterSource> Source;
            public IValueRegisterField PreCounterTop;
            public IValueRegisterField CounterTop;
            public IValueRegisterField PreCounterCompare;
            public IValueRegisterField PreCounter;
            public IValueRegisterField CounterCompare;
            public IValueRegisterField Counter;
            public IFlagRegisterField SeqMatchInterruptEnable;
            public IFlagRegisterField SeqUnderflowInterrupt;
            public IFlagRegisterField SeqUnderflowInterruptEnable;
            public IEnumRegisterField<PROTIMER_TimeoutCounterSource> SyncSource;
            public IFlagRegisterField MatchInterruptEnable;
            public IFlagRegisterField MatchInterrupt;
            public IFlagRegisterField UnderflowInterruptEnable;
            public IFlagRegisterField UnderflowInterrupt;
            public IFlagRegisterField Running;
            public IFlagRegisterField Synchronizing;
            public IFlagRegisterField SeqMatchInterrupt;
            public IEnumRegisterField<PROTIMER_RepeatMode> Mode;

            private readonly uint index;
            private readonly SiLabs_xG24_LPW parent;
        }

        private class FRC_FrameDescriptor
        {
            // Hardware semantics: WORDS is the number of words consumed by the subframe minus one
            // (an 8-bit field on this part). The FRC sequencer clamps each subframe to (WORDS+1) and
            // keeps iterating until WCNT reaches FRAMELENGTH. This value is only meaningful when
            // UnlimitedWords is false: 0xFF is a sentinel, not a 256-word subframe, so the
            // assemble/disassemble loops must check UnlimitedWords before clamping to it.
            public uint Words => (uint)(WordsField.Value + 1);

            // WORDS=0xFF selects an unlimited subframe: it runs to the end of the frame and the subframe
            // counter is not used.
            public bool UnlimitedWords => WordsField.Value == 0xFF;

            public uint BufferIndex => (uint)BufferField.Value;

            public IValueRegisterField WordsField;
            public IValueRegisterField BufferField;
            // INCLUDECRC inserts (TX) or extracts (RX) the CRC at the end of the subframe, while CALCCRC
            // only selects whether the subframe's words feed the CRC accumulator. CALCCRC has no observable
            // effect while CRC_CalculateCRC() is mocked.
            public IFlagRegisterField IncludeCrc;
            public IFlagRegisterField CalculateCrc;
            public IValueRegisterField CrcSkipWords;
            public IFlagRegisterField SkipWhitening;
            public IFlagRegisterField AddTrailData;
            public IFlagRegisterField ExcludeSubframeFromWcnt;
        }

        private enum FRC_FSMState
        {
            Idle                = 0x00,
            RxInit              = 0x01,
            RxData              = 0x02,
            RxCrc               = 0x03,
            RxFcdUpdate         = 0x04,
            RxDiscard           = 0x05,
            RxTrail             = 0x06,
            RxDone              = 0x07,
            RxPauseInit         = 0x08,
            RxPaused            = 0x09,
            Undefined1          = 0x0A,
            Undefined2          = 0x0B,
            RxCrcZeroCheck      = 0x0C,
            RxSup               = 0x0D,
            RxWaitEof           = 0x0E,
            Undefined3          = 0x0F,
            TxInit              = 0x10,
            TxData              = 0x11,
            TxCrc               = 0x12,
            TxFdcUpdate         = 0x13,
            TxTrail             = 0x14,
            TxFlush             = 0x15,
            TxDone              = 0x16,
            TxDoneWait          = 0x17,
            TxRaw               = 0x18,
            TxPauseFlush        = 0x19,
        }

        private enum FRC_RxRawDataMode
        {
            Disable = 0x0,
            SingleItem = 0x1,
            SingleBuffer = 0x2,
            SingleBufferFrame = 0x3,
            RepeatBuffer = 0x4,
        }

        private enum FRC_RxRawDataTriggerMode
        {
            Immediate = 0x0,
            PRS = 0x1,
            InternalSignal = 0x2,
        }

        private enum FRC_DynamicFrameLengthMode
        {
            Disable = 0x0,
            SingleByte = 0x1,
            SingleByteMSB = 0x2,
            DualByteLSBFirst = 0x3,
            DualByteMSBFirst = 0x4,
            Infinite = 0x5,
            BlockError = 0x6,
        }

        private enum FRC_DynamicFrameLengthBitOrder
        {
            Normal = 0x0,
            Reverse = 0x1,
        }

        private enum FRC_FrameDescriptorMode
        {
            FrameDescriptorMode0 = 0x0,
            FrameDescriptorMode1 = 0x1,
            FrameDescriptorMode2 = 0x2,
            FrameDescriptorMode3 = 0x3,
        }

        private enum RAC_RadioState
        {
            Off                 = 0,
            RxWarm              = 1,
            RxSearch            = 2,
            RxFrame             = 3,
            RxPoweringDown      = 4,
            Rx2Rx               = 5,
            RxOverflow          = 6,
            Rx2Tx               = 7,
            TxWarm              = 8,
            Tx                  = 9,
            TxPoweringDown      = 10,
            Tx2Rx               = 11,
            Tx2Tx               = 12,
            Shutdown            = 13,
            PowerOnReset        = 14,
        }

        private enum RAC_RadioStateMachineSignal
        {
            None                    = 0,
            Reset                   = 1,
            ForceDisable            = 2,
            ForceTx                 = 3,
            RxEnable                = 4,
            TxEnable                = 5,
            RxDone                  = 6,
            TxDone                  = 7,
            FrameDetected           = 8,
            RxCalibration           = 9,
            RxOverflow              = 10,
            SequencerDelay          = 11,
            SequencerEnd            = 12,
            RxDisable               = 13,
            TxAfterFrameActive      = 14,
            TxAfterFramePending     = 15,
            TxDisable               = 16,
            TxWarmIrqCleared        = 17,
            RxWarmIrqCleared        = 18,
            RxPowerDownIrqCleared   = 19,
            RxOverflowIrqCleared    = 20,
            Rx2RxIrqCleared         = 21,
            Rx2TxIrqCleared         = 22,
            Tx2RxIrqCleared         = 23,
            Tx2TxIrqCleared         = 24,
            TxIrqCleared            = 25,
            TxPowerDownIrqCleared   = 26,
            RxSearchIrqCleared      = 27,
            RxFrameIrqCleared       = 28,
            ShutdownIrqCleared      = 29,
            ClearRxOverflow         = 30,
            RxAbort                 = 31,
        }

        private enum RAC_RxEnableSource
        {
            Software0           = 0x0001,
            Software1           = 0x0002,
            Software2           = 0x0004,
            Software3           = 0x0008,
            Software4           = 0x0010,
            Software5           = 0x0020,
            Software6           = 0x0040,
            Software7           = 0x0080,
            ChannelBusy         = 0x0100,
            TimingDetected      = 0x0200,
            PreambleDetected    = 0x0400,
            FrameDetected       = 0x0800,
            DemodRxRequest      = 0x1000,
            PRS                 = 0x2000,
        }

        private enum RAC_SeqTimerCompareAction
        {
            Wrap                = 0x0,
            Continue            = 0x1,
        }

        // Determines when the STIMERCOMP value is invalid.
        private enum RAC_SeqTimerCompareInvalidMode
        {
            Never                       = 0x0,
            StateChange                 = 0x1,
            CompareEvent                = 0x2,
            StateChangeAndCompareEvent  = 0x3,
        }

        [Flags]
        private enum RAC_RxFailReason
        {
            None = 0,
            Collision = 1 << 0,
            TxAborted = 1 << 1,
        }

        private enum RAC_InternalRxState
        {
            Idle                = 0,
            PreambleAndSyncWord = 1,
            Frame               = 2,
        }

        private enum RAC_InternalTxState
        {
            Idle         = 0,
            Tx           = 1,
        }

        private enum PROTIMER_PreCounterSource
        {
            None    = 0x0,
            Clock   = 0x1,
            Unused0 = 0x2,
            Unused1 = 0x3,
        }

        private enum PROTIMER_BaseCounterSource
        {
            None                = 0x0,
            PreCounterOverflow  = 0x1,
            Unused0             = 0x2,
            Unused1             = 0x3,
        }

        private enum PROTIMER_WrapCounterSource
        {
            None                = 0x0,
            PreCounterOverflow  = 0x1,
            BaseCounterOverflow = 0x2,
            Unused              = 0x3,
        }

        private enum PROTIMER_TimeoutCounterSource
        {
            Disabled            = 0x0,
            PreCounterOverflow  = 0x1,
            BaseCounterOverflow = 0x2,
            WrapCounterOverflow = 0x3,
        }

        private enum PROTIMER_RepeatMode
        {
            Free    = 0x0,
            OneShot = 0x1,
        }

        private enum PROTIMER_CaptureCompareMode
        {
            Compare = 0x0,
            Capture = 0x1,
        }

        private enum PROTIMER_CaptureInputSource
        {
            PRS                             = 0x0,
            TxDone                          = 0x1,
            RxDone                          = 0x2,
            TxOrRxDone                      = 0x3,
            DemodulatorFoundSyncWord0       = 0x4,
            DemodulatorFoundSyncWord1       = 0x5,
            DemodulatorFoundSyncWord0or1    = 0x6,
            ModulatorSyncWordSent           = 0x7,
            RxAtEndOfFrameFromDemodulator   = 0x8,
            ProRtcCaptureCompare0           = 0x9,
            ProRtcCaptureCompare1           = 0xA,
        }

        private enum PROTIMER_CaptureInputEdge
        {
            Rising      = 0x0,
            Falling     = 0x1,
            Both        = 0x2,
            Disabled    = 0x3,
        }

        private enum PROTIMER_OutputAction
        {
            Disabled    = 0x0,
            Toggle      = 0x1,
            Clear       = 0x2,
            Set         = 0x3,
        }

        private enum PROTIMER_OverflowOutputActionCounter
        {
            PreCounter  = 0x0,
            BaseCounter = 0x1,
            WrapCounrer = 0x2,
            Disabled    = 0x3,
        }

        private enum PROTIMER_ListenBeforeTalkState
        {
            Idle        = 0,
            Backoff     = 1,
            CcaDelay    = 2,
        }

        private enum PROTIMER_TxRxRequestState
        {
            Idle,
            SetEvent1,
            Set,
            ClearEvent1,
        }

        private enum PROTIMER_PreCountOverflowSourced : uint
        {
            BaseCounter            = 0x0001,
            WrapCounter            = 0x0002,
            TimeoutCounter0        = 0x0004,
            TimeoutCounter1        = 0x0008,
            CaptureCompareChannel0 = 0x0010,
            CaptureCompareChannel1 = 0x0020,
            CaptureCompareChannel2 = 0x0040,
            CaptureCompareChannel3 = 0x0080,
            CaptureCompareChannel4 = 0x0100,
            CaptureCompareChannel5 = 0x0200,
            CaptureCompareChannel6 = 0x0400,
            CaptureCompareChannel7 = 0x0800,
        }

        private enum MODEM_DsssShiftedSymbols
        {
            ShiftedSymbol0 = 0,
            ShiftedSymbol1 = 1,
            ShiftedSymbol3 = 3,
            ShiftedSymbol7 = 7,
        }

        private enum MODEM_DsssDoublingMode
        {
            Disabled          = 0,
            Inverted          = 1,
            ComplexConjugated = 2,
        }

        private enum MODEM_ModulationFormat
        {
            FSK2     = 0,
            FSK4     = 1,
            BPSK     = 2,
            DBPSK    = 3,
            OQPSK    = 4,
            MSK      = 5,
            OOKASK   = 6,
            MOOK     = 7,
        }

        private enum MODEM_SymbolCoding
        {
            Nrz        = 0,
            Manchester = 1,
            Dsss       = 2,
            Linecode   = 3,
        }

        private enum MODEM_RateSelectMode
        {
            NOCHANGE = 0,
            PAYLOAD = 1,
            FRC = 2,
            SYNC = 3,
        }

        private enum CRC_CrcWidthMode
        {
            Width8 = 0x0,
            Width16 = 0x1,
            Width24 = 0x2,
            Width32 = 0x3,
        }

        private enum AGC_CcaMode
        {
            Mode1 = 0x0,
            Mode2 = 0x1,
            Mode3 = 0x2,
            Mode4 = 0x3,
        }

        private enum AGC_CcaMode3Logic
        {
            Or  = 0x0,
            And = 0x1,
        }

        private enum AGC_RssiState
        {
            Idle          = 0x0,
            Condition     = 0x1,
            Period        = 0x2,
            Command       = 0x3,
            FameDetection = 0x4,
        }

        private enum SYNTH_LoDivFreqCtrl
        {
            LoDiv1  = 0x01,
            LoDiv2  = 0x02,
            LoDiv3  = 0x03,
            LoDiv4  = 0x04,
            LoDiv5  = 0x05,
            LoDiv7  = 0x07,
            LoDiv6  = 0x13,
            LoDiv8  = 0x14,
            LoDiv10 = 0x15,
            LoDiv14 = 0x17,
            LoDiv9  = 0x1B,
            LoDiv12 = 0x1C,
            LoDiv15 = 0x1D,
            LoDiv16 = 0x24,
            LoDiv20 = 0x25,
            LoDiv18 = 0x9B,
            LoDiv24 = 0x9C,
        }

        [RegistersDescription("frc", "frc_ns")]
        private enum FrameControllerRegisters : long
        {
            IpVersion                                                       = 0x0000,
            Enable                                                          = 0x0004,
            Status                                                          = 0x0008,
            DynamicFrameLengthControl                                       = 0x000C,
            MaximumFrameLength                                              = 0x0010,
            AddressFilterControl                                            = 0x0014,
            FrameControllerDataBuffer                                       = 0x0018,
            WordCounter                                                     = 0x001C,
            WordCounterCompare0                                             = 0x0020,
            WordCounterCompare1                                             = 0x0024,
            WordCounterCompare2                                             = 0x0028,
            Command                                                         = 0x002C,
            WhitenerControl                                                 = 0x0030,
            WhitenerPolynomial                                              = 0x0034,
            WhitenerInitialValue                                            = 0x0038,
            ForwardErrorCorrectionControl                                   = 0x003C,
            BlockDecodingRamAddress                                         = 0x0040,
            ConvolutionalDecodingRamAddress                                 = 0x0044,
            Control                                                         = 0x0048,
            RxControl                                                       = 0x004C,
            TrailingTxDataControl                                           = 0x0050,
            TrailingRxData                                                  = 0x0054,
            SubFrameCounterValue                                            = 0x0058,
            ConvolutionalCoderGeneratorPolynomials                          = 0x005C,
            PuncturingControl                                               = 0x0060,
            PauseControl                                                    = 0x0064,
            InterruptFlags                                                  = 0x0068,
            InterruptEnable                                                 = 0x006C,
            OverTheAirNumberOfBitsCounter                                   = 0x0070,
            BufferControl                                                   = 0x0078,
            SnifferControl                                                  = 0x0084,
            AuxiliarySnifferDataOutput                                      = 0x0088,
            RawDataControl                                                  = 0x008C,
            RxRawData                                                       = 0x0090,
            RxPauseData                                                     = 0x0094,
            MostLikelyConvolutionalDecoderState                             = 0x0098,
            InterleaverElementValue                                         = 0x009C,
            InterleaverWritePointer                                         = 0x00A0,
            InterleaverReadPointer                                          = 0x00A4,
            AutomaticClockGating                                            = 0x00A8,
            AutomaticClockGatingClockStop                                   = 0x00AC,
            SequencerInterruptFlags                                         = 0x00B4,
            SequencerInterruptEnable                                        = 0x00B8,
            WordCounterCompare3                                             = 0x00BC,
            BitOfInterestControl                                            = 0x00C0,
            DynamicSuppLengthControl                                        = 0x00C4,
            WordCounterCompare4                                             = 0x00C8,
            WordCounterCompare5                                             = 0x00CC,
            PacketCaptureBufferControl                                      = 0x00D0,
            PacketCaptureBufferStatus                                       = 0x00D4,
            PacketCaptureDataBuffer0                                        = 0x00D8,
            PacketCaptureDataBuffer1                                        = 0x00DC,
            PacketCaptureDataBuffer2                                        = 0x00E0,
            PacketCaptureDataBuffer3                                        = 0x00E4,
            PacketCaptureDataBuffer4                                        = 0x00E8,
            PacketCaptureDataBuffer5                                        = 0x00EC,
            PacketCaptureDataBuffer6                                        = 0x00F0,
            PacketCaptureDataBuffer7                                        = 0x00F4,
            PacketCaptureDataBuffer8                                        = 0x00F8,
            PacketCaptureDataBuffer9                                        = 0x00FC,
            PacketCaptureDataBuffer10                                       = 0x0100,
            PacketCaptureDataBuffer11                                       = 0x0104,
            FrameControlDescriptor0                                         = 0x0108,
            FrameControlDescriptor1                                         = 0x010C,
            FrameControlDescriptor2                                         = 0x0110,
            FrameControlDescriptor3                                         = 0x0114,
            InterleaverElementValue0                                        = 0x0140,
            InterleaverElementValue1                                        = 0x0144,
            InterleaverElementValue2                                        = 0x0148,
            InterleaverElementValue3                                        = 0x014C,
            InterleaverElementValue4                                        = 0x0150,
            InterleaverElementValue5                                        = 0x0154,
            InterleaverElementValue6                                        = 0x0158,
            InterleaverElementValue7                                        = 0x015C,
            InterleaverElementValue8                                        = 0x0160,
            InterleaverElementValue9                                        = 0x0164,
            InterleaverElementValue10                                       = 0x0168,
            InterleaverElementValue11                                       = 0x016C,
            InterleaverElementValue12                                       = 0x0170,
            InterleaverElementValue13                                       = 0x0174,
            InterleaverElementValue14                                       = 0x0178,
            InterleaverElementValue15                                       = 0x017C,
            AhbConfiguration                                                = 0x0180,

            // Set Registers
            IpVersion_Set                                                   = 0x1000,
            Enable_Set                                                      = 0x1004,
            Status_Set                                                      = 0x1008,
            DynamicFrameLengthControl_Set                                   = 0x100C,
            MaximumFrameLength_Set                                          = 0x1010,
            AddressFilterControl_Set                                        = 0x1014,
            FrameControllerDataBuffer_Set                                   = 0x1018,
            WordCounter_Set                                                 = 0x101C,
            WordCounterCompare0_Set                                         = 0x1020,
            WordCounterCompare1_Set                                         = 0x1024,
            WordCounterCompare2_Set                                         = 0x1028,
            Command_Set                                                     = 0x102C,
            WhitenerControl_Set                                             = 0x1030,
            WhitenerPolynomial_Set                                          = 0x1034,
            WhitenerInitialValue_Set                                        = 0x1038,
            ForwardErrorCorrectionControl_Set                               = 0x103C,
            BlockDecodingRamAddress_Set                                     = 0x1040,
            ConvolutionalDecodingRamAddress_Set                             = 0x1044,
            Control_Set                                                     = 0x1048,
            RxControl_Set                                                   = 0x104C,
            TrailingTxDataControl_Set                                       = 0x1050,
            TrailingRxData_Set                                              = 0x1054,
            SubFrameCounterValue_Set                                        = 0x1058,
            ConvolutionalCoderGeneratorPolynomials_Set                      = 0x105C,
            PuncturingControl_Set                                           = 0x1060,
            PauseControl_Set                                                = 0x1064,
            InterruptFlags_Set                                              = 0x1068,
            InterruptEnable_Set                                             = 0x106C,
            OverTheAirNumberOfBitsCounter_Set                               = 0x1070,
            BufferControl_Set                                               = 0x1078,
            SnifferControl_Set                                              = 0x1084,
            AuxiliarySnifferDataOutput_Set                                  = 0x1088,
            RawDataControl_Set                                              = 0x108C,
            RxRawData_Set                                                   = 0x1090,
            RxPauseData_Set                                                 = 0x1094,
            MostLikelyConvolutionalDecoderState_Set                         = 0x1098,
            InterleaverElementValue_Set                                     = 0x109C,
            InterleaverWritePointer_Set                                     = 0x10A0,
            InterleaverReadPointer_Set                                      = 0x10A4,
            AutomaticClockGating_Set                                        = 0x10A8,
            AutomaticClockGatingClockStop_Set                               = 0x10AC,
            SequencerInterruptFlags_Set                                     = 0x10B4,
            SequencerInterruptEnable_Set                                    = 0x10B8,
            WordCounterCompare3_Set                                         = 0x10BC,
            BitOfInterestControl_Set                                        = 0x10C0,
            DynamicSuppLengthControl_Set                                    = 0x10C4,
            WordCounterCompare4_Set                                         = 0x10C8,
            WordCounterCompare5_Set                                         = 0x10CC,
            PacketCaptureBufferControl_Set                                  = 0x10D0,
            PacketCaptureBufferStatus_Set                                   = 0x10D4,
            PacketCaptureDataBuffer0_Set                                    = 0x10D8,
            PacketCaptureDataBuffer1_Set                                    = 0x10DC,
            PacketCaptureDataBuffer2_Set                                    = 0x10E0,
            PacketCaptureDataBuffer3_Set                                    = 0x10E4,
            PacketCaptureDataBuffer4_Set                                    = 0x10E8,
            PacketCaptureDataBuffer5_Set                                    = 0x10EC,
            PacketCaptureDataBuffer6_Set                                    = 0x10F0,
            PacketCaptureDataBuffer7_Set                                    = 0x10F4,
            PacketCaptureDataBuffer8_Set                                    = 0x10F8,
            PacketCaptureDataBuffer9_Set                                    = 0x10FC,
            PacketCaptureDataBuffer10_Set                                   = 0x1100,
            PacketCaptureDataBuffer11_Set                                   = 0x1104,
            FrameControlDescriptor0_Set                                     = 0x1108,
            FrameControlDescriptor1_Set                                     = 0x110C,
            FrameControlDescriptor2_Set                                     = 0x1110,
            FrameControlDescriptor3_Set                                     = 0x1114,
            InterleaverElementValue0_Set                                    = 0x1140,
            InterleaverElementValue1_Set                                    = 0x1144,
            InterleaverElementValue2_Set                                    = 0x1148,
            InterleaverElementValue3_Set                                    = 0x114C,
            InterleaverElementValue4_Set                                    = 0x1150,
            InterleaverElementValue5_Set                                    = 0x1154,
            InterleaverElementValue6_Set                                    = 0x1158,
            InterleaverElementValue7_Set                                    = 0x115C,
            InterleaverElementValue8_Set                                    = 0x1160,
            InterleaverElementValue9_Set                                    = 0x1164,
            InterleaverElementValue10_Set                                   = 0x1168,
            InterleaverElementValue11_Set                                   = 0x116C,
            InterleaverElementValue12_Set                                   = 0x1170,
            InterleaverElementValue13_Set                                   = 0x1174,
            InterleaverElementValue14_Set                                   = 0x1178,
            InterleaverElementValue15_Set                                   = 0x117C,
            AhbConfiguration_Set                                            = 0x1180,

            // Set Registers
            IpVersion_Clr                                                   = 0x2000,
            Enable_Clr                                                      = 0x2004,
            Status_Clr                                                      = 0x2008,
            DynamicFrameLengthControl_Clr                                   = 0x200C,
            MaximumFrameLength_Clr                                          = 0x2010,
            AddressFilterControl_Clr                                        = 0x2014,
            FrameControllerDataBuffer_Clr                                   = 0x2018,
            WordCounter_Clr                                                 = 0x201C,
            WordCounterCompare0_Clr                                         = 0x2020,
            WordCounterCompare1_Clr                                         = 0x2024,
            WordCounterCompare2_Clr                                         = 0x2028,
            Command_Clr                                                     = 0x202C,
            WhitenerControl_Clr                                             = 0x2030,
            WhitenerPolynomial_Clr                                          = 0x2034,
            WhitenerInitialValue_Clr                                        = 0x2038,
            ForwardErrorCorrectionControl_Clr                               = 0x203C,
            BlockDecodingRamAddress_Clr                                     = 0x2040,
            ConvolutionalDecodingRamAddress_Clr                             = 0x2044,
            Control_Clr                                                     = 0x2048,
            RxControl_Clr                                                   = 0x204C,
            TrailingTxDataControl_Clr                                       = 0x2050,
            TrailingRxData_Clr                                              = 0x2054,
            SubFrameCounterValue_Clr                                        = 0x2058,
            ConvolutionalCoderGeneratorPolynomials_Clr                      = 0x205C,
            PuncturingControl_Clr                                           = 0x2060,
            PauseControl_Clr                                                = 0x2064,
            InterruptFlags_Clr                                              = 0x2068,
            InterruptEnable_Clr                                             = 0x206C,
            OverTheAirNumberOfBitsCounter_Clr                               = 0x2070,
            BufferControl_Clr                                               = 0x2078,
            SnifferControl_Clr                                              = 0x2084,
            AuxiliarySnifferDataOutput_Clr                                  = 0x2088,
            RawDataControl_Clr                                              = 0x208C,
            RxRawData_Clr                                                   = 0x2090,
            RxPauseData_Clr                                                 = 0x2094,
            MostLikelyConvolutionalDecoderState_Clr                         = 0x2098,
            InterleaverElementValue_Clr                                     = 0x209C,
            InterleaverWritePointer_Clr                                     = 0x20A0,
            InterleaverReadPointer_Clr                                      = 0x20A4,
            AutomaticClockGating_Clr                                        = 0x20A8,
            AutomaticClockGatingClockStop_Clr                               = 0x20AC,
            SequencerInterruptFlags_Clr                                     = 0x20B4,
            SequencerInterruptEnable_Clr                                    = 0x20B8,
            WordCounterCompare3_Clr                                         = 0x20BC,
            BitOfInterestControl_Clr                                        = 0x20C0,
            DynamicSuppLengthControl_Clr                                    = 0x20C4,
            WordCounterCompare4_Clr                                         = 0x20C8,
            WordCounterCompare5_Clr                                         = 0x20CC,
            PacketCaptureBufferControl_Clr                                  = 0x20D0,
            PacketCaptureBufferStatus_Clr                                   = 0x20D4,
            PacketCaptureDataBuffer0_Clr                                    = 0x20D8,
            PacketCaptureDataBuffer1_Clr                                    = 0x20DC,
            PacketCaptureDataBuffer2_Clr                                    = 0x20E0,
            PacketCaptureDataBuffer3_Clr                                    = 0x20E4,
            PacketCaptureDataBuffer4_Clr                                    = 0x20E8,
            PacketCaptureDataBuffer5_Clr                                    = 0x20EC,
            PacketCaptureDataBuffer6_Clr                                    = 0x20F0,
            PacketCaptureDataBuffer7_Clr                                    = 0x20F4,
            PacketCaptureDataBuffer8_Clr                                    = 0x20F8,
            PacketCaptureDataBuffer9_Clr                                    = 0x20FC,
            PacketCaptureDataBuffer10_Clr                                   = 0x2100,
            PacketCaptureDataBuffer11_Clr                                   = 0x2104,
            FrameControlDescriptor0_Clr                                     = 0x2108,
            FrameControlDescriptor1_Clr                                     = 0x210C,
            FrameControlDescriptor2_Clr                                     = 0x2110,
            FrameControlDescriptor3_Clr                                     = 0x2114,
            InterleaverElementValue0_Clr                                    = 0x2140,
            InterleaverElementValue1_Clr                                    = 0x2144,
            InterleaverElementValue2_Clr                                    = 0x2148,
            InterleaverElementValue3_Clr                                    = 0x214C,
            InterleaverElementValue4_Clr                                    = 0x2150,
            InterleaverElementValue5_Clr                                    = 0x2154,
            InterleaverElementValue6_Clr                                    = 0x2158,
            InterleaverElementValue7_Clr                                    = 0x215C,
            InterleaverElementValue8_Clr                                    = 0x2160,
            InterleaverElementValue9_Clr                                    = 0x2164,
            InterleaverElementValue10_Clr                                   = 0x2168,
            InterleaverElementValue11_Clr                                   = 0x216C,
            InterleaverElementValue12_Clr                                   = 0x2170,
            InterleaverElementValue13_Clr                                   = 0x2174,
            InterleaverElementValue14_Clr                                   = 0x2178,
            InterleaverElementValue15_Clr                                   = 0x217C,
            AhbConfiguration_Clr                                            = 0x2180,

            // Toggle Registers
            IpVersion_Tgl                                                   = 0x3000,
            Enable_Tgl                                                      = 0x3004,
            Status_Tgl                                                      = 0x3008,
            DynamicFrameLengthControl_Tgl                                   = 0x300C,
            MaximumFrameLength_Tgl                                          = 0x3010,
            AddressFilterControl_Tgl                                        = 0x3014,
            FrameControllerDataBuffer_Tgl                                   = 0x3018,
            WordCounter_Tgl                                                 = 0x301C,
            WordCounterCompare0_Tgl                                         = 0x3020,
            WordCounterCompare1_Tgl                                         = 0x3024,
            WordCounterCompare2_Tgl                                         = 0x3028,
            Command_Tgl                                                     = 0x302C,
            WhitenerControl_Tgl                                             = 0x3030,
            WhitenerPolynomial_Tgl                                          = 0x3034,
            WhitenerInitialValue_Tgl                                        = 0x3038,
            ForwardErrorCorrectionControl_Tgl                               = 0x303C,
            BlockDecodingRamAddress_Tgl                                     = 0x3040,
            ConvolutionalDecodingRamAddress_Tgl                             = 0x3044,
            Control_Tgl                                                     = 0x3048,
            RxControl_Tgl                                                   = 0x304C,
            TrailingTxDataControl_Tgl                                       = 0x3050,
            TrailingRxData_Tgl                                              = 0x3054,
            SubFrameCounterValue_Tgl                                        = 0x3058,
            ConvolutionalCoderGeneratorPolynomials_Tgl                      = 0x305C,
            PuncturingControl_Tgl                                           = 0x3060,
            PauseControl_Tgl                                                = 0x3064,
            InterruptFlags_Tgl                                              = 0x3068,
            InterruptEnable_Tgl                                             = 0x306C,
            OverTheAirNumberOfBitsCounter_Tgl                               = 0x3070,
            BufferControl_Tgl                                               = 0x3078,
            SnifferControl_Tgl                                              = 0x3084,
            AuxiliarySnifferDataOutput_Tgl                                  = 0x3088,
            RawDataControl_Tgl                                              = 0x308C,
            RxRawData_Tgl                                                   = 0x3090,
            RxPauseData_Tgl                                                 = 0x3094,
            MostLikelyConvolutionalDecoderState_Tgl                         = 0x3098,
            InterleaverElementValue_Tgl                                     = 0x309C,
            InterleaverWritePointer_Tgl                                     = 0x30A0,
            InterleaverReadPointer_Tgl                                      = 0x30A4,
            AutomaticClockGating_Tgl                                        = 0x30A8,
            AutomaticClockGatingClockStop_Tgl                               = 0x30AC,
            SequencerInterruptFlags_Tgl                                     = 0x30B4,
            SequencerInterruptEnable_Tgl                                    = 0x30B8,
            WordCounterCompare3_Tgl                                         = 0x30BC,
            BitOfInterestControl_Tgl                                        = 0x30C0,
            DynamicSuppLengthControl_Tgl                                    = 0x30C4,
            WordCounterCompare4_Tgl                                         = 0x30C8,
            WordCounterCompare5_Tgl                                         = 0x30CC,
            PacketCaptureBufferControl_Tgl                                  = 0x30D0,
            PacketCaptureBufferStatus_Tgl                                   = 0x30D4,
            PacketCaptureDataBuffer0_Tgl                                    = 0x30D8,
            PacketCaptureDataBuffer1_Tgl                                    = 0x30DC,
            PacketCaptureDataBuffer2_Tgl                                    = 0x30E0,
            PacketCaptureDataBuffer3_Tgl                                    = 0x30E4,
            PacketCaptureDataBuffer4_Tgl                                    = 0x30E8,
            PacketCaptureDataBuffer5_Tgl                                    = 0x30EC,
            PacketCaptureDataBuffer6_Tgl                                    = 0x30F0,
            PacketCaptureDataBuffer7_Tgl                                    = 0x30F4,
            PacketCaptureDataBuffer8_Tgl                                    = 0x30F8,
            PacketCaptureDataBuffer9_Tgl                                    = 0x30FC,
            PacketCaptureDataBuffer10_Tgl                                   = 0x3100,
            PacketCaptureDataBuffer11_Tgl                                   = 0x3104,
            FrameControlDescriptor0_Tgl                                     = 0x3108,
            FrameControlDescriptor1_Tgl                                     = 0x310C,
            FrameControlDescriptor2_Tgl                                     = 0x3110,
            FrameControlDescriptor3_Tgl                                     = 0x3114,
            InterleaverElementValue0_Tgl                                    = 0x3140,
            InterleaverElementValue1_Tgl                                    = 0x3144,
            InterleaverElementValue2_Tgl                                    = 0x3148,
            InterleaverElementValue3_Tgl                                    = 0x314C,
            InterleaverElementValue4_Tgl                                    = 0x3150,
            InterleaverElementValue5_Tgl                                    = 0x3154,
            InterleaverElementValue6_Tgl                                    = 0x3158,
            InterleaverElementValue7_Tgl                                    = 0x315C,
            InterleaverElementValue8_Tgl                                    = 0x3160,
            InterleaverElementValue9_Tgl                                    = 0x3164,
            InterleaverElementValue10_Tgl                                   = 0x3168,
            InterleaverElementValue11_Tgl                                   = 0x316C,
            InterleaverElementValue12_Tgl                                   = 0x3170,
            InterleaverElementValue13_Tgl                                   = 0x3174,
            InterleaverElementValue14_Tgl                                   = 0x3178,
            InterleaverElementValue15_Tgl                                   = 0x317C,
            AhbConfiguration_Tgl                                            = 0x3180,
        }

        [RegistersDescription("modem", "modem_ns")]
        private enum ModulatorAndDemodulatorRegisters : long
        {
            IpVersion                                                       = 0x0000,
            Enable                                                          = 0x0004,
            InterruptFlags                                                  = 0x0008,
            InterruptEnable                                                 = 0x000C,
            SequencerInterruptFlags                                         = 0x0010,
            SequencerInterruptEnable                                        = 0x0014,
            Status                                                          = 0x0018,
            Status2                                                         = 0x001C,
            Status3                                                         = 0x0020,
            Status4                                                         = 0x0024,
            Status5                                                         = 0x0028,
            Status6                                                         = 0x002C,
            Status7                                                         = 0x0030,
            TimingDetectionStatus                                           = 0x0034,
            DemodulatorFSMStatus                                            = 0x0038,
            FrequencyOffsetEstimate                                         = 0x003C,
            AutomaticFrequencyControlAdjustmentRx                           = 0x0040,
            AutomaticFrequencyControldAjustmentTx                           = 0x0044,
            AnalogMixerControl                                              = 0x0048,
            Control0                                                        = 0x004C,
            Control1                                                        = 0x0050,
            Control2                                                        = 0x0054,
            Control3                                                        = 0x0058,
            Control4                                                        = 0x005C,
            Control5                                                        = 0x0060,
            Control6                                                        = 0x0064,
            TxBaudrate                                                      = 0x0068,
            RxBaudrate                                                      = 0x006C,
            ChannelFilter                                                   = 0x0070,
            Preamble                                                        = 0x0074,
            SyncWord0                                                       = 0x0078,
            SyncWord1                                                       = 0x007C,
            Timing                                                          = 0x0080,
            DirectSequenceSpreadSpectrumSymbol0                             = 0x0084,
            ModulationIndex                                                 = 0x0088,
            AutomaticFrequencyControl                                       = 0x008C,
            AutomaticFrequencyControlAdjustmentLimit                        = 0x0090,
            ShapingCoefficients0                                            = 0x0094,
            ShapingCoefficients1                                            = 0x0098,
            ShapingCoefficients2                                            = 0x009C,
            ShapingCoefficients3                                            = 0x00A0,
            ShapingCoefficients4                                            = 0x00A4,
            ShapingCoefficients5                                            = 0x00A8,
            ShapingCoefficients6                                            = 0x00AC,
            ShapingCoefficients7                                            = 0x00B0,
            ShapingCoefficients8                                            = 0x00B4,
            ShapingCoefficients9                                            = 0x00B8,
            ShapingCoefficients10                                           = 0x00BC,
            ShapingCoefficients11                                           = 0x00C0,
            ShapingCoefficients12                                           = 0x00C4,
            ShapingCoefficients13                                           = 0x00C8,
            ShapingCoefficients14                                           = 0x00CC,
            ShapingCoefficients15                                           = 0x00D0,
            RampingControl                                                  = 0x00D8,
            RampingLevels                                                   = 0x00DC,
            AnalogRampingControl                                            = 0x00E0,
            DirectCurrentOffsetCompensationFilterSettings                   = 0x0110,
            DirectCurrentOffsetCompensationFilterInitialization             = 0x0114,
            DirectCurrentOffsetEstimatedValue                               = 0x0118,
            SampleRateConverterRatioValuesAndChannelFilter                  = 0x011C,
            InternalAutomaticFrequencyControl                               = 0x0120,
            DetectionOfSignalArrivalThreshold0                              = 0x0124,
            DetectionOfSignalArrivalThreshold1                              = 0x0128,
            DetectionOfSignalArrivalThreshold2                              = 0x012C,
            DetectionOfSignalArrivalThreshold3                              = 0x0130,
            DetectionOfSignalArrivalThreshold4                              = 0x0134,
            DetectionOfSignalArrivalMode                                    = 0x0138,
            DigitalMixerControl                                             = 0x013C,
            ViterbiDemodulator                                              = 0x0140,
            ViterbiDemodulatorCorrelationConfiguration0                     = 0x0144,
            ViterbiDemodulatorCorrelationConfiguration1                     = 0x0148,
            ViterbiDemodulatorTrackingLoop                                  = 0x014C,
            ViterbiBleTimingStampControl                                    = 0x0150,
            BaudrateEstimate                                                = 0x0154,
            AutomaticClockGating                                            = 0x0158,
            AutomaticClockGatingClockStop                                   = 0x015C,
            PhaseOffsetEstimate                                             = 0x0160,
            DirectModeControl                                               = 0x0164,
            BleLongRange                                                    = 0x0168,
            BleLongRangeSet1                                                = 0x016C,
            BleLongRangeSet2                                                = 0x0170,
            BleLongRangeSet3                                                = 0x0174,
            BleLongRangeSet4                                                = 0x0178,
            BleLongRangeSet5                                                = 0x017C,
            BleLongRangeSet6                                                = 0x0180,
            BleLongRangeFrameControllerInterface                            = 0x0184,
            CoherentDemodulatorSignals0                                     = 0x0188,
            CoherentDemodulatorSignals1                                     = 0x018C,
            CoherentDemodulatorSignals2                                     = 0x0190,
            CoherentDemodulatorSignals3                                     = 0x0194,
            Command                                                         = 0x0198,
            SyncWordProperties                                              = 0x01A4,
            DigitalGainControl                                              = 0x01A8,
            PeripheralReflexSystemControl                                   = 0x01AC,
            PowerAmplifierDebug                                             = 0x01B0,
            RealTimeCostFunctionEngineControl                               = 0x01B4,
            EarlyTimeStampControl                                           = 0x01B8,
            EarlyTimeStampTiming                                            = 0x01BC,
            AntennaSwitchControl                                            = 0x01C0,
            AntennaSwitchControl1                                           = 0x01C4,
            AntennaSwitchStart                                              = 0x01C8,
            AntennaSwitchEnd                                                = 0x01CC,
            TrecsPreamblePattern                                            = 0x01D0,
            TrecsPreambleDetectionControl                                   = 0x01D4,
            TrecsConfiguration                                              = 0x01D8,
            ConfigureAntennaPattern                                         = 0x01DC,
            ConcurrentMode                                                  = 0x01E0,
            ChannelFilterCoeSet0Group0                                      = 0x01E4,
            ChannelFilterCoeSet0Group1                                      = 0x01E8,
            ChannelFilterCoeSet0Group2                                      = 0x01EC,
            ChannelFilterCoeSet0Group3                                      = 0x01F0,
            ChannelFilterCoeSet0Group4                                      = 0x01F4,
            ChannelFilterCoeSet0Group5                                      = 0x01F8,
            ChannelFilterCoeSet0Group6                                      = 0x01FC,
            ChannelFilterCoeSet1Group0                                      = 0x0200,
            ChannelFilterCoeSet1Group1                                      = 0x0204,
            ChannelFilterCoeSet1Group2                                      = 0x0208,
            ChannelFilterCoeSet1Group3                                      = 0x020C,
            ChannelFilterCoeSet1Group4                                      = 0x0210,
            ChannelFilterCoeSet1Group5                                      = 0x0214,
            ChannelFilterCoeSet1Group6                                      = 0x0218,
            ChannelFilterControl                                            = 0x021C,
            ChannelFilterLatencyControl                                     = 0x0220,
            FrameSchTimeoutLength                                           = 0x0224,
            PreambleFilterCoefficients                                      = 0x0228,
            CollisionRestartControl                                         = 0x022C,
            PreambleSenseMode                                               = 0x0230,
            PreambleSenseModeExtended                                       = 0x0234,
            SignalQualityIndicator                                          = 0x0238,
            AntennaDiversityModeControl                                     = 0x023C,
            PhaseDemodulatorFwMode                                          = 0x0240,
            PhaseDemodulatorAntennaDiversity                                = 0x0244,
            PhaseDemodulatorAntennaDiversityDecision                        = 0x0248,
            PhaseDemodulatorControl                                         = 0x024C,
            SignalIdentifierControl0                                        = 0x0250,
            SignalIdentifierControl1                                        = 0x0254,
            SignalIdentifierStatus                                          = 0x0258,
            ConfigureAntennaPatternExtended                                 = 0x025C,
            SignalIdentifierControl2                                        = 0x0264,
            ChannelFilterSwitchTime                                         = 0x0268,
            IrCalibrationControl                                            = 0x0270,
            IrCalCoefficientValues                                          = 0x0274,
            IrCalCoefficientWrPerAntenna0                                   = 0x0278,
            IrCalCoefficientWrPerAntenna1                                   = 0x027C,
            AdControl1                                                      = 0x0280,
            AdControl2                                                      = 0x0284,
            AdQual0                                                         = 0x0288,
            AdQual1                                                         = 0x028C,
            AdQual2                                                         = 0x0290,
            AdQual3                                                         = 0x0294,
            AdQual4                                                         = 0x0298,
            AdQual5                                                         = 0x029C,
            AdQual6                                                         = 0x02A0,
            AdQual7                                                         = 0x02A4,
            AdQual8                                                         = 0x02A8,
            AdQual9                                                         = 0x02AC,
            AdQual10                                                        = 0x02B0,
            AdFsm0                                                          = 0x02B4,
            AdFsm1                                                          = 0x02B8,
            AdFsm2                                                          = 0x02BC,
            AdFsm3                                                          = 0x02C0,
            AdFsm4                                                          = 0x02C4,
            AdFsm5                                                          = 0x02C8,
            AdFsm6                                                          = 0x02CC,
            AdFsm7                                                          = 0x02D0,
            AdFsm8                                                          = 0x02D4,
            AdFsm9                                                          = 0x02D8,
            AdFsm10                                                         = 0x02DC,
            AdFsm11                                                         = 0x02E0,
            AdFsm12                                                         = 0x02E4,
            AdFsm13                                                         = 0x02E8,
            AdFsm14                                                         = 0x02EC,
            AdFsm15                                                         = 0x02F0,
            AdFsm16                                                         = 0x02F4,
            AdFsm17                                                         = 0x02F8,
            AdFsm18                                                         = 0x02FC,
            AdFsm19                                                         = 0x0300,
            AdFsm20                                                         = 0x0304,
            AdFsm21                                                         = 0x0308,
            AdFsm22                                                         = 0x030C,
            AdFsm23                                                         = 0x0310,
            AdFsm24                                                         = 0x0314,
            AdFsm25                                                         = 0x0318,
            AdFsm26                                                         = 0x031C,
            AdFsm27                                                         = 0x0320,
            AdFsm28                                                         = 0x0324,
            AdFsm29                                                         = 0x0328,
            AdFsm30                                                         = 0x032C,
            AdPc1                                                           = 0x0330,
            AdPc2                                                           = 0x0334,
            AdPc3                                                           = 0x0338,
            AdPc4                                                           = 0x033C,
            AdPc5                                                           = 0x0340,
            AdPc6                                                           = 0x0344,
            AdPc7                                                           = 0x0348,
            AdPc8                                                           = 0x034C,
            AdPc9                                                           = 0x0350,
            AdPc10                                                          = 0x0354,
            HadmControl0                                                    = 0x03B0,
            HadmControl1                                                    = 0x03B4,
            HadmStatus0                                                     = 0x03B8,
            HadmStatus1                                                     = 0x03BC,
            HadmStatus2                                                     = 0x03C0,
            HadmStatus3                                                     = 0x03C4,
            HadmStatus4                                                     = 0x03C8,
            HadmStatus5                                                     = 0x03CC,
            HadmStatus6                                                     = 0x03D0,
            Src2NcoControl                                                  = 0x03E0,
            Spare                                                           = 0x0400,

            // Set registers
            IpVersion_Set                                                   = 0x1000,
            Enable_Set                                                      = 0x1004,
            InterruptFlags_Set                                              = 0x1008,
            InterruptEnable_Set                                             = 0x100C,
            SequencerInterruptFlags_Set                                     = 0x1010,
            SequencerInterruptEnable_Set                                    = 0x1014,
            Status_Set                                                      = 0x1018,
            Status2_Set                                                     = 0x101C,
            Status3_Set                                                     = 0x1020,
            Status4_Set                                                     = 0x1024,
            Status5_Set                                                     = 0x1028,
            Status6_Set                                                     = 0x102C,
            Status7_Set                                                     = 0x1030,
            TimingDetectionStatus_Set                                       = 0x1034,
            DemodulatorFSMStatus_Set                                        = 0x1038,
            FrequencyOffsetEstimate_Set                                     = 0x103C,
            AutomaticFrequencyControlAdjustmentRx_Set                       = 0x1040,
            AutomaticFrequencyControldAjustmentTx_Set                       = 0x1044,
            AnalogMixerControl_Set                                          = 0x1048,
            Control0_Set                                                    = 0x104C,
            Control1_Set                                                    = 0x1050,
            Control2_Set                                                    = 0x1054,
            Control3_Set                                                    = 0x1058,
            Control4_Set                                                    = 0x105C,
            Control5_Set                                                    = 0x1060,
            Control6_Set                                                    = 0x1064,
            TxBaudrate_Set                                                  = 0x1068,
            RxBaudrate_Set                                                  = 0x106C,
            ChannelFilter_Set                                               = 0x1070,
            Preamble_Set                                                    = 0x1074,
            SyncWord0_Set                                                   = 0x1078,
            SyncWord1_Set                                                   = 0x107C,
            Timing_Set                                                      = 0x1080,
            DirectSequenceSpreadSpectrumSymbol0_Set                         = 0x1084,
            ModulationIndex_Set                                             = 0x1088,
            AutomaticFrequencyControl_Set                                   = 0x108C,
            AutomaticFrequencyControlAdjustmentLimit_Set                    = 0x1090,
            ShapingCoefficients0_Set                                        = 0x1094,
            ShapingCoefficients1_Set                                        = 0x1098,
            ShapingCoefficients2_Set                                        = 0x109C,
            ShapingCoefficients3_Set                                        = 0x10A0,
            ShapingCoefficients4_Set                                        = 0x10A4,
            ShapingCoefficients5_Set                                        = 0x10A8,
            ShapingCoefficients6_Set                                        = 0x10AC,
            ShapingCoefficients7_Set                                        = 0x10B0,
            ShapingCoefficients8_Set                                        = 0x10B4,
            ShapingCoefficients9_Set                                        = 0x10B8,
            ShapingCoefficients10_Set                                       = 0x10BC,
            ShapingCoefficients11_Set                                       = 0x10C0,
            ShapingCoefficients12_Set                                       = 0x10C4,
            ShapingCoefficients13_Set                                       = 0x10C8,
            ShapingCoefficients14_Set                                       = 0x10CC,
            ShapingCoefficients15_Set                                       = 0x10D0,
            RampingControl_Set                                              = 0x10D8,
            RampingLevels_Set                                               = 0x10DC,
            AnalogRampingControl_Set                                        = 0x10E0,
            DirectCurrentOffsetCompensationFilterSettings_Set               = 0x1110,
            DirectCurrentOffsetCompensationFilterInitialization_Set         = 0x1114,
            DirectCurrentOffsetEstimatedValue_Set                           = 0x1118,
            SampleRateConverterRatioValuesAndChannelFilter_Set              = 0x111C,
            InternalAutomaticFrequencyControl_Set                           = 0x1120,
            DetectionOfSignalArrivalThreshold0_Set                          = 0x1124,
            DetectionOfSignalArrivalThreshold1_Set                          = 0x1128,
            DetectionOfSignalArrivalThreshold2_Set                          = 0x112C,
            DetectionOfSignalArrivalThreshold3_Set                          = 0x1130,
            DetectionOfSignalArrivalThreshold4_Set                          = 0x1134,
            DetectionOfSignalArrivalMode_Set                                = 0x1138,
            DigitalMixerControl_Set                                         = 0x113C,
            ViterbiDemodulator_Set                                          = 0x1140,
            ViterbiDemodulatorCorrelationConfiguration0_Set                 = 0x1144,
            ViterbiDemodulatorCorrelationConfiguration1_Set                 = 0x1148,
            ViterbiDemodulatorTrackingLoop_Set                              = 0x114C,
            ViterbiBleTimingStampControl_Set                                = 0x1150,
            BaudrateEstimate_Set                                            = 0x1154,
            AutomaticClockGating_Set                                        = 0x1158,
            AutomaticClockGatingClockStop_Set                               = 0x115C,
            PhaseOffsetEstimate_Set                                         = 0x1160,
            DirectModeControl_Set                                           = 0x1164,
            BleLongRange_Set                                                = 0x1168,
            BleLongRangeSet1_Set                                            = 0x116C,
            BleLongRangeSet2_Set                                            = 0x1170,
            BleLongRangeSet3_Set                                            = 0x1174,
            BleLongRangeSet4_Set                                            = 0x1178,
            BleLongRangeSet5_Set                                            = 0x117C,
            BleLongRangeSet6_Set                                            = 0x1180,
            BleLongRangeFrameControllerInterface_Set                        = 0x1184,
            CoherentDemodulatorSignals0_Set                                 = 0x1188,
            CoherentDemodulatorSignals1_Set                                 = 0x118C,
            CoherentDemodulatorSignals2_Set                                 = 0x1190,
            CoherentDemodulatorSignals3_Set                                 = 0x1194,
            Command_Set                                                     = 0x1198,
            SyncWordProperties_Set                                          = 0x11A4,
            DigitalGainControl_Set                                          = 0x11A8,
            PeripheralReflexSystemControl_Set                               = 0x11AC,
            PowerAmplifierDebug_Set                                         = 0x11B0,
            RealTimeCostFunctionEngineControl_Set                           = 0x11B4,
            EarlyTimeStampControl_Set                                       = 0x11B8,
            EarlyTimeStampTiming_Set                                        = 0x11BC,
            AntennaSwitchControl_Set                                        = 0x11C0,
            AntennaSwitchControl1_Set                                       = 0x11C4,
            AntennaSwitchStart_Set                                          = 0x11C8,
            AntennaSwitchEnd_Set                                            = 0x11CC,
            TrecsPreamblePattern_Set                                        = 0x11D0,
            TrecsPreambleDetectionControl_Set                               = 0x11D4,
            TrecsConfiguration_Set                                          = 0x11D8,
            ConfigureAntennaPattern_Set                                     = 0x11DC,
            ConcurrentMode_Set                                              = 0x11E0,
            ChannelFilterCoeSet0Group0_Set                                  = 0x11E4,
            ChannelFilterCoeSet0Group1_Set                                  = 0x11E8,
            ChannelFilterCoeSet0Group2_Set                                  = 0x11EC,
            ChannelFilterCoeSet0Group3_Set                                  = 0x11F0,
            ChannelFilterCoeSet0Group4_Set                                  = 0x11F4,
            ChannelFilterCoeSet0Group5_Set                                  = 0x11F8,
            ChannelFilterCoeSet0Group6_Set                                  = 0x11FC,
            ChannelFilterCoeSet1Group0_Set                                  = 0x1200,
            ChannelFilterCoeSet1Group1_Set                                  = 0x1204,
            ChannelFilterCoeSet1Group2_Set                                  = 0x1208,
            ChannelFilterCoeSet1Group3_Set                                  = 0x120C,
            ChannelFilterCoeSet1Group4_Set                                  = 0x1210,
            ChannelFilterCoeSet1Group5_Set                                  = 0x1214,
            ChannelFilterCoeSet1Group6_Set                                  = 0x1218,
            ChannelFilterControl_Set                                        = 0x121C,
            ChannelFilterLatencyControl_Set                                 = 0x1220,
            FrameSchTimeoutLength_Set                                       = 0x1224,
            PreambleFilterCoefficients_Set                                  = 0x1228,
            CollisionRestartControl_Set                                     = 0x122C,
            PreambleSenseMode_Set                                           = 0x1230,
            PreambleSenseModeExtended_Set                                   = 0x1234,
            SignalQualityIndicator_Set                                      = 0x1238,
            AntennaDiversityModeControl_Set                                 = 0x123C,
            PhaseDemodulatorFwMode_Set                                      = 0x1240,
            PhaseDemodulatorAntennaDiversity_Set                            = 0x1244,
            PhaseDemodulatorAntennaDiversityDecision_Set                    = 0x1248,
            PhaseDemodulatorControl_Set                                     = 0x124C,
            SignalIdentifierControl0_Set                                    = 0x1250,
            SignalIdentifierControl1_Set                                    = 0x1254,
            SignalIdentifierStatus_Set                                      = 0x1258,
            ConfigureAntennaPatternExtended_Set                             = 0x125C,
            SignalIdentifierControl2_Set                                    = 0x1264,
            ChannelFilterSwitchTime_Set                                     = 0x1268,
            IrCalibrationControl_Set                                        = 0x1270,
            IrCalCoefficientValues_Set                                      = 0x1274,
            IrCalCoefficientWrPerAntenna0_Set                               = 0x1278,
            IrCalCoefficientWrPerAntenna1_Set                               = 0x127C,
            AdControl1_Set                                                  = 0x1280,
            AdControl2_Set                                                  = 0x1284,
            AdQual0_Set                                                     = 0x1288,
            AdQual1_Set                                                     = 0x128C,
            AdQual2_Set                                                     = 0x1290,
            AdQual3_Set                                                     = 0x1294,
            AdQual4_Set                                                     = 0x1298,
            AdQual5_Set                                                     = 0x129C,
            AdQual6_Set                                                     = 0x12A0,
            AdQual7_Set                                                     = 0x12A4,
            AdQual8_Set                                                     = 0x12A8,
            AdQual9_Set                                                     = 0x12AC,
            AdQual10_Set                                                    = 0x12B0,
            AdFsm0_Set                                                      = 0x12B4,
            AdFsm1_Set                                                      = 0x12B8,
            AdFsm2_Set                                                      = 0x12BC,
            AdFsm3_Set                                                      = 0x12C0,
            AdFsm4_Set                                                      = 0x12C4,
            AdFsm5_Set                                                      = 0x12C8,
            AdFsm6_Set                                                      = 0x12CC,
            AdFsm7_Set                                                      = 0x12D0,
            AdFsm8_Set                                                      = 0x12D4,
            AdFsm9_Set                                                      = 0x12D8,
            AdFsm10_Set                                                     = 0x12DC,
            AdFsm11_Set                                                     = 0x12E0,
            AdFsm12_Set                                                     = 0x12E4,
            AdFsm13_Set                                                     = 0x12E8,
            AdFsm14_Set                                                     = 0x12EC,
            AdFsm15_Set                                                     = 0x12F0,
            AdFsm16_Set                                                     = 0x12F4,
            AdFsm17_Set                                                     = 0x12F8,
            AdFsm18_Set                                                     = 0x12FC,
            AdFsm19_Set                                                     = 0x1300,
            AdFsm20_Set                                                     = 0x1304,
            AdFsm21_Set                                                     = 0x1308,
            AdFsm22_Set                                                     = 0x130C,
            AdFsm23_Set                                                     = 0x1310,
            AdFsm24_Set                                                     = 0x1314,
            AdFsm25_Set                                                     = 0x1318,
            AdFsm26_Set                                                     = 0x131C,
            AdFsm27_Set                                                     = 0x1320,
            AdFsm28_Set                                                     = 0x1324,
            AdFsm29_Set                                                     = 0x1328,
            AdFsm30_Set                                                     = 0x132C,
            AdPc1_Set                                                       = 0x1330,
            AdPc2_Set                                                       = 0x1334,
            AdPc3_Set                                                       = 0x1338,
            AdPc4_Set                                                       = 0x133C,
            AdPc5_Set                                                       = 0x1340,
            AdPc6_Set                                                       = 0x1344,
            AdPc7_Set                                                       = 0x1348,
            AdPc8_Set                                                       = 0x134C,
            AdPc9_Set                                                       = 0x1350,
            AdPc10_Set                                                      = 0x1354,
            HadmControl0_Set                                                = 0x13B0,
            HadmControl1_Set                                                = 0x13B4,
            HadmStatus0_Set                                                 = 0x13B8,
            HadmStatus1_Set                                                 = 0x13BC,
            HadmStatus2_Set                                                 = 0x13C0,
            HadmStatus3_Set                                                 = 0x13C4,
            HadmStatus4_Set                                                 = 0x13C8,
            HadmStatus5_Set                                                 = 0x13CC,
            HadmStatus6_Set                                                 = 0x13D0,
            Src2NcoControl_Set                                              = 0x13E0,
            Spare_Set                                                       = 0x1400,

            // Clear registers
            IpVersion_Clr                                                   = 0x2000,
            Enable_Clr                                                      = 0x2004,
            InterruptFlags_Clr                                              = 0x2008,
            InterruptEnable_Clr                                             = 0x200C,
            SequencerInterruptFlags_Clr                                     = 0x2010,
            SequencerInterruptEnable_Clr                                    = 0x2014,
            Status_Clr                                                      = 0x2018,
            Status2_Clr                                                     = 0x201C,
            Status3_Clr                                                     = 0x2020,
            Status4_Clr                                                     = 0x2024,
            Status5_Clr                                                     = 0x2028,
            Status6_Clr                                                     = 0x202C,
            Status7_Clr                                                     = 0x2030,
            TimingDetectionStatus_Clr                                       = 0x2034,
            DemodulatorFSMStatus_Clr                                        = 0x2038,
            FrequencyOffsetEstimate_Clr                                     = 0x203C,
            AutomaticFrequencyControlAdjustmentRx_Clr                       = 0x2040,
            AutomaticFrequencyControldAjustmentTx_Clr                       = 0x2044,
            AnalogMixerControl_Clr                                          = 0x2048,
            Control0_Clr                                                    = 0x204C,
            Control1_Clr                                                    = 0x2050,
            Control2_Clr                                                    = 0x2054,
            Control3_Clr                                                    = 0x2058,
            Control4_Clr                                                    = 0x205C,
            Control5_Clr                                                    = 0x2060,
            Control6_Clr                                                    = 0x2064,
            TxBaudrate_Clr                                                  = 0x2068,
            RxBaudrate_Clr                                                  = 0x206C,
            ChannelFilter_Clr                                               = 0x2070,
            Preamble_Clr                                                    = 0x2074,
            SyncWord0_Clr                                                   = 0x2078,
            SyncWord1_Clr                                                   = 0x207C,
            Timing_Clr                                                      = 0x2080,
            DirectSequenceSpreadSpectrumSymbol0_Clr                         = 0x2084,
            ModulationIndex_Clr                                             = 0x2088,
            AutomaticFrequencyControl_Clr                                   = 0x208C,
            AutomaticFrequencyControlAdjustmentLimit_Clr                    = 0x2090,
            ShapingCoefficients0_Clr                                        = 0x2094,
            ShapingCoefficients1_Clr                                        = 0x2098,
            ShapingCoefficients2_Clr                                        = 0x209C,
            ShapingCoefficients3_Clr                                        = 0x20A0,
            ShapingCoefficients4_Clr                                        = 0x20A4,
            ShapingCoefficients5_Clr                                        = 0x20A8,
            ShapingCoefficients6_Clr                                        = 0x20AC,
            ShapingCoefficients7_Clr                                        = 0x20B0,
            ShapingCoefficients8_Clr                                        = 0x20B4,
            ShapingCoefficients9_Clr                                        = 0x20B8,
            ShapingCoefficients10_Clr                                       = 0x20BC,
            ShapingCoefficients11_Clr                                       = 0x20C0,
            ShapingCoefficients12_Clr                                       = 0x20C4,
            ShapingCoefficients13_Clr                                       = 0x20C8,
            ShapingCoefficients14_Clr                                       = 0x20CC,
            ShapingCoefficients15_Clr                                       = 0x20D0,
            RampingControl_Clr                                              = 0x20D8,
            RampingLevels_Clr                                               = 0x20DC,
            AnalogRampingControl_Clr                                        = 0x20E0,
            DirectCurrentOffsetCompensationFilterSettings_Clr               = 0x2110,
            DirectCurrentOffsetCompensationFilterInitialization_Clr         = 0x2114,
            DirectCurrentOffsetEstimatedValue_Clr                           = 0x2118,
            SampleRateConverterRatioValuesAndChannelFilter_Clr              = 0x211C,
            InternalAutomaticFrequencyControl_Clr                           = 0x2120,
            DetectionOfSignalArrivalThreshold0_Clr                          = 0x2124,
            DetectionOfSignalArrivalThreshold1_Clr                          = 0x2128,
            DetectionOfSignalArrivalThreshold2_Clr                          = 0x212C,
            DetectionOfSignalArrivalThreshold3_Clr                          = 0x2130,
            DetectionOfSignalArrivalThreshold4_Clr                          = 0x2134,
            DetectionOfSignalArrivalMode_Clr                                = 0x2138,
            DigitalMixerControl_Clr                                         = 0x213C,
            ViterbiDemodulator_Clr                                          = 0x2140,
            ViterbiDemodulatorCorrelationConfiguration0_Clr                 = 0x2144,
            ViterbiDemodulatorCorrelationConfiguration1_Clr                 = 0x2148,
            ViterbiDemodulatorTrackingLoop_Clr                              = 0x214C,
            ViterbiBleTimingStampControl_Clr                                = 0x2150,
            BaudrateEstimate_Clr                                            = 0x2154,
            AutomaticClockGating_Clr                                        = 0x2158,
            AutomaticClockGatingClockStop_Clr                               = 0x215C,
            PhaseOffsetEstimate_Clr                                         = 0x2160,
            DirectModeControl_Clr                                           = 0x2164,
            BleLongRange_Clr                                                = 0x2168,
            BleLongRangeSet1_Clr                                            = 0x216C,
            BleLongRangeSet2_Clr                                            = 0x2170,
            BleLongRangeSet3_Clr                                            = 0x2174,
            BleLongRangeSet4_Clr                                            = 0x2178,
            BleLongRangeSet5_Clr                                            = 0x217C,
            BleLongRangeSet6_Clr                                            = 0x2180,
            BleLongRangeFrameControllerInterface_Clr                        = 0x2184,
            CoherentDemodulatorSignals0_Clr                                 = 0x2188,
            CoherentDemodulatorSignals1_Clr                                 = 0x218C,
            CoherentDemodulatorSignals2_Clr                                 = 0x2190,
            CoherentDemodulatorSignals3_Clr                                 = 0x2194,
            Command_Clr                                                     = 0x2198,
            SyncWordProperties_Clr                                          = 0x21A4,
            DigitalGainControl_Clr                                          = 0x21A8,
            PeripheralReflexSystemControl_Clr                               = 0x21AC,
            PowerAmplifierDebug_Clr                                         = 0x21B0,
            RealTimeCostFunctionEngineControl_Clr                           = 0x21B4,
            EarlyTimeStampControl_Clr                                       = 0x21B8,
            EarlyTimeStampTiming_Clr                                        = 0x21BC,
            AntennaSwitchControl_Clr                                        = 0x21C0,
            AntennaSwitchControl1_Clr                                       = 0x21C4,
            AntennaSwitchStart_Clr                                          = 0x21C8,
            AntennaSwitchEnd_Clr                                            = 0x21CC,
            TrecsPreamblePattern_Clr                                        = 0x21D0,
            TrecsPreambleDetectionControl_Clr                               = 0x21D4,
            TrecsConfiguration_Clr                                          = 0x21D8,
            ConfigureAntennaPattern_Clr                                     = 0x21DC,
            ConcurrentMode_Clr                                              = 0x21E0,
            ChannelFilterCoeSet0Group0_Clr                                  = 0x21E4,
            ChannelFilterCoeSet0Group1_Clr                                  = 0x21E8,
            ChannelFilterCoeSet0Group2_Clr                                  = 0x21EC,
            ChannelFilterCoeSet0Group3_Clr                                  = 0x21F0,
            ChannelFilterCoeSet0Group4_Clr                                  = 0x21F4,
            ChannelFilterCoeSet0Group5_Clr                                  = 0x21F8,
            ChannelFilterCoeSet0Group6_Clr                                  = 0x21FC,
            ChannelFilterCoeSet1Group0_Clr                                  = 0x2200,
            ChannelFilterCoeSet1Group1_Clr                                  = 0x2204,
            ChannelFilterCoeSet1Group2_Clr                                  = 0x2208,
            ChannelFilterCoeSet1Group3_Clr                                  = 0x220C,
            ChannelFilterCoeSet1Group4_Clr                                  = 0x2210,
            ChannelFilterCoeSet1Group5_Clr                                  = 0x2214,
            ChannelFilterCoeSet1Group6_Clr                                  = 0x2218,
            ChannelFilterControl_Clr                                        = 0x221C,
            ChannelFilterLatencyControl_Clr                                 = 0x2220,
            FrameSchTimeoutLength_Clr                                       = 0x2224,
            PreambleFilterCoefficients_Clr                                  = 0x2228,
            CollisionRestartControl_Clr                                     = 0x222C,
            PreambleSenseMode_Clr                                           = 0x2230,
            PreambleSenseModeExtended_Clr                                   = 0x2234,
            SignalQualityIndicator_Clr                                      = 0x2238,
            AntennaDiversityModeControl_Clr                                 = 0x223C,
            PhaseDemodulatorFwMode_Clr                                      = 0x2240,
            PhaseDemodulatorAntennaDiversity_Clr                            = 0x2244,
            PhaseDemodulatorAntennaDiversityDecision_Clr                    = 0x2248,
            PhaseDemodulatorControl_Clr                                     = 0x224C,
            SignalIdentifierControl0_Clr                                    = 0x2250,
            SignalIdentifierControl1_Clr                                    = 0x2254,
            SignalIdentifierStatus_Clr                                      = 0x2258,
            ConfigureAntennaPatternExtended_Clr                             = 0x225C,
            SignalIdentifierControl2_Clr                                    = 0x2264,
            ChannelFilterSwitchTime_Clr                                     = 0x2268,
            IrCalibrationControl_Clr                                        = 0x2270,
            IrCalCoefficientValues_Clr                                      = 0x2274,
            IrCalCoefficientWrPerAntenna0_Clr                               = 0x2278,
            IrCalCoefficientWrPerAntenna1_Clr                               = 0x227C,
            AdControl1_Clr                                                  = 0x2280,
            AdControl2_Clr                                                  = 0x2284,
            AdQual0_Clr                                                     = 0x2288,
            AdQual1_Clr                                                     = 0x228C,
            AdQual2_Clr                                                     = 0x2290,
            AdQual3_Clr                                                     = 0x2294,
            AdQual4_Clr                                                     = 0x2298,
            AdQual5_Clr                                                     = 0x229C,
            AdQual6_Clr                                                     = 0x22A0,
            AdQual7_Clr                                                     = 0x22A4,
            AdQual8_Clr                                                     = 0x22A8,
            AdQual9_Clr                                                     = 0x22AC,
            AdQual10_Clr                                                    = 0x22B0,
            AdFsm0_Clr                                                      = 0x22B4,
            AdFsm1_Clr                                                      = 0x22B8,
            AdFsm2_Clr                                                      = 0x22BC,
            AdFsm3_Clr                                                      = 0x22C0,
            AdFsm4_Clr                                                      = 0x22C4,
            AdFsm5_Clr                                                      = 0x22C8,
            AdFsm6_Clr                                                      = 0x22CC,
            AdFsm7_Clr                                                      = 0x22D0,
            AdFsm8_Clr                                                      = 0x22D4,
            AdFsm9_Clr                                                      = 0x22D8,
            AdFsm10_Clr                                                     = 0x22DC,
            AdFsm11_Clr                                                     = 0x22E0,
            AdFsm12_Clr                                                     = 0x22E4,
            AdFsm13_Clr                                                     = 0x22E8,
            AdFsm14_Clr                                                     = 0x22EC,
            AdFsm15_Clr                                                     = 0x22F0,
            AdFsm16_Clr                                                     = 0x22F4,
            AdFsm17_Clr                                                     = 0x22F8,
            AdFsm18_Clr                                                     = 0x22FC,
            AdFsm19_Clr                                                     = 0x2300,
            AdFsm20_Clr                                                     = 0x2304,
            AdFsm21_Clr                                                     = 0x2308,
            AdFsm22_Clr                                                     = 0x230C,
            AdFsm23_Clr                                                     = 0x2310,
            AdFsm24_Clr                                                     = 0x2314,
            AdFsm25_Clr                                                     = 0x2318,
            AdFsm26_Clr                                                     = 0x231C,
            AdFsm27_Clr                                                     = 0x2320,
            AdFsm28_Clr                                                     = 0x2324,
            AdFsm29_Clr                                                     = 0x2328,
            AdFsm30_Clr                                                     = 0x232C,
            AdPc1_Clr                                                       = 0x2330,
            AdPc2_Clr                                                       = 0x2334,
            AdPc3_Clr                                                       = 0x2338,
            AdPc4_Clr                                                       = 0x233C,
            AdPc5_Clr                                                       = 0x2340,
            AdPc6_Clr                                                       = 0x2344,
            AdPc7_Clr                                                       = 0x2348,
            AdPc8_Clr                                                       = 0x234C,
            AdPc9_Clr                                                       = 0x2350,
            AdPc10_Clr                                                      = 0x2354,
            HadmControl0_Clr                                                = 0x23B0,
            HadmControl1_Clr                                                = 0x23B4,
            HadmStatus0_Clr                                                 = 0x23B8,
            HadmStatus1_Clr                                                 = 0x23BC,
            HadmStatus2_Clr                                                 = 0x23C0,
            HadmStatus3_Clr                                                 = 0x23C4,
            HadmStatus4_Clr                                                 = 0x23C8,
            HadmStatus5_Clr                                                 = 0x23CC,
            HadmStatus6_Clr                                                 = 0x23D0,
            Src2NcoControl_Clr                                              = 0x23E0,
            Spare_Clr                                                       = 0x2400,

            // Toggle registers
            IpVersion_Tgl                                                   = 0x3000,
            Enable_Tgl                                                      = 0x3004,
            InterruptFlags_Tgl                                              = 0x3008,
            InterruptEnable_Tgl                                             = 0x300C,
            SequencerInterruptFlags_Tgl                                     = 0x3010,
            SequencerInterruptEnable_Tgl                                    = 0x3014,
            Status_Tgl                                                      = 0x3018,
            Status2_Tgl                                                     = 0x301C,
            Status3_Tgl                                                     = 0x3020,
            Status4_Tgl                                                     = 0x3024,
            Status5_Tgl                                                     = 0x3028,
            Status6_Tgl                                                     = 0x302C,
            Status7_Tgl                                                     = 0x3030,
            TimingDetectionStatus_Tgl                                       = 0x3034,
            DemodulatorFSMStatus_Tgl                                        = 0x3038,
            FrequencyOffsetEstimate_Tgl                                     = 0x303C,
            AutomaticFrequencyControlAdjustmentRx_Tgl                       = 0x3040,
            AutomaticFrequencyControldAjustmentTx_Tgl                       = 0x3044,
            AnalogMixerControl_Tgl                                          = 0x3048,
            Control0_Tgl                                                    = 0x304C,
            Control1_Tgl                                                    = 0x3050,
            Control2_Tgl                                                    = 0x3054,
            Control3_Tgl                                                    = 0x3058,
            Control4_Tgl                                                    = 0x305C,
            Control5_Tgl                                                    = 0x3060,
            Control6_Tgl                                                    = 0x3064,
            TxBaudrate_Tgl                                                  = 0x3068,
            RxBaudrate_Tgl                                                  = 0x306C,
            ChannelFilter_Tgl                                               = 0x3070,
            Preamble_Tgl                                                    = 0x3074,
            SyncWord0_Tgl                                                   = 0x3078,
            SyncWord1_Tgl                                                   = 0x307C,
            Timing_Tgl                                                      = 0x3080,
            DirectSequenceSpreadSpectrumSymbol0_Tgl                         = 0x3084,
            ModulationIndex_Tgl                                             = 0x3088,
            AutomaticFrequencyControl_Tgl                                   = 0x308C,
            AutomaticFrequencyControlAdjustmentLimit_Tgl                    = 0x3090,
            ShapingCoefficients0_Tgl                                        = 0x3094,
            ShapingCoefficients1_Tgl                                        = 0x3098,
            ShapingCoefficients2_Tgl                                        = 0x309C,
            ShapingCoefficients3_Tgl                                        = 0x30A0,
            ShapingCoefficients4_Tgl                                        = 0x30A4,
            ShapingCoefficients5_Tgl                                        = 0x30A8,
            ShapingCoefficients6_Tgl                                        = 0x30AC,
            ShapingCoefficients7_Tgl                                        = 0x30B0,
            ShapingCoefficients8_Tgl                                        = 0x30B4,
            ShapingCoefficients9_Tgl                                        = 0x30B8,
            ShapingCoefficients10_Tgl                                       = 0x30BC,
            ShapingCoefficients11_Tgl                                       = 0x30C0,
            ShapingCoefficients12_Tgl                                       = 0x30C4,
            ShapingCoefficients13_Tgl                                       = 0x30C8,
            ShapingCoefficients14_Tgl                                       = 0x30CC,
            ShapingCoefficients15_Tgl                                       = 0x30D0,
            RampingControl_Tgl                                              = 0x30D8,
            RampingLevels_Tgl                                               = 0x30DC,
            AnalogRampingControl_Tgl                                        = 0x30E0,
            DirectCurrentOffsetCompensationFilterSettings_Tgl               = 0x3110,
            DirectCurrentOffsetCompensationFilterInitialization_Tgl         = 0x3114,
            DirectCurrentOffsetEstimatedValue_Tgl                           = 0x3118,
            SampleRateConverterRatioValuesAndChannelFilter_Tgl              = 0x311C,
            InternalAutomaticFrequencyControl_Tgl                           = 0x3120,
            DetectionOfSignalArrivalThreshold0_Tgl                          = 0x3124,
            DetectionOfSignalArrivalThreshold1_Tgl                          = 0x3128,
            DetectionOfSignalArrivalThreshold2_Tgl                          = 0x312C,
            DetectionOfSignalArrivalThreshold3_Tgl                          = 0x3130,
            DetectionOfSignalArrivalThreshold4_Tgl                          = 0x3134,
            DetectionOfSignalArrivalMode_Tgl                                = 0x3138,
            DigitalMixerControl_Tgl                                         = 0x313C,
            ViterbiDemodulator_Tgl                                          = 0x3140,
            ViterbiDemodulatorCorrelationConfiguration0_Tgl                 = 0x3144,
            ViterbiDemodulatorCorrelationConfiguration1_Tgl                 = 0x3148,
            ViterbiDemodulatorTrackingLoop_Tgl                              = 0x314C,
            ViterbiBleTimingStampControl_Tgl                                = 0x3150,
            BaudrateEstimate_Tgl                                            = 0x3154,
            AutomaticClockGating_Tgl                                        = 0x3158,
            AutomaticClockGatingClockStop_Tgl                               = 0x315C,
            PhaseOffsetEstimate_Tgl                                         = 0x3160,
            DirectModeControl_Tgl                                           = 0x3164,
            BleLongRange_Tgl                                                = 0x3168,
            BleLongRangeSet1_Tgl                                            = 0x316C,
            BleLongRangeSet2_Tgl                                            = 0x3170,
            BleLongRangeSet3_Tgl                                            = 0x3174,
            BleLongRangeSet4_Tgl                                            = 0x3178,
            BleLongRangeSet5_Tgl                                            = 0x317C,
            BleLongRangeSet6_Tgl                                            = 0x3180,
            BleLongRangeFrameControllerInterface_Tgl                        = 0x3184,
            CoherentDemodulatorSignals0_Tgl                                 = 0x3188,
            CoherentDemodulatorSignals1_Tgl                                 = 0x318C,
            CoherentDemodulatorSignals2_Tgl                                 = 0x3190,
            CoherentDemodulatorSignals3_Tgl                                 = 0x3194,
            Command_Tgl                                                     = 0x3198,
            SyncWordProperties_Tgl                                          = 0x31A4,
            DigitalGainControl_Tgl                                          = 0x31A8,
            PeripheralReflexSystemControl_Tgl                               = 0x31AC,
            PowerAmplifierDebug_Tgl                                         = 0x31B0,
            RealTimeCostFunctionEngineControl_Tgl                           = 0x31B4,
            EarlyTimeStampControl_Tgl                                       = 0x31B8,
            EarlyTimeStampTiming_Tgl                                        = 0x31BC,
            AntennaSwitchControl_Tgl                                        = 0x31C0,
            AntennaSwitchControl1_Tgl                                       = 0x31C4,
            AntennaSwitchStart_Tgl                                          = 0x31C8,
            AntennaSwitchEnd_Tgl                                            = 0x31CC,
            TrecsPreamblePattern_Tgl                                        = 0x31D0,
            TrecsPreambleDetectionControl_Tgl                               = 0x31D4,
            TrecsConfiguration_Tgl                                          = 0x31D8,
            ConfigureAntennaPattern_Tgl                                     = 0x31DC,
            ConcurrentMode_Tgl                                              = 0x31E0,
            ChannelFilterCoeSet0Group0_Tgl                                  = 0x31E4,
            ChannelFilterCoeSet0Group1_Tgl                                  = 0x31E8,
            ChannelFilterCoeSet0Group2_Tgl                                  = 0x31EC,
            ChannelFilterCoeSet0Group3_Tgl                                  = 0x31F0,
            ChannelFilterCoeSet0Group4_Tgl                                  = 0x31F4,
            ChannelFilterCoeSet0Group5_Tgl                                  = 0x31F8,
            ChannelFilterCoeSet0Group6_Tgl                                  = 0x31FC,
            ChannelFilterCoeSet1Group0_Tgl                                  = 0x3200,
            ChannelFilterCoeSet1Group1_Tgl                                  = 0x3204,
            ChannelFilterCoeSet1Group2_Tgl                                  = 0x3208,
            ChannelFilterCoeSet1Group3_Tgl                                  = 0x320C,
            ChannelFilterCoeSet1Group4_Tgl                                  = 0x3210,
            ChannelFilterCoeSet1Group5_Tgl                                  = 0x3214,
            ChannelFilterCoeSet1Group6_Tgl                                  = 0x3218,
            ChannelFilterControl_Tgl                                        = 0x321C,
            ChannelFilterLatencyControl_Tgl                                 = 0x3220,
            FrameSchTimeoutLength_Tgl                                       = 0x3224,
            PreambleFilterCoefficients_Tgl                                  = 0x3228,
            CollisionRestartControl_Tgl                                     = 0x322C,
            PreambleSenseMode_Tgl                                           = 0x3230,
            PreambleSenseModeExtended_Tgl                                   = 0x3234,
            SignalQualityIndicator_Tgl                                      = 0x3238,
            AntennaDiversityModeControl_Tgl                                 = 0x323C,
            PhaseDemodulatorFwMode_Tgl                                      = 0x3240,
            PhaseDemodulatorAntennaDiversity_Tgl                            = 0x3244,
            PhaseDemodulatorAntennaDiversityDecision_Tgl                    = 0x3248,
            PhaseDemodulatorControl_Tgl                                     = 0x324C,
            SignalIdentifierControl0_Tgl                                    = 0x3250,
            SignalIdentifierControl1_Tgl                                    = 0x3254,
            SignalIdentifierStatus_Tgl                                      = 0x3258,
            ConfigureAntennaPatternExtended_Tgl                             = 0x325C,
            SignalIdentifierControl2_Tgl                                    = 0x3264,
            ChannelFilterSwitchTime_Tgl                                     = 0x3268,
            IrCalibrationControl_Tgl                                        = 0x3270,
            IrCalCoefficientValues_Tgl                                      = 0x3274,
            IrCalCoefficientWrPerAntenna0_Tgl                               = 0x3278,
            IrCalCoefficientWrPerAntenna1_Tgl                               = 0x327C,
            AdControl1_Tgl                                                  = 0x3280,
            AdControl2_Tgl                                                  = 0x3284,
            AdQual0_Tgl                                                     = 0x3288,
            AdQual1_Tgl                                                     = 0x328C,
            AdQual2_Tgl                                                     = 0x3290,
            AdQual3_Tgl                                                     = 0x3294,
            AdQual4_Tgl                                                     = 0x3298,
            AdQual5_Tgl                                                     = 0x329C,
            AdQual6_Tgl                                                     = 0x32A0,
            AdQual7_Tgl                                                     = 0x32A4,
            AdQual8_Tgl                                                     = 0x32A8,
            AdQual9_Tgl                                                     = 0x32AC,
            AdQual10_Tgl                                                    = 0x32B0,
            AdFsm0_Tgl                                                      = 0x32B4,
            AdFsm1_Tgl                                                      = 0x32B8,
            AdFsm2_Tgl                                                      = 0x32BC,
            AdFsm3_Tgl                                                      = 0x32C0,
            AdFsm4_Tgl                                                      = 0x32C4,
            AdFsm5_Tgl                                                      = 0x32C8,
            AdFsm6_Tgl                                                      = 0x32CC,
            AdFsm7_Tgl                                                      = 0x32D0,
            AdFsm8_Tgl                                                      = 0x32D4,
            AdFsm9_Tgl                                                      = 0x32D8,
            AdFsm10_Tgl                                                     = 0x32DC,
            AdFsm11_Tgl                                                     = 0x32E0,
            AdFsm12_Tgl                                                     = 0x32E4,
            AdFsm13_Tgl                                                     = 0x32E8,
            AdFsm14_Tgl                                                     = 0x32EC,
            AdFsm15_Tgl                                                     = 0x32F0,
            AdFsm16_Tgl                                                     = 0x32F4,
            AdFsm17_Tgl                                                     = 0x32F8,
            AdFsm18_Tgl                                                     = 0x32FC,
            AdFsm19_Tgl                                                     = 0x3300,
            AdFsm20_Tgl                                                     = 0x3304,
            AdFsm21_Tgl                                                     = 0x3308,
            AdFsm22_Tgl                                                     = 0x330C,
            AdFsm23_Tgl                                                     = 0x3310,
            AdFsm24_Tgl                                                     = 0x3314,
            AdFsm25_Tgl                                                     = 0x3318,
            AdFsm26_Tgl                                                     = 0x331C,
            AdFsm27_Tgl                                                     = 0x3320,
            AdFsm28_Tgl                                                     = 0x3324,
            AdFsm29_Tgl                                                     = 0x3328,
            AdFsm30_Tgl                                                     = 0x332C,
            AdPc1_Tgl                                                       = 0x3330,
            AdPc2_Tgl                                                       = 0x3334,
            AdPc3_Tgl                                                       = 0x3338,
            AdPc4_Tgl                                                       = 0x333C,
            AdPc5_Tgl                                                       = 0x3340,
            AdPc6_Tgl                                                       = 0x3344,
            AdPc7_Tgl                                                       = 0x3348,
            AdPc8_Tgl                                                       = 0x334C,
            AdPc9_Tgl                                                       = 0x3350,
            AdPc10_Tgl                                                      = 0x3354,
            HadmControl0_Tgl                                                = 0x33B0,
            HadmControl1_Tgl                                                = 0x33B4,
            HadmStatus0_Tgl                                                 = 0x33B8,
            HadmStatus1_Tgl                                                 = 0x33BC,
            HadmStatus2_Tgl                                                 = 0x33C0,
            HadmStatus3_Tgl                                                 = 0x33C4,
            HadmStatus4_Tgl                                                 = 0x33C8,
            HadmStatus5_Tgl                                                 = 0x33CC,
            HadmStatus6_Tgl                                                 = 0x33D0,
            Src2NcoControl_Tgl                                              = 0x33E0,
            Spare_Tgl                                                       = 0x3400,
        }

        [RegistersDescription("agc", "agc_ns")]
        private enum AutomaticGainControlRegisters : long
        {
            IpVersion                                                       = 0x0000,
            Enable                                                          = 0x0004,
            Status0                                                         = 0x0008,
            Status1                                                         = 0x000C,
            Status2                                                         = 0x0010,
            ReceivedSignalStrengthIndicator                                 = 0x0018,
            FrameReceivedSignalStrengthIndicator                            = 0x001C,
            Control0                                                        = 0x0020,
            Control1                                                        = 0x0024,
            Control2                                                        = 0x0028,
            Control3                                                        = 0x002C,
            Control4                                                        = 0x0030,
            Control5                                                        = 0x0034,
            Control6                                                        = 0x0038,
            Control7                                                        = 0x003C,
            ReceivedSignalStrengthIndicatorStepThreshold                    = 0x0040,
            ManualGain                                                      = 0x0044,
            InterruptFlags                                                  = 0x0048,
            InterruptEnable                                                 = 0x004C,
            Command                                                         = 0x0050,
            RxGainRange                                                     = 0x0054,
            AutomaticGainControlPeriod0                                     = 0x0058,
            AutomaticGainControlPeriod1                                     = 0x005C,
            HiCounterRegion0                                                = 0x0060,
            HiCounterRegion1                                                = 0x0064,
            HiCounterRegion2                                                = 0x0068,
            GainStepsLimits0                                                = 0x006C,
            GainStepsLimits1                                                = 0x0070,
            PnRfAttenuationCodeGroup0                                       = 0x0074,
            PnRfAttenuationCodeGroup1                                       = 0x0078,
            PnRfAttenuationCodeGroup2                                       = 0x007C,
            PnRfAttenuationCodeGroup3                                       = 0x0080,
            PnRfAttenuationCodeGroup4                                       = 0x0084,
            PnRfAttenuationCodeGroup5                                       = 0x0088,
            PnRfAttenuationCodeGroup6                                       = 0x008C,
            PnRfAttenuationCodeGroup7                                       = 0x0090,
            PnRfAttenuationCodeGroupAlternate                               = 0x00A4,
            LnaMixSliceCodeGroup0                                           = 0x00A8,
            LnaMixSliceCodeGroup1                                           = 0x00AC,
            ProgrammableGainAmplifierGainCodeGroup0                         = 0x00B0,
            ProgrammableGainAmplifierGainCodeGroup1                         = 0x00B4,
            ListenBeforeTalkConfiguration                                   = 0x00B8,
            MirrorInterruptFlags                                            = 0x00BC,
            SequencerInterruptFlags                                         = 0x00C0,
            SequencerInterruptEnable                                        = 0x00C4,
            ReceivedSignalStrengthIndicatorAbsoluteThreshold                = 0x00C8,
            LnaBoostControl                                                 = 0x00CC,
            AntennaDiversity                                                = 0x00D0,
            DualRfpkdThreshold0                                             = 0x00D4,
            DualRfpkdThreshold1                                             = 0x00D8,
            Spare                                                           = 0x00DC,
            PnRfFilterCodeGroup0                                            = 0x00E0,
            PnRfFilterCodeGroup1                                            = 0x00E4,
            PnRfFilterCodeGroup2                                            = 0x00E8,
            PnRfFilterCodeGroup3                                            = 0x00EC,
            PnRfFilterCodeGroup4                                            = 0x00F0,
            PnRfFilterCodeGroup5                                            = 0x00F4,
            PnRfFilterCodeGroup6                                            = 0x00F8,
            PnRfFilterCodeGroup7                                            = 0x00FC,
            FeNotchAttenuationCodeGroup0                                    = 0x0100,
            FeNotchAttenuationCodeGroup1                                    = 0x0104,
            FeNotchAttenuationCodeGroup2                                    = 0x0108,
            FeNotchAttenuationCodeGroup3                                    = 0x010C,
            FeNotchAttenuationCodeGroup4                                    = 0x0110,
            FeNotchAttenuationCodeGroup5                                    = 0x0114,
            FeNotchAttenuationCodeGroup6                                    = 0x0118,
            FeNotchAttenuationCodeGroup7                                    = 0x011C,
            FeNotchAttenuationCodeGroup8                                    = 0x0120,
            FeNotchAttenuationCodeGroup9                                    = 0x0124,
            FeNotchAttenuationCodeGroup10                                   = 0x0128,
            FeNotchAttenuationCodeGroup11                                   = 0x012C,
            FeNotchFilterCodeGroup0                                         = 0x0130,
            FeNotchFilterCodeGroup1                                         = 0x0134,
            FeNotchFilterCodeGroup2                                         = 0x0138,
            FeNotchFilterCodeGroup3                                         = 0x013C,
            FeNotchFilterCodeGroup4                                         = 0x0140,
            FeNotchFilterCodeGroup5                                         = 0x0144,
            FeNotchFilterCodeGroup6                                         = 0x0148,
            FeNotchFilterCodeGroup7                                         = 0x014C,
            FeNotchFilterCodeGroup8                                         = 0x0150,
            FeNotchFilterCodeGroup9                                         = 0x0154,
            FeNotchFilterCodeGroup10                                        = 0x0158,
            FeNotchFilterCodeGroup11                                        = 0x015C,
            ClearChannelAssessmentDebug                                     = 0x0160,
            // Set registers
            IpVersion_Set                                                   = 0x1000,
            Enable_Set                                                      = 0x1004,
            Status0_Set                                                     = 0x1008,
            Status1_Set                                                     = 0x100C,
            Status2_Set                                                     = 0x1010,
            ReceivedSignalStrengthIndicator_Set                             = 0x1018,
            FrameReceivedSignalStrengthIndicator_Set                        = 0x101C,
            Control0_Set                                                    = 0x1020,
            Control1_Set                                                    = 0x1024,
            Control2_Set                                                    = 0x1028,
            Control3_Set                                                    = 0x102C,
            Control4_Set                                                    = 0x1030,
            Control5_Set                                                    = 0x1034,
            Control6_Set                                                    = 0x1038,
            Control7_Set                                                    = 0x103C,
            ReceivedSignalStrengthIndicatorStepThreshold_Set                = 0x1040,
            ManualGain_Set                                                  = 0x1044,
            InterruptFlags_Set                                              = 0x1048,
            InterruptEnable_Set                                             = 0x104C,
            Command_Set                                                     = 0x1050,
            RxGainRange_Set                                                 = 0x1054,
            AutomaticGainControlPeriod0_Set                                 = 0x1058,
            AutomaticGainControlPeriod1_Set                                 = 0x105C,
            HiCounterRegion0_Set                                            = 0x1060,
            HiCounterRegion1_Set                                            = 0x1064,
            HiCounterRegion2_Set                                            = 0x1068,
            GainStepsLimits0_Set                                            = 0x106C,
            GainStepsLimits1_Set                                            = 0x1070,
            PnRfAttenuationCodeGroup0_Set                                   = 0x1074,
            PnRfAttenuationCodeGroup1_Set                                   = 0x1078,
            PnRfAttenuationCodeGroup2_Set                                   = 0x107C,
            PnRfAttenuationCodeGroup3_Set                                   = 0x1080,
            PnRfAttenuationCodeGroup4_Set                                   = 0x1084,
            PnRfAttenuationCodeGroup5_Set                                   = 0x1088,
            PnRfAttenuationCodeGroup6_Set                                   = 0x108C,
            PnRfAttenuationCodeGroup7_Set                                   = 0x1090,
            PnRfAttenuationCodeGroupAlternate_Set                           = 0x10A4,
            LnaMixSliceCodeGroup0_Set                                       = 0x10A8,
            LnaMixSliceCodeGroup1_Set                                       = 0x10AC,
            ProgrammableGainAmplifierGainCodeGroup0_Set                     = 0x10B0,
            ProgrammableGainAmplifierGainCodeGroup1_Set                     = 0x10B4,
            ListenBeforeTalkConfiguration_Set                               = 0x10B8,
            MirrorInterruptFlags_Set                                        = 0x10BC,
            SequencerInterruptFlags_Set                                     = 0x10C0,
            SequencerInterruptEnable_Set                                    = 0x10C4,
            ReceivedSignalStrengthIndicatorAbsoluteThreshold_Set            = 0x10C8,
            LnaBoostControl_Set                                             = 0x10CC,
            AntennaDiversity_Set                                            = 0x10D0,
            DualRfpkdThreshold0_Set                                         = 0x10D4,
            DualRfpkdThreshold1_Set                                         = 0x10D8,
            Spare_Set                                                       = 0x10DC,
            PnRfFilterCodeGroup0_Set                                        = 0x10E0,
            PnRfFilterCodeGroup1_Set                                        = 0x10E4,
            PnRfFilterCodeGroup2_Set                                        = 0x10E8,
            PnRfFilterCodeGroup3_Set                                        = 0x10EC,
            PnRfFilterCodeGroup4_Set                                        = 0x10F0,
            PnRfFilterCodeGroup5_Set                                        = 0x10F4,
            PnRfFilterCodeGroup6_Set                                        = 0x10F8,
            PnRfFilterCodeGroup7_Set                                        = 0x10FC,
            FeNotchAttenuationCodeGroup0_Set                                = 0x1100,
            FeNotchAttenuationCodeGroup1_Set                                = 0x1104,
            FeNotchAttenuationCodeGroup2_Set                                = 0x1108,
            FeNotchAttenuationCodeGroup3_Set                                = 0x110C,
            FeNotchAttenuationCodeGroup4_Set                                = 0x1110,
            FeNotchAttenuationCodeGroup5_Set                                = 0x1114,
            FeNotchAttenuationCodeGroup6_Set                                = 0x1118,
            FeNotchAttenuationCodeGroup7_Set                                = 0x111C,
            FeNotchAttenuationCodeGroup8_Set                                = 0x1120,
            FeNotchAttenuationCodeGroup9_Set                                = 0x1124,
            FeNotchAttenuationCodeGroup10_Set                               = 0x1128,
            FeNotchAttenuationCodeGroup11_Set                               = 0x112C,
            FeNotchFilterCodeGroup0_Set                                     = 0x1130,
            FeNotchFilterCodeGroup1_Set                                     = 0x1134,
            FeNotchFilterCodeGroup2_Set                                     = 0x1138,
            FeNotchFilterCodeGroup3_Set                                     = 0x113C,
            FeNotchFilterCodeGroup4_Set                                     = 0x1140,
            FeNotchFilterCodeGroup5_Set                                     = 0x1144,
            FeNotchFilterCodeGroup6_Set                                     = 0x1148,
            FeNotchFilterCodeGroup7_Set                                     = 0x114C,
            FeNotchFilterCodeGroup8_Set                                     = 0x1150,
            FeNotchFilterCodeGroup9_Set                                     = 0x1154,
            FeNotchFilterCodeGroup10_Set                                    = 0x1158,
            FeNotchFilterCodeGroup11_Set                                    = 0x115C,
            ClearChannelAssessmentDebug_Set                                 = 0x1160,
            // Clear registers
            IpVersion_Clr                                                   = 0x2000,
            Enable_Clr                                                      = 0x2004,
            Status0_Clr                                                     = 0x2008,
            Status1_Clr                                                     = 0x200C,
            Status2_Clr                                                     = 0x2010,
            ReceivedSignalStrengthIndicator_Clr                             = 0x2018,
            FrameReceivedSignalStrengthIndicator_Clr                        = 0x201C,
            Control0_Clr                                                    = 0x2020,
            Control1_Clr                                                    = 0x2024,
            Control2_Clr                                                    = 0x2028,
            Control3_Clr                                                    = 0x202C,
            Control4_Clr                                                    = 0x2030,
            Control5_Clr                                                    = 0x2034,
            Control6_Clr                                                    = 0x2038,
            Control7_Clr                                                    = 0x203C,
            ReceivedSignalStrengthIndicatorStepThreshold_Clr                = 0x2040,
            ManualGain_Clr                                                  = 0x2044,
            InterruptFlags_Clr                                              = 0x2048,
            InterruptEnable_Clr                                             = 0x204C,
            Command_Clr                                                     = 0x2050,
            RxGainRange_Clr                                                 = 0x2054,
            AutomaticGainControlPeriod0_Clr                                 = 0x2058,
            AutomaticGainControlPeriod1_Clr                                 = 0x205C,
            HiCounterRegion0_Clr                                            = 0x2060,
            HiCounterRegion1_Clr                                            = 0x2064,
            HiCounterRegion2_Clr                                            = 0x2068,
            GainStepsLimits0_Clr                                            = 0x206C,
            GainStepsLimits1_Clr                                            = 0x2070,
            PnRfAttenuationCodeGroup0_Clr                                   = 0x2074,
            PnRfAttenuationCodeGroup1_Clr                                   = 0x2078,
            PnRfAttenuationCodeGroup2_Clr                                   = 0x207C,
            PnRfAttenuationCodeGroup3_Clr                                   = 0x2080,
            PnRfAttenuationCodeGroup4_Clr                                   = 0x2084,
            PnRfAttenuationCodeGroup5_Clr                                   = 0x2088,
            PnRfAttenuationCodeGroup6_Clr                                   = 0x208C,
            PnRfAttenuationCodeGroup7_Clr                                   = 0x2090,
            PnRfAttenuationCodeGroupAlternate_Clr                           = 0x20A4,
            LnaMixSliceCodeGroup0_Clr                                       = 0x20A8,
            LnaMixSliceCodeGroup1_Clr                                       = 0x20AC,
            ProgrammableGainAmplifierGainCodeGroup0_Clr                     = 0x20B0,
            ProgrammableGainAmplifierGainCodeGroup1_Clr                     = 0x20B4,
            ListenBeforeTalkConfiguration_Clr                               = 0x20B8,
            MirrorInterruptFlags_Clr                                        = 0x20BC,
            SequencerInterruptFlags_Clr                                     = 0x20C0,
            SequencerInterruptEnable_Clr                                    = 0x20C4,
            ReceivedSignalStrengthIndicatorAbsoluteThreshold_Clr            = 0x20C8,
            LnaBoostControl_Clr                                             = 0x20CC,
            AntennaDiversity_Clr                                            = 0x20D0,
            DualRfpkdThreshold0_Clr                                         = 0x20D4,
            DualRfpkdThreshold1_Clr                                         = 0x20D8,
            Spare_Clr                                                       = 0x20DC,
            PnRfFilterCodeGroup0_Clr                                        = 0x20E0,
            PnRfFilterCodeGroup1_Clr                                        = 0x20E4,
            PnRfFilterCodeGroup2_Clr                                        = 0x20E8,
            PnRfFilterCodeGroup3_Clr                                        = 0x20EC,
            PnRfFilterCodeGroup4_Clr                                        = 0x20F0,
            PnRfFilterCodeGroup5_Clr                                        = 0x20F4,
            PnRfFilterCodeGroup6_Clr                                        = 0x20F8,
            PnRfFilterCodeGroup7_Clr                                        = 0x20FC,
            FeNotchAttenuationCodeGroup0_Clr                                = 0x2100,
            FeNotchAttenuationCodeGroup1_Clr                                = 0x2104,
            FeNotchAttenuationCodeGroup2_Clr                                = 0x2108,
            FeNotchAttenuationCodeGroup3_Clr                                = 0x210C,
            FeNotchAttenuationCodeGroup4_Clr                                = 0x2110,
            FeNotchAttenuationCodeGroup5_Clr                                = 0x2114,
            FeNotchAttenuationCodeGroup6_Clr                                = 0x2118,
            FeNotchAttenuationCodeGroup7_Clr                                = 0x211C,
            FeNotchAttenuationCodeGroup8_Clr                                = 0x2120,
            FeNotchAttenuationCodeGroup9_Clr                                = 0x2124,
            FeNotchAttenuationCodeGroup10_Clr                               = 0x2128,
            FeNotchAttenuationCodeGroup11_Clr                               = 0x212C,
            FeNotchFilterCodeGroup0_Clr                                     = 0x2130,
            FeNotchFilterCodeGroup1_Clr                                     = 0x2134,
            FeNotchFilterCodeGroup2_Clr                                     = 0x2138,
            FeNotchFilterCodeGroup3_Clr                                     = 0x213C,
            FeNotchFilterCodeGroup4_Clr                                     = 0x2140,
            FeNotchFilterCodeGroup5_Clr                                     = 0x2144,
            FeNotchFilterCodeGroup6_Clr                                     = 0x2148,
            FeNotchFilterCodeGroup7_Clr                                     = 0x214C,
            FeNotchFilterCodeGroup8_Clr                                     = 0x2150,
            FeNotchFilterCodeGroup9_Clr                                     = 0x2154,
            FeNotchFilterCodeGroup10_Clr                                    = 0x2158,
            FeNotchFilterCodeGroup11_Clr                                    = 0x215C,
            ClearChannelAssessmentDebug_Clr                                 = 0x2160,
            // Toggle registers
            IpVersion_Tgl                                                   = 0x3000,
            Enable_Tgl                                                      = 0x3004,
            Status0_Tgl                                                     = 0x3008,
            Status1_Tgl                                                     = 0x300C,
            Status2_Tgl                                                     = 0x3010,
            ReceivedSignalStrengthIndicator_Tgl                             = 0x3018,
            FrameReceivedSignalStrengthIndicator_Tgl                        = 0x301C,
            Control0_Tgl                                                    = 0x3020,
            Control1_Tgl                                                    = 0x3024,
            Control2_Tgl                                                    = 0x3028,
            Control3_Tgl                                                    = 0x302C,
            Control4_Tgl                                                    = 0x3030,
            Control5_Tgl                                                    = 0x3034,
            Control6_Tgl                                                    = 0x3038,
            Control7_Tgl                                                    = 0x303C,
            ReceivedSignalStrengthIndicatorStepThreshold_Tgl                = 0x3040,
            ManualGain_Tgl                                                  = 0x3044,
            InterruptFlags_Tgl                                              = 0x3048,
            InterruptEnable_Tgl                                             = 0x304C,
            Command_Tgl                                                     = 0x3050,
            RxGainRange_Tgl                                                 = 0x3054,
            AutomaticGainControlPeriod0_Tgl                                 = 0x3058,
            AutomaticGainControlPeriod1_Tgl                                 = 0x305C,
            HiCounterRegion0_Tgl                                            = 0x3060,
            HiCounterRegion1_Tgl                                            = 0x3064,
            HiCounterRegion2_Tgl                                            = 0x3068,
            GainStepsLimits0_Tgl                                            = 0x306C,
            GainStepsLimits1_Tgl                                            = 0x3070,
            PnRfAttenuationCodeGroup0_Tgl                                   = 0x3074,
            PnRfAttenuationCodeGroup1_Tgl                                   = 0x3078,
            PnRfAttenuationCodeGroup2_Tgl                                   = 0x307C,
            PnRfAttenuationCodeGroup3_Tgl                                   = 0x3080,
            PnRfAttenuationCodeGroup4_Tgl                                   = 0x3084,
            PnRfAttenuationCodeGroup5_Tgl                                   = 0x3088,
            PnRfAttenuationCodeGroup6_Tgl                                   = 0x308C,
            PnRfAttenuationCodeGroup7_Tgl                                   = 0x3090,
            PnRfAttenuationCodeGroupAlternate_Tgl                           = 0x30A4,
            LnaMixSliceCodeGroup0_Tgl                                       = 0x30A8,
            LnaMixSliceCodeGroup1_Tgl                                       = 0x30AC,
            ProgrammableGainAmplifierGainCodeGroup0_Tgl                     = 0x30B0,
            ProgrammableGainAmplifierGainCodeGroup1_Tgl                     = 0x30B4,
            ListenBeforeTalkConfiguration_Tgl                               = 0x30B8,
            MirrorInterruptFlags_Tgl                                        = 0x30BC,
            SequencerInterruptFlags_Tgl                                     = 0x30C0,
            SequencerInterruptEnable_Tgl                                    = 0x30C4,
            ReceivedSignalStrengthIndicatorAbsoluteThreshold_Tgl            = 0x30C8,
            LnaBoostControl_Tgl                                             = 0x30CC,
            AntennaDiversity_Tgl                                            = 0x30D0,
            DualRfpkdThreshold0_Tgl                                         = 0x30D4,
            DualRfpkdThreshold1_Tgl                                         = 0x30D8,
            Spare_Tgl                                                       = 0x30DC,
            PnRfFilterCodeGroup0_Tgl                                        = 0x30E0,
            PnRfFilterCodeGroup1_Tgl                                        = 0x30E4,
            PnRfFilterCodeGroup2_Tgl                                        = 0x30E8,
            PnRfFilterCodeGroup3_Tgl                                        = 0x30EC,
            PnRfFilterCodeGroup4_Tgl                                        = 0x30F0,
            PnRfFilterCodeGroup5_Tgl                                        = 0x30F4,
            PnRfFilterCodeGroup6_Tgl                                        = 0x30F8,
            PnRfFilterCodeGroup7_Tgl                                        = 0x30FC,
            FeNotchAttenuationCodeGroup0_Tgl                                = 0x3100,
            FeNotchAttenuationCodeGroup1_Tgl                                = 0x3104,
            FeNotchAttenuationCodeGroup2_Tgl                                = 0x3108,
            FeNotchAttenuationCodeGroup3_Tgl                                = 0x310C,
            FeNotchAttenuationCodeGroup4_Tgl                                = 0x3110,
            FeNotchAttenuationCodeGroup5_Tgl                                = 0x3114,
            FeNotchAttenuationCodeGroup6_Tgl                                = 0x3118,
            FeNotchAttenuationCodeGroup7_Tgl                                = 0x311C,
            FeNotchAttenuationCodeGroup8_Tgl                                = 0x3120,
            FeNotchAttenuationCodeGroup9_Tgl                                = 0x3124,
            FeNotchAttenuationCodeGroup10_Tgl                               = 0x3128,
            FeNotchAttenuationCodeGroup11_Tgl                               = 0x312C,
            FeNotchFilterCodeGroup0_Tgl                                     = 0x3130,
            FeNotchFilterCodeGroup1_Tgl                                     = 0x3134,
            FeNotchFilterCodeGroup2_Tgl                                     = 0x3138,
            FeNotchFilterCodeGroup3_Tgl                                     = 0x313C,
            FeNotchFilterCodeGroup4_Tgl                                     = 0x3140,
            FeNotchFilterCodeGroup5_Tgl                                     = 0x3144,
            FeNotchFilterCodeGroup6_Tgl                                     = 0x3148,
            FeNotchFilterCodeGroup7_Tgl                                     = 0x314C,
            FeNotchFilterCodeGroup8_Tgl                                     = 0x3150,
            FeNotchFilterCodeGroup9_Tgl                                     = 0x3154,
            FeNotchFilterCodeGroup10_Tgl                                    = 0x3158,
            FeNotchFilterCodeGroup11_Tgl                                    = 0x315C,
            ClearChannelAssessmentDebug_Tgl                                 = 0x3160,
        }

        [RegistersDescription("crc", "crc_ns")]
        private enum CyclicRedundancyCheckRegisters : long
        {
            IpVersion                                                       = 0x0000,
            Enable                                                          = 0x0004,
            Control                                                         = 0x0008,
            Status                                                          = 0x000C,
            Command                                                         = 0x0010,
            InputData                                                       = 0x0014,
            InitializationValue                                             = 0x0018,
            Data                                                            = 0x001C,
            PolynomialValue                                                 = 0x0020,

            // Set registers
            IpVersion_Set                                                   = 0x1000,
            Enable_Set                                                      = 0x1004,
            Control_Set                                                     = 0x1008,
            Status_Set                                                      = 0x100C,
            Command_Set                                                     = 0x1010,
            InputData_Set                                                   = 0x1014,
            InitializationValue_Set                                         = 0x1018,
            Data_Set                                                        = 0x101C,
            PolynomialValue_Set                                             = 0x1020,

            // Clear registers
            IpVersion_Clr                                                   = 0x2000,
            Enable_Clr                                                      = 0x2004,
            Control_Clr                                                     = 0x2008,
            Status_Clr                                                      = 0x200C,
            Command_Clr                                                     = 0x2010,
            InputData_Clr                                                   = 0x2014,
            InitializationValue_Clr                                         = 0x2018,
            Data_Clr                                                        = 0x201C,
            PolynomialValue_Clr                                             = 0x2020,

            // Toggle registers
            IpVersion_Tgl                                                   = 0x3000,
            Enable_Tgl                                                      = 0x3004,
            Control_Tgl                                                     = 0x3008,
            Status_Tgl                                                      = 0x300C,
            Command_Tgl                                                     = 0x3010,
            InputData_Tgl                                                   = 0x3014,
            InitializationValue_Tgl                                         = 0x3018,
            Data_Tgl                                                        = 0x301C,
            PolynomialValue_Tgl                                             = 0x3020,
        }

        [RegistersDescription("protimer", "protimer_ns")]
        private enum ProtocolTimerRegisters : long
        {
            IpVersion                                                       = 0x0000,
            Enable                                                          = 0x0004,
            Control                                                         = 0x0008,
            Command                                                         = 0x000C,
            PrsControl                                                      = 0x0010,
            Status                                                          = 0x0014,
            PreCounterValue                                                 = 0x0018,
            BaseCounterValue                                                = 0x001C,
            WrapCounterValue                                                = 0x0020,
            BaseAndPreCounterValues                                         = 0x0024,
            LatchedWrapCounterValue                                         = 0x0028,
            PreCounterTopAdjustValue                                        = 0x002C,
            PreCounterTopValue                                              = 0x0030,
            BaseCounterTopValue                                             = 0x0034,
            WrapCounterTopValue                                             = 0x0038,
            Timeout0Counter                                                 = 0x003C,
            Timeout0CounterTop                                              = 0x0040,
            Timeout0Compare                                                 = 0x0044,
            Timeout1Counter                                                 = 0x0048,
            Timeout1CounterTop                                              = 0x004C,
            Timeout1Compare                                                 = 0x0050,
            ListenBeforeTalkWaitControl                                     = 0x0054,
            ListenBeforeTalkPrsControl                                      = 0x0058,
            ListenBeforeTalkState                                           = 0x005C,
            PseudoRandomGeneratorValue                                      = 0x0060,
            InterruptFlags                                                  = 0x0064,
            InterruptEnable                                                 = 0x0070,
            RxControl                                                       = 0x0074,
            TxControl                                                       = 0x0078,
            ListenBeforeTalkETSIStandardSupport                             = 0x007C,
            ListenBeforeTalkState1                                          = 0x0080,
            LinearRandomValueGeneratedByFirmware0                           = 0x0084,
            LinearRandomValueGeneratedByFirmware1                           = 0x0088,
            LinearRandomValueGeneratedByFirmware2                           = 0x008C,
            SequencerInterruptFlags                                         = 0x0090,
            SequencerInterruptEnable                                        = 0x0094,
            CaptureCompareChannel0Control                                   = 0x0100,
            CaptureCompareChannel0PreValue                                  = 0x0104,
            CaptureCompareChannel0BaseValue                                 = 0x0108,
            CaptureCompareChannel0WrapValue                                 = 0x010C,
            CaptureCompareChannel1Control                                   = 0x0110,
            CaptureCompareChannel1PreValue                                  = 0x0114,
            CaptureCompareChannel1BaseValue                                 = 0x0118,
            CaptureCompareChannel1WrapValue                                 = 0x011C,
            CaptureCompareChannel2Control                                   = 0x0120,
            CaptureCompareChannel2PreValue                                  = 0x0124,
            CaptureCompareChannel2BaseValue                                 = 0x0128,
            CaptureCompareChannel2WrapValue                                 = 0x012C,
            CaptureCompareChannel3Control                                   = 0x0130,
            CaptureCompareChannel3PreValue                                  = 0x0134,
            CaptureCompareChannel3BaseValue                                 = 0x0138,
            CaptureCompareChannel3WrapValue                                 = 0x013C,
            CaptureCompareChannel4Control                                   = 0x0140,
            CaptureCompareChannel4PreValue                                  = 0x0144,
            CaptureCompareChannel4BaseValue                                 = 0x0148,
            CaptureCompareChannel4WrapValue                                 = 0x014C,
            CaptureCompareChannel5Control                                   = 0x0150,
            CaptureCompareChannel5PreValue                                  = 0x0154,
            CaptureCompareChannel5BaseValue                                 = 0x0158,
            CaptureCompareChannel5WrapValue                                 = 0x015C,
            CaptureCompareChannel6Control                                   = 0x0160,
            CaptureCompareChannel6PreValue                                  = 0x0164,
            CaptureCompareChannel6BaseValue                                 = 0x0168,
            CaptureCompareChannel6WrapValue                                 = 0x016C,
            CaptureCompareChannel7Control                                   = 0x0170,
            CaptureCompareChannel7PreValue                                  = 0x0174,
            CaptureCompareChannel7BaseValue                                 = 0x0178,
            CaptureCompareChannel7WrapValue                                 = 0x017C,

            // Set registers
            IpVersion_Set                                                   = 0x1000,
            Enable_Set                                                      = 0x1004,
            Control_Set                                                     = 0x1008,
            Command_Set                                                     = 0x100C,
            PrsControl_Set                                                  = 0x1010,
            Status_Set                                                      = 0x1014,
            PreCounterValue_Set                                             = 0x1018,
            BaseCounterValue_Set                                            = 0x101C,
            WrapCounterValue_Set                                            = 0x1020,
            BaseAndPreCounterValues_Set                                     = 0x1024,
            LatchedWrapCounterValue_Set                                     = 0x1028,
            PreCounterTopAdjustValue_Set                                    = 0x102C,
            PreCounterTopValue_Set                                          = 0x1030,
            BaseCounterTopValue_Set                                         = 0x1034,
            WrapCounterTopValue_Set                                         = 0x1038,
            Timeout0Counter_Set                                             = 0x103C,
            Timeout0CounterTop_Set                                          = 0x1040,
            Timeout0Compare_Set                                             = 0x1044,
            Timeout1Counter_Set                                             = 0x1048,
            Timeout1CounterTop_Set                                          = 0x104C,
            Timeout1Compare_Set                                             = 0x1050,
            ListenBeforeTalkWaitControl_Set                                 = 0x1054,
            ListenBeforeTalkPrsControl_Set                                  = 0x1058,
            ListenBeforeTalkState_Set                                       = 0x105C,
            PseudoRandomGeneratorValue_Set                                  = 0x1060,
            InterruptFlags_Set                                              = 0x1064,
            InterruptEnable_Set                                             = 0x1070,
            RxControl_Set                                                   = 0x1074,
            TxControl_Set                                                   = 0x1078,
            ListenBeforeTalkETSIStandardSupport_Set                         = 0x107C,
            ListenBeforeTalkState1_Set                                      = 0x1080,
            LinearRandomValueGeneratedByFirmware0_Set                       = 0x1084,
            LinearRandomValueGeneratedByFirmware1_Set                       = 0x1088,
            LinearRandomValueGeneratedByFirmware2_Set                       = 0x108C,
            SequencerInterruptFlags_Set                                     = 0x1090,
            SequencerInterruptEnable_Set                                    = 0x1094,
            CaptureCompareChannel0Control_Set                               = 0x1100,
            CaptureCompareChannel0PreValue_Set                              = 0x1104,
            CaptureCompareChannel0BaseValue_Set                             = 0x1108,
            CaptureCompareChannel0WrapValue_Set                             = 0x110C,
            CaptureCompareChannel1Control_Set                               = 0x1110,
            CaptureCompareChannel1PreValue_Set                              = 0x1114,
            CaptureCompareChannel1BaseValue_Set                             = 0x1118,
            CaptureCompareChannel1WrapValue_Set                             = 0x111C,
            CaptureCompareChannel2Control_Set                               = 0x1120,
            CaptureCompareChannel2PreValue_Set                              = 0x1124,
            CaptureCompareChannel2BaseValue_Set                             = 0x1128,
            CaptureCompareChannel2WrapValue_Set                             = 0x112C,
            CaptureCompareChannel3Control_Set                               = 0x1130,
            CaptureCompareChannel3PreValue_Set                              = 0x1134,
            CaptureCompareChannel3BaseValue_Set                             = 0x1138,
            CaptureCompareChannel3WrapValue_Set                             = 0x113C,
            CaptureCompareChannel4Control_Set                               = 0x1140,
            CaptureCompareChannel4PreValue_Set                              = 0x1144,
            CaptureCompareChannel4BaseValue_Set                             = 0x1148,
            CaptureCompareChannel4WrapValue_Set                             = 0x114C,
            CaptureCompareChannel5Control_Set                               = 0x1150,
            CaptureCompareChannel5PreValue_Set                              = 0x1154,
            CaptureCompareChannel5BaseValue_Set                             = 0x1158,
            CaptureCompareChannel5WrapValue_Set                             = 0x115C,
            CaptureCompareChannel6Control_Set                               = 0x1160,
            CaptureCompareChannel6PreValue_Set                              = 0x1164,
            CaptureCompareChannel6BaseValue_Set                             = 0x1168,
            CaptureCompareChannel6WrapValue_Set                             = 0x116C,
            CaptureCompareChannel7Control_Set                               = 0x1170,
            CaptureCompareChannel7PreValue_Set                              = 0x1174,
            CaptureCompareChannel7BaseValue_Set                             = 0x1178,
            CaptureCompareChannel7WrapValue_Set                             = 0x117C,

            // Clear registers
            IpVersion_Clr                                                   = 0x2000,
            Enable_Clr                                                      = 0x2004,
            Control_Clr                                                     = 0x2008,
            Command_Clr                                                     = 0x200C,
            PrsControl_Clr                                                  = 0x2010,
            Status_Clr                                                      = 0x2014,
            PreCounterValue_Clr                                             = 0x2018,
            BaseCounterValue_Clr                                            = 0x201C,
            WrapCounterValue_Clr                                            = 0x2020,
            BaseAndPreCounterValues_Clr                                     = 0x2024,
            LatchedWrapCounterValue_Clr                                     = 0x2028,
            PreCounterTopAdjustValue_Clr                                    = 0x202C,
            PreCounterTopValue_Clr                                          = 0x2030,
            BaseCounterTopValue_Clr                                         = 0x2034,
            WrapCounterTopValue_Clr                                         = 0x2038,
            Timeout0Counter_Clr                                             = 0x203C,
            Timeout0CounterTop_Clr                                          = 0x2040,
            Timeout0Compare_Clr                                             = 0x2044,
            Timeout1Counter_Clr                                             = 0x2048,
            Timeout1CounterTop_Clr                                          = 0x204C,
            Timeout1Compare_Clr                                             = 0x2050,
            ListenBeforeTalkWaitControl_Clr                                 = 0x2054,
            ListenBeforeTalkPrsControl_Clr                                  = 0x2058,
            ListenBeforeTalkState_Clr                                       = 0x205C,
            PseudoRandomGeneratorValue_Clr                                  = 0x2060,
            InterruptFlags_Clr                                              = 0x2064,
            InterruptEnable_Clr                                             = 0x2070,
            RxControl_Clr                                                   = 0x2074,
            TxControl_Clr                                                   = 0x2078,
            ListenBeforeTalkETSIStandardSupport_Clr                         = 0x207C,
            ListenBeforeTalkState1_Clr                                      = 0x2080,
            LinearRandomValueGeneratedByFirmware0_Clr                       = 0x2084,
            LinearRandomValueGeneratedByFirmware1_Clr                       = 0x2088,
            LinearRandomValueGeneratedByFirmware2_Clr                       = 0x208C,
            SequencerInterruptFlags_Clr                                     = 0x2090,
            SequencerInterruptEnable_Clr                                    = 0x2094,
            CaptureCompareChannel0Control_Clr                               = 0x2100,
            CaptureCompareChannel0PreValue_Clr                              = 0x2104,
            CaptureCompareChannel0BaseValue_Clr                             = 0x2108,
            CaptureCompareChannel0WrapValue_Clr                             = 0x210C,
            CaptureCompareChannel1Control_Clr                               = 0x2110,
            CaptureCompareChannel1PreValue_Clr                              = 0x2114,
            CaptureCompareChannel1BaseValue_Clr                             = 0x2118,
            CaptureCompareChannel1WrapValue_Clr                             = 0x211C,
            CaptureCompareChannel2Control_Clr                               = 0x2120,
            CaptureCompareChannel2PreValue_Clr                              = 0x2124,
            CaptureCompareChannel2BaseValue_Clr                             = 0x2128,
            CaptureCompareChannel2WrapValue_Clr                             = 0x212C,
            CaptureCompareChannel3Control_Clr                               = 0x2130,
            CaptureCompareChannel3PreValue_Clr                              = 0x2134,
            CaptureCompareChannel3BaseValue_Clr                             = 0x2138,
            CaptureCompareChannel3WrapValue_Clr                             = 0x213C,
            CaptureCompareChannel4Control_Clr                               = 0x2140,
            CaptureCompareChannel4PreValue_Clr                              = 0x2144,
            CaptureCompareChannel4BaseValue_Clr                             = 0x2148,
            CaptureCompareChannel4WrapValue_Clr                             = 0x214C,
            CaptureCompareChannel5Control_Clr                               = 0x2150,
            CaptureCompareChannel5PreValue_Clr                              = 0x2154,
            CaptureCompareChannel5BaseValue_Clr                             = 0x2158,
            CaptureCompareChannel5WrapValue_Clr                             = 0x215C,
            CaptureCompareChannel6Control_Clr                               = 0x2160,
            CaptureCompareChannel6PreValue_Clr                              = 0x2164,
            CaptureCompareChannel6BaseValue_Clr                             = 0x2168,
            CaptureCompareChannel6WrapValue_Clr                             = 0x216C,
            CaptureCompareChannel7Control_Clr                               = 0x2170,
            CaptureCompareChannel7PreValue_Clr                              = 0x2174,
            CaptureCompareChannel7BaseValue_Clr                             = 0x2178,
            CaptureCompareChannel7WrapValue_Clr                             = 0x217C,

            // Toggle registers
            IpVersion_Tgl                                                   = 0x3000,
            Enable_Tgl                                                      = 0x3004,
            Control_Tgl                                                     = 0x3008,
            Command_Tgl                                                     = 0x300C,
            PrsControl_Tgl                                                  = 0x3010,
            Status_Tgl                                                      = 0x3014,
            PreCounterValue_Tgl                                             = 0x3018,
            BaseCounterValue_Tgl                                            = 0x301C,
            WrapCounterValue_Tgl                                            = 0x3020,
            BaseAndPreCounterValues_Tgl                                     = 0x3024,
            LatchedWrapCounterValue_Tgl                                     = 0x3028,
            PreCounterTopAdjustValue_Tgl                                    = 0x302C,
            PreCounterTopValue_Tgl                                          = 0x3030,
            BaseCounterTopValue_Tgl                                         = 0x3034,
            WrapCounterTopValue_Tgl                                         = 0x3038,
            Timeout0Counter_Tgl                                             = 0x303C,
            Timeout0CounterTop_Tgl                                          = 0x3040,
            Timeout0Compare_Tgl                                             = 0x3044,
            Timeout1Counter_Tgl                                             = 0x3048,
            Timeout1CounterTop_Tgl                                          = 0x304C,
            Timeout1Compare_Tgl                                             = 0x3050,
            ListenBeforeTalkWaitControl_Tgl                                 = 0x3054,
            ListenBeforeTalkPrsControl_Tgl                                  = 0x3058,
            ListenBeforeTalkState_Tgl                                       = 0x305C,
            PseudoRandomGeneratorValue_Tgl                                  = 0x3060,
            InterruptFlags_Tgl                                              = 0x3064,
            InterruptEnable_Tgl                                             = 0x3070,
            RxControl_Tgl                                                   = 0x3074,
            TxControl_Tgl                                                   = 0x3078,
            ListenBeforeTalkETSIStandardSupport_Tgl                         = 0x307C,
            ListenBeforeTalkState1_Tgl                                      = 0x3080,
            LinearRandomValueGeneratedByFirmware0_Tgl                       = 0x3084,
            LinearRandomValueGeneratedByFirmware1_Tgl                       = 0x3088,
            LinearRandomValueGeneratedByFirmware2_Tgl                       = 0x308C,
            SequencerInterruptFlags_Tgl                                     = 0x3090,
            SequencerInterruptEnable_Tgl                                    = 0x3094,
            CaptureCompareChannel0Control_Tgl                               = 0x3100,
            CaptureCompareChannel0PreValue_Tgl                              = 0x3104,
            CaptureCompareChannel0BaseValue_Tgl                             = 0x3108,
            CaptureCompareChannel0WrapValue_Tgl                             = 0x310C,
            CaptureCompareChannel1Control_Tgl                               = 0x3110,
            CaptureCompareChannel1PreValue_Tgl                              = 0x3114,
            CaptureCompareChannel1BaseValue_Tgl                             = 0x3118,
            CaptureCompareChannel1WrapValue_Tgl                             = 0x311C,
            CaptureCompareChannel2Control_Tgl                               = 0x3120,
            CaptureCompareChannel2PreValue_Tgl                              = 0x3124,
            CaptureCompareChannel2BaseValue_Tgl                             = 0x3128,
            CaptureCompareChannel2WrapValue_Tgl                             = 0x312C,
            CaptureCompareChannel3Control_Tgl                               = 0x3130,
            CaptureCompareChannel3PreValue_Tgl                              = 0x3134,
            CaptureCompareChannel3BaseValue_Tgl                             = 0x3138,
            CaptureCompareChannel3WrapValue_Tgl                             = 0x313C,
            CaptureCompareChannel4Control_Tgl                               = 0x3140,
            CaptureCompareChannel4PreValue_Tgl                              = 0x3144,
            CaptureCompareChannel4BaseValue_Tgl                             = 0x3148,
            CaptureCompareChannel4WrapValue_Tgl                             = 0x314C,
            CaptureCompareChannel5Control_Tgl                               = 0x3150,
            CaptureCompareChannel5PreValue_Tgl                              = 0x3154,
            CaptureCompareChannel5BaseValue_Tgl                             = 0x3158,
            CaptureCompareChannel5WrapValue_Tgl                             = 0x315C,
            CaptureCompareChannel6Control_Tgl                               = 0x3160,
            CaptureCompareChannel6PreValue_Tgl                              = 0x3164,
            CaptureCompareChannel6BaseValue_Tgl                             = 0x3168,
            CaptureCompareChannel6WrapValue_Tgl                             = 0x316C,
            CaptureCompareChannel7Control_Tgl                               = 0x3170,
            CaptureCompareChannel7PreValue_Tgl                              = 0x3174,
            CaptureCompareChannel7BaseValue_Tgl                             = 0x3178,
            CaptureCompareChannel7WrapValue_Tgl                             = 0x317C,
        }

        [RegistersDescription("rac", "rac_ns")]
        private enum RadioControllerRegisters : long
        {
            IpVersion                                                       = 0x0000,
            Enable                                                          = 0x0004,
            RXENSourceEnable                                                = 0x0008,
            Status                                                          = 0x000C,
            Command                                                         = 0x0010,
            Control                                                         = 0x0014,
            ForceStateTransition                                            = 0x0018,
            InterruptFlags                                                  = 0x001C,
            InterruptEnable                                                 = 0x0020,
            TestControl                                                     = 0x0024,
            SequencerInterruptFlags                                         = 0x0028,
            SequencerInterruptEnable                                        = 0x002C,
            Status1                                                         = 0x0030,
            SequencerTimerValue                                             = 0x0034,
            SequencerTimerCompareValue                                      = 0x0038,
            SequencerControl                                                = 0x003C,
            SequencerPrescaler                                              = 0x0040,
            Storage0                                                        = 0x0044,
            Storage1                                                        = 0x0048,
            Storage2                                                        = 0x004C,
            Storage3                                                        = 0x0050,
            SystemTickTimerControl                                          = 0x0054,
            FrameControlWordBufferWrite                                     = 0x0058,
            FrameControlWordBufferRead                                      = 0x005C,
            Em1pControlAndStatus                                            = 0x0060,
            SynthesizerEnableControl                                        = 0x0098,
            SynthesizerRegulatorEnableControl                               = 0x009C,
            VoltageControlledOscillatorControl                              = 0x00A0,
            ChargePumpControl                                               = 0x00A4,
            RadioFrequencyStatus                                            = 0x00AC,
            Status2                                                         = 0x00B0,
            IntermediateFrequencyProgrammableGainAmplifierControl           = 0x00B4,
            PowerAmplifierEnableControl                                     = 0x00B8,
            AutomaticPowerControl                                           = 0x00BC,
            AntennaDiversity                                                = 0x00C0,
            AuxiliaryAnalogToDigitalConverterTrim                           = 0x00C4,
            AuxiliaryAnalogToDigitalConverterEnable                         = 0x00C8,
            AuxiliaryAnalogToDigitalConverterControl0                       = 0x00CC,
            AuxiliaryAnalogToDigitalConverterControl1                       = 0x00D0,
            AuxiliaryAnalogToDigitalConverterDigitalOutput                  = 0x00D4,
            ClockMultEnable0                                                = 0x00D8,
            ClockMultEnable1                                                = 0x00DC,
            ClockMultControl                                                = 0x00E0,
            ClockMultStatus                                                 = 0x00E4,
            IntermediateFrequencyAnalogToDigitalConverterDebug              = 0x00E8,
            IntermediateFrequencyAnalogToDigitalConverterTrim0              = 0x00EC,
            IntermediateFrequencyAnalogToDigitalConverterTrim1              = 0x00F0,
            IntermediateFrequencyAnalogToDigitalConverterCalibration        = 0x00F4,
            IntermediateFrequencyAnalogToDigitalConverterStatus             = 0x00F8,
            LowNoiseAmplifierMixerDebug                                     = 0x00FC,
            LowNoiseAmplifierMixerTrim0                                     = 0x0100,
            LowNoiseAmplifierMixerTrim1                                     = 0x0104,
            LowNoiseAmplifierMixerTrim2                                     = 0x0108,
            LowNoiseAmplifierMixerTrim3                                     = 0x010C,
            LowNoiseAmplifierMixerTrim4                                     = 0x0110,
            LowNoiseAmplifierMixerCalibration                               = 0x0114,
            LowNoiseAmplifierMixerEnable                                    = 0x0118,
            PreCounterOverflow                                              = 0x011C,
            PowerAmplifierTrim0                                             = 0x0120,
            PowerAmplifierTrim1                                             = 0x0124,
            PowerAmplifierTrim2                                             = 0x0128,
            PowerAmplifierTrim3                                             = 0x012C,
            PowerAmplifierTrim4                                             = 0x0130,
            PowerAmplifierTrim5                                             = 0x0134,
            TxPower                                                         = 0x013C,
            TxRamp                                                          = 0x0140,
            ProgrammableGainAmplifierTrim                                   = 0x0148,
            ProgrammableGainAmplifierCalibration                            = 0x014C,
            ProgrammableGainAmplifierControl                                = 0x0150,
            RadioFrequencyBiasCalibration                                   = 0x0154,
            RadioFrequencyBiasControl                                       = 0x0158,
            RadioEnable                                                     = 0x015C,
            RadioFrequencyPathEnable0                                       = 0x0160,
            RadioFrequencyPathEnable1                                       = 0x0164,
            Rx                                                              = 0x0168,
            Tx                                                              = 0x016C,
            SyDebug                                                         = 0x0170,
            SyTrim0                                                         = 0x0174,
            SyTrim1                                                         = 0x0178,
            SyCalibration                                                   = 0x017C,
            SyEnable                                                        = 0x0180,
            SyloEnable                                                      = 0x0184,
            SymmControl                                                     = 0x0188,
            DigitalClockRetimeControl                                       = 0x0190,
            DigitalClockRetimeStatus                                        = 0x0194,
            XoRetimeControl                                                 = 0x0198,
            XoRetimeStatus                                                  = 0x019C,
            AutomaticGainControlOverwrite0                                  = 0x01A0,
            AutomaticGainControlOverwrite1                                  = 0x01A4,
            AutomaticGainControlOverwrite2                                  = 0x01A8,
            Spare                                                           = 0x01C4,
            PowerAmplifierControl                                           = 0x01C8,
            FeNotch0                                                        = 0x01CC,
            FeNotch1                                                        = 0x01D0,
            Scratch0                                                        = 0x03E0,
            Scratch1                                                        = 0x03E4,
            Scratch2                                                        = 0x03E8,
            Scratch3                                                        = 0x03EC,
            Scratch4                                                        = 0x03F0,
            Scratch5                                                        = 0x03F4,
            Scratch6                                                        = 0x03F8,
            Scratch7                                                        = 0x03FC,
            ThermisterControl                                               = 0x07E8,
            DiagaAlternateEnable                                            = 0x07EC,
            DiagaAlternateRfBlocksAndTpSelect                               = 0x07F0,
            DiagaAlternateBridgeControl                                     = 0x07F4,
            RadioFrequencyLock0                                             = 0x07F8,
            RadioFrequencyLock1                                             = 0x07FC,

            // Set registers
            IpVersion_Set                                                   = 0x1000,
            Enable_Set                                                      = 0x1004,
            RXENSourceEnable_Set                                            = 0x1008,
            Status0_Set                                                     = 0x100C,
            Command_Set                                                     = 0x1010,
            Control_Set                                                     = 0x1014,
            ForceStateTransition_Set                                        = 0x1018,
            InterruptFlags_Set                                              = 0x101C,
            InterruptEnable_Set                                             = 0x1020,
            TestControl_Set                                                 = 0x1024,
            SequencerInterruptFlags_Set                                     = 0x1028,
            SequencerInterruptEnable_Set                                    = 0x102C,
            Status1_Set                                                     = 0x1030,
            SequencerTimerValue_Set                                         = 0x1034,
            SequencerTimerCompareValue_Set                                  = 0x1038,
            SequencerControl_Set                                            = 0x103C,
            SequencerPrescaler_Set                                          = 0x1040,
            Storage0_Set                                                    = 0x1044,
            Storage1_Set                                                    = 0x1048,
            Storage2_Set                                                    = 0x104C,
            Storage3_Set                                                    = 0x1050,
            SystemTickTimerControl_Set                                      = 0x1054,
            FrameControlWordBufferWrite_Set                                 = 0x1058,
            FrameControlWordBufferRead_Set                                  = 0x105C,
            Em1pControlAndStatus_Set                                        = 0x1060,
            SynthesizerEnableControl_Set                                    = 0x1098,
            SynthesizerRegulatorEnableControl_Set                           = 0x109C,
            VoltageControlledOscillatorControl_Set                          = 0x10A0,
            ChargePumpControl_Set                                           = 0x10A4,
            RadioFrequencyStatus_Set                                        = 0x10AC,
            Status2_Set                                                     = 0x10B0,
            IntermediateFrequencyProgrammableGainAmplifierControl_Set       = 0x10B4,
            PowerAmplifierEnableControl_Set                                 = 0x10B8,
            AutomaticPowerControl_Set                                       = 0x10BC,
            AntennaDiversity_Set                                            = 0x10C0,
            AuxiliaryAnalogToDigitalConverterTrim_Set                       = 0x10C4,
            AuxiliaryAnalogToDigitalConverterEnable_Set                     = 0x10C8,
            AuxiliaryAnalogToDigitalConverterControl0_Set                   = 0x10CC,
            AuxiliaryAnalogToDigitalConverterControl1_Set                   = 0x10D0,
            AuxiliaryAnalogToDigitalConverterDigitalOutput_Set              = 0x10D4,
            ClockMultEnable0_Set                                            = 0x10D8,
            ClockMultEnable1_Set                                            = 0x10DC,
            ClockMultControl_Set                                            = 0x10E0,
            ClockMultStatus_Set                                             = 0x10E4,
            IntermediateFrequencyAnalogToDigitalConverterDebug_Set          = 0x10E8,
            IntermediateFrequencyAnalogToDigitalConverterTrim0_Set          = 0x10EC,
            IntermediateFrequencyAnalogToDigitalConverterTrim1_Set          = 0x10F0,
            IntermediateFrequencyAnalogToDigitalConverterCalibration_Set    = 0x10F4,
            IntermediateFrequencyAnalogToDigitalConverterStatus_Set         = 0x10F8,
            LowNoiseAmplifierMixerDebug_Set                                 = 0x10FC,
            LowNoiseAmplifierMixerTrim0_Set                                 = 0x1100,
            LowNoiseAmplifierMixerTrim1_Set                                 = 0x1104,
            LowNoiseAmplifierMixerTrim2_Set                                 = 0x1108,
            LowNoiseAmplifierMixerTrim3_Set                                 = 0x110C,
            LowNoiseAmplifierMixerTrim4_Set                                 = 0x1110,
            LowNoiseAmplifierMixerCalibration_Set                           = 0x1114,
            LowNoiseAmplifierMixerEnable_Set                                = 0x1118,
            PreCounterOverflow_Set                                          = 0x111C,
            PowerAmplifierTrim0_Set                                         = 0x1120,
            PowerAmplifierTrim1_Set                                         = 0x1124,
            PowerAmplifierTrim2_Set                                         = 0x1128,
            PowerAmplifierTrim3_Set                                         = 0x112C,
            PowerAmplifierTrim4_Set                                         = 0x1130,
            PowerAmplifierTrim5_Set                                         = 0x1134,
            TxPower_Set                                                     = 0x113C,
            TxRamp_Set                                                      = 0x1140,
            ProgrammableGainAmplifierTrim_Set                               = 0x1148,
            ProgrammableGainAmplifierCalibration_Set                        = 0x114C,
            ProgrammableGainAmplifierControl_Set                            = 0x1150,
            RadioFrequencyBiasCalibration_Set                               = 0x1154,
            RadioFrequencyBiasControl_Set                                   = 0x1158,
            RadioEnable_Set                                                 = 0x115C,
            RadioFrequencyPathEnable0_Set                                   = 0x1160,
            RadioFrequencyPathEnable1_Set                                   = 0x1164,
            Rx_Set                                                          = 0x1168,
            Tx_Set                                                          = 0x116C,
            SyDebug_Set                                                     = 0x1170,
            SyTrim0_Set                                                     = 0x1174,
            SyTrim1_Set                                                     = 0x1178,
            SyCalibration_Set                                               = 0x117C,
            SyEnable_Set                                                    = 0x1180,
            SyloEnable_Set                                                  = 0x1184,
            SymmControl_Set                                                 = 0x1188,
            DigitalClockRetimeControl_Set                                   = 0x1190,
            DigitalClockRetimeStatus_Set                                    = 0x1194,
            XoRetimeControl_Set                                             = 0x1198,
            XoRetimeStatus_Set                                              = 0x119C,
            AutomaticGainControlOverwrite0_Set                              = 0x11A0,
            AutomaticGainControlOverwrite1_Set                              = 0x11A4,
            AutomaticGainControlOverwrite2_Set                              = 0x11A8,
            Spare_Set                                                       = 0x11C4,
            PowerAmplifierControl_Set                                       = 0x11C8,
            FeNotch0_Set                                                    = 0x11CC,
            FeNotch1_Set                                                    = 0x11D0,
            Scratch0_Set                                                    = 0x13E0,
            Scratch1_Set                                                    = 0x13E4,
            Scratch2_Set                                                    = 0x13E8,
            Scratch3_Set                                                    = 0x13EC,
            Scratch4_Set                                                    = 0x13F0,
            Scratch5_Set                                                    = 0x13F4,
            Scratch6_Set                                                    = 0x13F8,
            Scratch7_Set                                                    = 0x13FC,
            ThermisterControl_Set                                           = 0x17E8,
            DiagaAlternateEnable_Set                                        = 0x17EC,
            DiagaAlternateRfBlocksAndTpSelect_Set                           = 0x17F0,
            DiagaAlternateBridgeControl_Set                                 = 0x17F4,
            RadioFrequencyLock0_Set                                         = 0x17F8,
            RadioFrequencyLock1_Set                                         = 0x17FC,

            // Clear registers
            IpVersion_Clr                                                   = 0x2000,
            Enable_Clr                                                      = 0x2004,
            RXENSourceEnable_Clr                                            = 0x2008,
            Status0_Clr                                                     = 0x200C,
            Command_Clr                                                     = 0x2010,
            Control_Clr                                                     = 0x2014,
            ForceStateTransition_Clr                                        = 0x2018,
            InterruptFlags_Clr                                              = 0x201C,
            InterruptEnable_Clr                                             = 0x2020,
            TestControl_Clr                                                 = 0x2024,
            SequencerInterruptFlags_Clr                                     = 0x2028,
            SequencerInterruptEnable_Clr                                    = 0x202C,
            Status1_Clr                                                     = 0x2030,
            SequencerTimerValue_Clr                                         = 0x2034,
            SequencerTimerCompareValue_Clr                                  = 0x2038,
            SequencerControl_Clr                                            = 0x203C,
            SequencerPrescaler_Clr                                          = 0x2040,
            Storage0_Clr                                                    = 0x2044,
            Storage1_Clr                                                    = 0x2048,
            Storage2_Clr                                                    = 0x204C,
            Storage3_Clr                                                    = 0x2050,
            SystemTickTimerControl_Clr                                      = 0x2054,
            FrameControlWordBufferWrite_Clr                                 = 0x2058,
            FrameControlWordBufferRead_Clr                                  = 0x205C,
            Em1pControlAndStatus_Clr                                        = 0x2060,
            SynthesizerEnableControl_Clr                                    = 0x2098,
            SynthesizerRegulatorEnableControl_Clr                           = 0x209C,
            VoltageControlledOscillatorControl_Clr                          = 0x20A0,
            ChargePumpControl_Clr                                           = 0x20A4,
            RadioFrequencyStatus_Clr                                        = 0x20AC,
            Status2_Clr                                                     = 0x20B0,
            IntermediateFrequencyProgrammableGainAmplifierControl_Clr       = 0x20B4,
            PowerAmplifierEnableControl_Clr                                 = 0x20B8,
            AutomaticPowerControl_Clr                                       = 0x20BC,
            AntennaDiversity_Clr                                            = 0x20C0,
            AuxiliaryAnalogToDigitalConverterTrim_Clr                       = 0x20C4,
            AuxiliaryAnalogToDigitalConverterEnable_Clr                     = 0x20C8,
            AuxiliaryAnalogToDigitalConverterControl0_Clr                   = 0x20CC,
            AuxiliaryAnalogToDigitalConverterControl1_Clr                   = 0x20D0,
            AuxiliaryAnalogToDigitalConverterDigitalOutput_Clr              = 0x20D4,
            ClockMultEnable0_Clr                                            = 0x20D8,
            ClockMultEnable1_Clr                                            = 0x20DC,
            ClockMultControl_Clr                                            = 0x20E0,
            ClockMultStatus_Clr                                             = 0x20E4,
            IntermediateFrequencyAnalogToDigitalConverterDebug_Clr          = 0x20E8,
            IntermediateFrequencyAnalogToDigitalConverterTrim0_Clr          = 0x20EC,
            IntermediateFrequencyAnalogToDigitalConverterTrim1_Clr          = 0x20F0,
            IntermediateFrequencyAnalogToDigitalConverterCalibration_Clr    = 0x20F4,
            IntermediateFrequencyAnalogToDigitalConverterStatus_Clr         = 0x20F8,
            LowNoiseAmplifierMixerDebug_Clr                                 = 0x20FC,
            LowNoiseAmplifierMixerTrim0_Clr                                 = 0x2100,
            LowNoiseAmplifierMixerTrim1_Clr                                 = 0x2104,
            LowNoiseAmplifierMixerTrim2_Clr                                 = 0x2108,
            LowNoiseAmplifierMixerTrim3_Clr                                 = 0x210C,
            LowNoiseAmplifierMixerTrim4_Clr                                 = 0x2110,
            LowNoiseAmplifierMixerCalibration_Clr                           = 0x2114,
            LowNoiseAmplifierMixerEnable_Clr                                = 0x2118,
            PreCounterOverflow_Clr                                          = 0x211C,
            PowerAmplifierTrim0_Clr                                         = 0x2120,
            PowerAmplifierTrim1_Clr                                         = 0x2124,
            PowerAmplifierTrim2_Clr                                         = 0x2128,
            PowerAmplifierTrim3_Clr                                         = 0x212C,
            PowerAmplifierTrim4_Clr                                         = 0x2130,
            PowerAmplifierTrim5_Clr                                         = 0x2134,
            TxPower_Clr                                                     = 0x213C,
            TxRamp_Clr                                                      = 0x2140,
            ProgrammableGainAmplifierTrim_Clr                               = 0x2148,
            ProgrammableGainAmplifierCalibration_Clr                        = 0x214C,
            ProgrammableGainAmplifierControl_Clr                            = 0x2150,
            RadioFrequencyBiasCalibration_Clr                               = 0x2154,
            RadioFrequencyBiasControl_Clr                                   = 0x2158,
            RadioEnable_Clr                                                 = 0x215C,
            RadioFrequencyPathEnable0_Clr                                   = 0x2160,
            RadioFrequencyPathEnable1_Clr                                   = 0x2164,
            Rx_Clr                                                          = 0x2168,
            Tx_Clr                                                          = 0x216C,
            SyDebug_Clr                                                     = 0x2170,
            SyTrim0_Clr                                                     = 0x2174,
            SyTrim1_Clr                                                     = 0x2178,
            SyCalibration_Clr                                               = 0x217C,
            SyEnable_Clr                                                    = 0x2180,
            SyloEnable_Clr                                                  = 0x2184,
            SymmControl_Clr                                                 = 0x2188,
            DigitalClockRetimeControl_Clr                                   = 0x2190,
            DigitalClockRetimeStatus_Clr                                    = 0x2194,
            XoRetimeControl_Clr                                             = 0x2198,
            XoRetimeStatus_Clr                                              = 0x219C,
            AutomaticGainControlOverwrite0_Clr                              = 0x21A0,
            AutomaticGainControlOverwrite1_Clr                              = 0x21A4,
            AutomaticGainControlOverwrite2_Clr                              = 0x21A8,
            Spare_Clr                                                       = 0x21C4,
            PowerAmplifierControl_Clr                                       = 0x21C8,
            FeNotch0_Clr                                                    = 0x21CC,
            FeNotch1_Clr                                                    = 0x21D0,
            Scratch0_Clr                                                    = 0x23E0,
            Scratch1_Clr                                                    = 0x23E4,
            Scratch2_Clr                                                    = 0x23E8,
            Scratch3_Clr                                                    = 0x23EC,
            Scratch4_Clr                                                    = 0x23F0,
            Scratch5_Clr                                                    = 0x23F4,
            Scratch6_Clr                                                    = 0x23F8,
            Scratch7_Clr                                                    = 0x23FC,
            ThermisterControl_Clr                                           = 0x27E8,
            DiagaAlternateEnable_Clr                                        = 0x27EC,
            DiagaAlternateRfBlocksAndTpSelect_Clr                           = 0x27F0,
            DiagaAlternateBridgeControl_Clr                                 = 0x27F4,
            RadioFrequencyLock0_Clr                                         = 0x27F8,
            RadioFrequencyLock1_Clr                                         = 0x27FC,

            // Toggle registers
            IpVersion_Tgl                                                   = 0x3000,
            Enable_Tgl                                                      = 0x3004,
            RXENSourceEnable_Tgl                                            = 0x3008,
            Status0_Tgl                                                     = 0x300C,
            Command_Tgl                                                     = 0x3010,
            Control_Tgl                                                     = 0x3014,
            ForceStateTransition_Tgl                                        = 0x3018,
            InterruptFlags_Tgl                                              = 0x301C,
            InterruptEnable_Tgl                                             = 0x3020,
            TestControl_Tgl                                                 = 0x3024,
            SequencerInterruptFlags_Tgl                                     = 0x3028,
            SequencerInterruptEnable_Tgl                                    = 0x302C,
            Status1_Tgl                                                     = 0x3030,
            SequencerTimerValue_Tgl                                         = 0x3034,
            SequencerTimerCompareValue_Tgl                                  = 0x3038,
            SequencerControl_Tgl                                            = 0x303C,
            SequencerPrescaler_Tgl                                          = 0x3040,
            Storage0_Tgl                                                    = 0x3044,
            Storage1_Tgl                                                    = 0x3048,
            Storage2_Tgl                                                    = 0x304C,
            Storage3_Tgl                                                    = 0x3050,
            SystemTickTimerControl_Tgl                                      = 0x3054,
            FrameControlWordBufferWrite_Tgl                                 = 0x3058,
            FrameControlWordBufferRead_Tgl                                  = 0x305C,
            Em1pControlAndStatus_Tgl                                        = 0x3060,
            SynthesizerEnableControl_Tgl                                    = 0x3098,
            SynthesizerRegulatorEnableControl_Tgl                           = 0x309C,
            VoltageControlledOscillatorControl_Tgl                          = 0x30A0,
            ChargePumpControl_Tgl                                           = 0x30A4,
            RadioFrequencyStatus_Tgl                                        = 0x30AC,
            Status2_Tgl                                                     = 0x30B0,
            IntermediateFrequencyProgrammableGainAmplifierControl_Tgl       = 0x30B4,
            PowerAmplifierEnableControl_Tgl                                 = 0x30B8,
            AutomaticPowerControl_Tgl                                       = 0x30BC,
            AntennaDiversity_Tgl                                            = 0x30C0,
            AuxiliaryAnalogToDigitalConverterTrim_Tgl                       = 0x30C4,
            AuxiliaryAnalogToDigitalConverterEnable_Tgl                     = 0x30C8,
            AuxiliaryAnalogToDigitalConverterControl0_Tgl                   = 0x30CC,
            AuxiliaryAnalogToDigitalConverterControl1_Tgl                   = 0x30D0,
            AuxiliaryAnalogToDigitalConverterDigitalOutput_Tgl              = 0x30D4,
            ClockMultEnable0_Tgl                                            = 0x30D8,
            ClockMultEnable1_Tgl                                            = 0x30DC,
            ClockMultControl_Tgl                                            = 0x30E0,
            ClockMultStatus_Tgl                                             = 0x30E4,
            IntermediateFrequencyAnalogToDigitalConverterDebug_Tgl          = 0x30E8,
            IntermediateFrequencyAnalogToDigitalConverterTrim0_Tgl          = 0x30EC,
            IntermediateFrequencyAnalogToDigitalConverterTrim1_Tgl          = 0x30F0,
            IntermediateFrequencyAnalogToDigitalConverterCalibration_Tgl    = 0x30F4,
            IntermediateFrequencyAnalogToDigitalConverterStatus_Tgl         = 0x30F8,
            LowNoiseAmplifierMixerDebug_Tgl                                 = 0x30FC,
            LowNoiseAmplifierMixerTrim0_Tgl                                 = 0x3100,
            LowNoiseAmplifierMixerTrim1_Tgl                                 = 0x3104,
            LowNoiseAmplifierMixerTrim2_Tgl                                 = 0x3108,
            LowNoiseAmplifierMixerTrim3_Tgl                                 = 0x310C,
            LowNoiseAmplifierMixerTrim4_Tgl                                 = 0x3110,
            LowNoiseAmplifierMixerCalibration_Tgl                           = 0x3114,
            LowNoiseAmplifierMixerEnable_Tgl                                = 0x3118,
            PreCounterOverflow_Tgl                                          = 0x311C,
            PowerAmplifierTrim0_Tgl                                         = 0x3120,
            PowerAmplifierTrim1_Tgl                                         = 0x3124,
            PowerAmplifierTrim2_Tgl                                         = 0x3128,
            PowerAmplifierTrim3_Tgl                                         = 0x312C,
            PowerAmplifierTrim4_Tgl                                         = 0x3130,
            PowerAmplifierTrim5_Tgl                                         = 0x3134,
            TxPower_Tgl                                                     = 0x313C,
            TxRamp_Tgl                                                      = 0x3140,
            ProgrammableGainAmplifierTrim_Tgl                               = 0x3148,
            ProgrammableGainAmplifierCalibration_Tgl                        = 0x314C,
            ProgrammableGainAmplifierControl_Tgl                            = 0x3150,
            RadioFrequencyBiasCalibration_Tgl                               = 0x3154,
            RadioFrequencyBiasControl_Tgl                                   = 0x3158,
            RadioEnable_Tgl                                                 = 0x315C,
            RadioFrequencyPathEnable0_Tgl                                   = 0x3160,
            RadioFrequencyPathEnable1_Tgl                                   = 0x3164,
            Rx_Tgl                                                          = 0x3168,
            Tx_Tgl                                                          = 0x316C,
            SyDebug_Tgl                                                     = 0x3170,
            SyTrim0_Tgl                                                     = 0x3174,
            SyTrim1_Tgl                                                     = 0x3178,
            SyCalibration_Tgl                                               = 0x317C,
            SyEnable_Tgl                                                    = 0x3180,
            SyloEnable_Tgl                                                  = 0x3184,
            SymmControl_Tgl                                                 = 0x3188,
            DigitalClockRetimeControl_Tgl                                   = 0x3190,
            DigitalClockRetimeStatus_Tgl                                    = 0x3194,
            XoRetimeControl_Tgl                                             = 0x3198,
            XoRetimeStatus_Tgl                                              = 0x319C,
            AutomaticGainControlOverwrite0_Tgl                              = 0x31A0,
            AutomaticGainControlOverwrite1_Tgl                              = 0x31A4,
            AutomaticGainControlOverwrite2_Tgl                              = 0x31A8,
            Spare_Tgl                                                       = 0x31C4,
            PowerAmplifierControl_Tgl                                       = 0x31C8,
            FeNotch0_Tgl                                                    = 0x31CC,
            FeNotch1_Tgl                                                    = 0x31D0,
            Scratch0_Tgl                                                    = 0x33E0,
            Scratch1_Tgl                                                    = 0x33E4,
            Scratch2_Tgl                                                    = 0x33E8,
            Scratch3_Tgl                                                    = 0x33EC,
            Scratch4_Tgl                                                    = 0x33F0,
            Scratch5_Tgl                                                    = 0x33F4,
            Scratch6_Tgl                                                    = 0x33F8,
            Scratch7_Tgl                                                    = 0x33FC,
            ThermisterControl_Tgl                                           = 0x37E8,
            DiagaAlternateEnable_Tgl                                        = 0x37EC,
            DiagaAlternateRfBlocksAndTpSelect_Tgl                           = 0x37F0,
            DiagaAlternateBridgeControl_Tgl                                 = 0x37F4,
            RadioFrequencyLock0_Tgl                                         = 0x37F8,
            RadioFrequencyLock1_Tgl                                         = 0x37FC,
        }

        [RegistersDescription("synth", "synth_ns")]
        private enum SynthesizerRegisters : long
        {
            IpVersion                                                       = 0x0000,
            Enable                                                          = 0x0004,
            Status                                                          = 0x0008,
            Command                                                         = 0x000C,
            Control                                                         = 0x0010,
            CalibrationControl                                              = 0x0014,
            VCDACControl                                                    = 0x002C,
            Frequency                                                       = 0x0034,
            IntermediateFrequency                                           = 0x0038,
            FrequencyDivisionControl                                        = 0x003C,
            ChannelControl                                                  = 0x0040,
            ChannelSpacing                                                  = 0x0044,
            CalibrationOffset                                               = 0x0048,
            VoltageControlledOscillatorFrequencyTuning                      = 0x004C,
            VoltageControlledOscillatorFrequencyRangeControl                = 0x0054,
            VoltageControlledOscillatorGainCalibration                      = 0x0058,
            ChargePumpDigitalToAnalogConverterControl                       = 0x0068,
            CapacitorArrayCalibrationCycleCount                             = 0x006C,
            VoltageControlledOscillatorForceCalibrationCycleCount           = 0x0070,
            InterruptFlags                                                  = 0x0078,
            InterruptEnable                                                 = 0x0084,
            LoCounterControl                                                = 0x0088,
            LoCounterStatus                                                 = 0x008C,
            LoCounterTargetValue                                            = 0x0090,
            MmdDenominatorInitialValue                                      = 0x0094,
            ChargePumpDigitalToAnalogConverterInitialValue                  = 0x0098,
            LowPassFilterCalModeControl                                     = 0x009C,
            LowPassFilterRxModeControl1                                     = 0x00A0,
            LowPassFilterTxModeControl1                                     = 0x00A4,
            LowPassFilterRxModeControl2                                     = 0x00A8,
            LowPassFilterTxModeControl2                                     = 0x00AC,
            DsmRxModeControl                                                = 0x00B0,
            DsmTxModeControl                                                = 0x00B4,
            SequencerInterruptFlags                                         = 0x00B8,
            SequencerInterruptEnable                                        = 0x00BC,

            // Set registers
            IpVersion_Set                                                   = 0x1000,
            Enable_Set                                                      = 0x1004,
            Status_Set                                                      = 0x1008,
            Command_Set                                                     = 0x100C,
            Control_Set                                                     = 0x1010,
            CalibrationControl_Set                                          = 0x1014,
            VCDACControl_Set                                                = 0x102C,
            Frequency_Set                                                   = 0x1034,
            IntermediateFrequency_Set                                       = 0x1038,
            FrequencyDivisionControl_Set                                    = 0x103C,
            ChannelControl_Set                                              = 0x1040,
            ChannelSpacing_Set                                              = 0x1044,
            CalibrationOffset_Set                                           = 0x1048,
            VoltageControlledOscillatorFrequencyTuning_Set                  = 0x104C,
            VoltageControlledOscillatorFrequencyRangeControl_Set            = 0x1054,
            VoltageControlledOscillatorGainCalibration_Set                  = 0x1058,
            ChargePumpDigitalToAnalogConverterControl_Set                   = 0x1068,
            CapacitorArrayCalibrationCycleCount_Set                         = 0x106C,
            VoltageControlledOscillatorForceCalibrationCycleCount_Set       = 0x1070,
            InterruptFlags_Set                                              = 0x1078,
            InterruptEnable_Set                                             = 0x1084,
            LoCounterControl_Set                                            = 0x1088,
            LoCounterStatus_Set                                             = 0x108C,
            LoCounterTargetValue_Set                                        = 0x1090,
            MmdDenominatorInitialValue_Set                                  = 0x1094,
            ChargePumpDigitalToAnalogConverterInitialValue_Set              = 0x1098,
            LowPassFilterCalModeControl_Set                                 = 0x109C,
            LowPassFilterRxModeControl1_Set                                 = 0x10A0,
            LowPassFilterTxModeControl1_Set                                 = 0x10A4,
            LowPassFilterRxModeControl2_Set                                 = 0x10A8,
            LowPassFilterTxModeControl2_Set                                 = 0x10AC,
            DsmRxModeControl_Set                                            = 0x10B0,
            DsmTxModeControl_Set                                            = 0x10B4,
            SequencerInterruptFlags_Set                                     = 0x10B8,
            SequencerInterruptEnable_Set                                    = 0x10BC,

            // Clear registers
            IpVersion_Clr                                                   = 0x2000,
            Enable_Clr                                                      = 0x2004,
            Status_Clr                                                      = 0x2008,
            Command_Clr                                                     = 0x200C,
            Control_Clr                                                     = 0x2010,
            CalibrationControl_Clr                                          = 0x2014,
            VCDACControl_Clr                                                = 0x202C,
            Frequency_Clr                                                   = 0x2034,
            IntermediateFrequency_Clr                                       = 0x2038,
            FrequencyDivisionControl_Clr                                    = 0x203C,
            ChannelControl_Clr                                              = 0x2040,
            ChannelSpacing_Clr                                              = 0x2044,
            CalibrationOffset_Clr                                           = 0x2048,
            VoltageControlledOscillatorFrequencyTuning_Clr                  = 0x204C,
            VoltageControlledOscillatorFrequencyRangeControl_Clr            = 0x2054,
            VoltageControlledOscillatorGainCalibration_Clr                  = 0x2058,
            ChargePumpDigitalToAnalogConverterControl_Clr                   = 0x2068,
            CapacitorArrayCalibrationCycleCount_Clr                         = 0x206C,
            VoltageControlledOscillatorForceCalibrationCycleCount_Clr       = 0x2070,
            InterruptFlags_Clr                                              = 0x2078,
            InterruptEnable_Clr                                             = 0x2084,
            LoCounterControl_Clr                                            = 0x2088,
            LoCounterStatus_Clr                                             = 0x208C,
            LoCounterTargetValue_Clr                                        = 0x2090,
            MmdDenominatorInitialValue_Clr                                  = 0x2094,
            ChargePumpDigitalToAnalogConverterInitialValue_Clr              = 0x2098,
            LowPassFilterCalModeControl_Clr                                 = 0x209C,
            LowPassFilterRxModeControl1_Clr                                 = 0x20A0,
            LowPassFilterTxModeControl1_Clr                                 = 0x20A4,
            LowPassFilterRxModeControl2_Clr                                 = 0x20A8,
            LowPassFilterTxModeControl2_Clr                                 = 0x20AC,
            DsmRxModeControl_Clr                                            = 0x20B0,
            DsmTxModeControl_Clr                                            = 0x20B4,
            SequencerInterruptFlags_Clr                                     = 0x20B8,
            SequencerInterruptEnable_Clr                                    = 0x20BC,

            // Toggle registers
            IpVersion_Tgl                                                   = 0x3000,
            Enable_Tgl                                                      = 0x3004,
            Status_Tgl                                                      = 0x3008,
            Command_Tgl                                                     = 0x300C,
            Control_Tgl                                                     = 0x3010,
            CalibrationControl_Tgl                                          = 0x3014,
            VCDACControl_Tgl                                                = 0x302C,
            Frequency_Tgl                                                   = 0x3034,
            IntermediateFrequency_Tgl                                       = 0x3038,
            FrequencyDivisionControl_Tgl                                    = 0x303C,
            ChannelControl_Tgl                                              = 0x3040,
            ChannelSpacing_Tgl                                              = 0x3044,
            CalibrationOffset_Tgl                                           = 0x3048,
            VoltageControlledOscillatorFrequencyTuning_Tgl                  = 0x304C,
            VoltageControlledOscillatorFrequencyRangeControl_Tgl            = 0x3054,
            VoltageControlledOscillatorGainCalibration_Tgl                  = 0x3058,
            ChargePumpDigitalToAnalogConverterControl_Tgl                   = 0x3068,
            CapacitorArrayCalibrationCycleCount_Tgl                         = 0x306C,
            VoltageControlledOscillatorForceCalibrationCycleCount_Tgl       = 0x3070,
            InterruptFlags_Tgl                                              = 0x3078,
            InterruptEnable_Tgl                                             = 0x3084,
            LoCounterControl_Tgl                                            = 0x3088,
            LoCounterStatus_Tgl                                             = 0x308C,
            LoCounterTargetValue_Tgl                                        = 0x3090,
            MmdDenominatorInitialValue_Tgl                                  = 0x3094,
            ChargePumpDigitalToAnalogConverterInitialValue_Tgl              = 0x3098,
            LowPassFilterCalModeControl_Tgl                                 = 0x309C,
            LowPassFilterRxModeControl1_Tgl                                 = 0x30A0,
            LowPassFilterTxModeControl1_Tgl                                 = 0x30A4,
            LowPassFilterRxModeControl2_Tgl                                 = 0x30A8,
            LowPassFilterTxModeControl2_Tgl                                 = 0x30AC,
            DsmRxModeControl_Tgl                                            = 0x30B0,
            DsmTxModeControl_Tgl                                            = 0x30B4,
            SequencerInterruptFlags_Tgl                                     = 0x30B8,
            SequencerInterruptEnable_Tgl                                    = 0x30BC,
        }

        [RegistersDescription("hostmailbox", "hostmailbox_ns")]
        private enum HostMailboxRegisters : long
        {
            MessagePointer0                                                 = 0x0000,
            MessagePointer1                                                 = 0x0004,
            MessagePointer2                                                 = 0x0008,
            MessagePointer3                                                 = 0x000C,
            InterruptFlags                                                  = 0x0040,
            InterruptEnable                                                 = 0x0044,

            // Set Registers
            MessagePointer0_Set                                             = 0x1000,
            MessagePointer1_Set                                             = 0x1004,
            MessagePointer2_Set                                             = 0x1008,
            MessagePointer3_Set                                             = 0x100C,
            InterruptFlags_Set                                              = 0x1040,
            InterruptEnable_Set                                             = 0x1044,

            // Clear Registers
            MessagePointer0_Clr                                             = 0x2000,
            MessagePointer1_Clr                                             = 0x2004,
            MessagePointer2_Clr                                             = 0x2008,
            MessagePointer3_Clr                                             = 0x200C,
            InterruptFlags_Clr                                              = 0x2040,
            InterruptEnable_Clr                                             = 0x2044,

            // Toggle Registers
            MessagePointer0_Tgl                                             = 0x3000,
            MessagePointer1_Tgl                                             = 0x3004,
            MessagePointer2_Tgl                                             = 0x3008,
            MessagePointer3_Tgl                                             = 0x300C,
            InterruptFlags_Tgl                                              = 0x3040,
            InterruptEnable_Tgl                                             = 0x3044,
        }

        [RegistersDescription("rfmailbox", "rfmailbox_ns")]
        private enum RadioMailboxRegisters : long
        {
            MessagePointer0                                                 = 0x0000,
            MessagePointer1                                                 = 0x0004,
            MessagePointer2                                                 = 0x0008,
            MessagePointer3                                                 = 0x000C,
            InterruptFlags                                                  = 0x0040,
            InterruptEnable                                                 = 0x0044,

            // Set Registers
            MessagePointer0_Set                                             = 0x1000,
            MessagePointer1_Set                                             = 0x1004,
            MessagePointer2_Set                                             = 0x1008,
            MessagePointer3_Set                                             = 0x100C,
            InterruptFlags_Set                                              = 0x1040,
            InterruptEnable_Set                                             = 0x1044,

            // Clear Registers
            MessagePointer0_Clr                                             = 0x2000,
            MessagePointer1_Clr                                             = 0x2004,
            MessagePointer2_Clr                                             = 0x2008,
            MessagePointer3_Clr                                             = 0x200C,
            InterruptFlags_Clr                                              = 0x2040,
            InterruptEnable_Clr                                             = 0x2044,

            // Toggle Registers
            MessagePointer0_Tgl                                             = 0x3000,
            MessagePointer1_Tgl                                             = 0x3004,
            MessagePointer2_Tgl                                             = 0x3008,
            MessagePointer3_Tgl                                             = 0x300C,
            InterruptFlags_Tgl                                              = 0x3040,
            InterruptEnable_Tgl                                             = 0x3044,
        }
    }
}
