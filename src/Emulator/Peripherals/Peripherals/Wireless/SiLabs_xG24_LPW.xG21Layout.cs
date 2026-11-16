// Generated from the EFR32MG21 device headers; do not edit by hand.
// EFR32MG21 register layouts for the radio blocks the xG24 model lays out differently.
// Consumed by SiLabs_xG24_LPW's bus read/write path when registerLayout is "xg21":
//   *ToModel : MG21 bus offset -> the model's (xG24) register offset, same register;
//   *Only    : MG21 registers the model does not implement -> plain 32-bit storage.
// An MG21 offset in neither set falls through to the model's own decode (unhandled).
using System.Collections.Generic;

namespace Antmicro.Renode.Peripherals.Wireless
{
    public partial class SiLabs_xG24_LPW
    {
        private static readonly Dictionary<long, long> xG21BufcToModel = new Dictionary<long, long>
        {
            {0x0000, 0x0000},  // IPVERSION -> IpVersion
            {0x0004, 0x0004},  // EN -> Enable
            {0x0008, 0x000C},  // BUF0_CTRL -> Buffer0Control
            {0x000C, 0x0010},  // BUF0_ADDR -> Buffer0Address
            {0x0010, 0x0014},  // BUF0_WRITEOFFSET -> Buffer0WriteOffset
            {0x0014, 0x0018},  // BUF0_READOFFSET -> Buffer0ReadOffset
            {0x001C, 0x0020},  // BUF0_READDATA -> Buffer0ReadData
            {0x0020, 0x0024},  // BUF0_WRITEDATA -> Buffer0WriteData
            {0x0024, 0x0028},  // BUF0_XWRITE -> Buffer0XorWrite
            {0x0028, 0x002C},  // BUF0_STATUS -> Buffer0Status
            {0x002C, 0x0030},  // BUF0_THRESHOLDCTRL -> Buffer0ThresholdControl
            {0x0030, 0x0034},  // BUF0_CMD -> Buffer0Command
            {0x0034, 0x0038},  // BUF0_FIFOASYNC -> Buffer0FifoAsync
            {0x0038, 0x004C},  // BUF1_CTRL -> Buffer1Control
            {0x003C, 0x0050},  // BUF1_ADDR -> Buffer1Address
            {0x0040, 0x0054},  // BUF1_WRITEOFFSET -> Buffer1WriteOffset
            {0x0044, 0x0058},  // BUF1_READOFFSET -> Buffer1ReadOffset
            {0x004C, 0x0060},  // BUF1_READDATA -> Buffer1ReadData
            {0x0050, 0x0064},  // BUF1_WRITEDATA -> Buffer1WriteData
            {0x0054, 0x0068},  // BUF1_XWRITE -> Buffer1XorWrite
            {0x0058, 0x006C},  // BUF1_STATUS -> Buffer1Status
            {0x005C, 0x0070},  // BUF1_THRESHOLDCTRL -> Buffer1ThresholdControl
            {0x0060, 0x0074},  // BUF1_CMD -> Buffer1Command
            {0x0064, 0x0078},  // BUF1_FIFOASYNC -> Buffer1FifoAsync
            {0x0068, 0x008C},  // BUF2_CTRL -> Buffer2Control
            {0x006C, 0x0090},  // BUF2_ADDR -> Buffer2Address
            {0x0070, 0x0094},  // BUF2_WRITEOFFSET -> Buffer2WriteOffset
            {0x0074, 0x0098},  // BUF2_READOFFSET -> Buffer2ReadOffset
            {0x007C, 0x00A0},  // BUF2_READDATA -> Buffer2ReadData
            {0x0080, 0x00A4},  // BUF2_WRITEDATA -> Buffer2WriteData
            {0x0084, 0x00A8},  // BUF2_XWRITE -> Buffer2XorWrite
            {0x0088, 0x00AC},  // BUF2_STATUS -> Buffer2Status
            {0x008C, 0x00B0},  // BUF2_THRESHOLDCTRL -> Buffer2ThresholdControl
            {0x0090, 0x00B4},  // BUF2_CMD -> Buffer2Command
            {0x0094, 0x00B8},  // BUF2_FIFOASYNC -> Buffer2FifoAsync
            {0x0098, 0x00CC},  // BUF3_CTRL -> Buffer3Control
            {0x009C, 0x00D0},  // BUF3_ADDR -> Buffer3Address
            {0x00A0, 0x00D4},  // BUF3_WRITEOFFSET -> Buffer3WriteOffset
            {0x00A4, 0x00D8},  // BUF3_READOFFSET -> Buffer3ReadOffset
            {0x00AC, 0x00E0},  // BUF3_READDATA -> Buffer3ReadData
            {0x00B0, 0x00E4},  // BUF3_WRITEDATA -> Buffer3WriteData
            {0x00B4, 0x00E8},  // BUF3_XWRITE -> Buffer3XorWrite
            {0x00B8, 0x00EC},  // BUF3_STATUS -> Buffer3Status
            {0x00BC, 0x00F0},  // BUF3_THRESHOLDCTRL -> Buffer3ThresholdControl
            {0x00C0, 0x00F4},  // BUF3_CMD -> Buffer3Command
            {0x00C4, 0x00F8},  // BUF3_FIFOASYNC -> Buffer3FifoAsync
            {0x00E0, 0x0114},  // IF -> InterruptFlags
            {0x00EC, 0x0118},  // IEN -> InterruptEnable
        };
        private static readonly HashSet<long> xG21BufcOnly = new HashSet<long>
        {
            0x00F0,
        };

        private static readonly Dictionary<long, long> xG21ModemToModel = new Dictionary<long, long>
        {
            {0x0000, 0x0000},  // IPVERSION -> IpVersion
            {0x0004, 0x0004},  // EN -> Enable
            {0x0008, 0x0018},  // STATUS -> Status
            {0x0020, 0x004C},  // CTRL0 -> Control0
            {0x0024, 0x0050},  // CTRL1 -> Control1
            {0x0028, 0x0054},  // CTRL2 -> Control2
            {0x002C, 0x0058},  // CTRL3 -> Control3
            {0x0030, 0x005C},  // CTRL4 -> Control4
            {0x0034, 0x0060},  // CTRL5 -> Control5
            {0x0038, 0x0064},  // CTRL6 -> Control6
            {0x0064, 0x0074},  // PRE (fields 100%) -> Preamble
            {0x0068, 0x0078},  // SYNC0 -> SyncWord0
            {0x006C, 0x007C},  // SYNC1 -> SyncWord1
            {0x0080, 0x0080},  // TIMING -> Timing
            {0x00C0, 0x00D8},  // RAMPCTRL (fields 60%) -> RampingControl
            {0x0100, 0x0140},  // VITERBIDEMOD -> ViterbiDemodulator
            {0x0208, 0x0008},  // IF -> InterruptFlags
            {0x020C, 0x000C},  // IEN -> InterruptEnable
            {0x0218, 0x0198},  // CMD -> Command
            {0x0220, 0x001C},  // STATUS2 -> Status2
            {0x0224, 0x0020},  // STATUS3 -> Status3
            {0x0238, 0x01A4},  // SYNCPROPERTIES -> SyncWordProperties
        };
        private static readonly HashSet<long> xG21ModemOnly = new HashSet<long>
        {
            0x000C, 0x0010, 0x0014, 0x0018, 0x001C, 0x0058, 0x005C, 0x0060, 0x0084, 0x0088, 0x008C, 0x0090, 0x0094, 0x0098, 0x009C, 0x00A0, 0x00A4, 0x00A8, 0x00AC, 0x00C4, 0x00E0, 0x00E4, 0x00E8, 0x00EC, 0x00F0, 0x00F4, 0x00F8, 0x00FC, 0x0104, 0x010C, 0x0110, 0x0114, 0x0118, 0x0124, 0x0128, 0x012C, 0x0130, 0x0134, 0x0138, 0x013C, 0x0140, 0x0144, 0x0148, 0x014C, 0x0150, 0x0154, 0x0158, 0x015C, 0x0160, 0x0164, 0x0168, 0x016C, 0x0170, 0x021C, 0x0228, 0x022C, 0x0230, 0x0234, 0x023C, 0x0240, 0x0250, 0x0254,
        };

        private static readonly Dictionary<long, long> xG21RacToModel = new Dictionary<long, long>
        {
            {0x0000, 0x0000},  // IPVERSION -> IpVersion
            {0x0004, 0x0004},  // EN -> Enable
            {0x0008, 0x0008},  // RXENSRCEN -> RXENSourceEnable
            {0x000C, 0x000C},  // STATUS -> Status
            {0x0010, 0x0010},  // CMD -> Command
            {0x0014, 0x0014},  // CTRL -> Control
            {0x0018, 0x0018},  // FORCESTATE -> ForceStateTransition
            {0x001C, 0x001C},  // IF -> InterruptFlags
            {0x0020, 0x0020},  // IEN -> InterruptEnable
            {0x0024, 0x0024},  // TESTCTRL -> TestControl
            {0x005C, 0x0034},  // STIMER -> SequencerTimerValue
            {0x0060, 0x0038},  // STIMERCOMP -> SequencerTimerCompareValue
            {0x0068, 0x003C},  // SEQCTRL (fields 50%) -> SequencerControl
            {0x006C, 0x0040},  // PRESC -> SequencerPrescaler
            {0x0070, 0x0044},  // SR0 -> Storage0
            {0x0074, 0x0048},  // SR1 -> Storage1
            {0x0078, 0x004C},  // SR2 -> Storage2
            {0x007C, 0x0050},  // SR3 -> Storage3
            {0x009C, 0x00B0},  // STATUS2 -> Status2
            {0x00A4, 0x00B8},  // PAENCTRL -> PowerAmplifierEnableControl
            {0x0124, 0x015C},  // RADIOEN -> RadioEnable
            {0x0130, 0x0168},  // RX -> Rx
            {0x0134, 0x016C},  // TX -> Tx
            {0x013C, 0x0174},  // SYTRIM0 -> SyTrim0
            {0x0140, 0x0178},  // SYTRIM1 -> SyTrim1
            {0x0148, 0x0180},  // SYEN -> SyEnable
            {0x014C, 0x0184},  // SYLOEN -> SyloEnable
            {0x0164, 0x0198},  // XORETIMECTRL -> XoRetimeControl
            {0x0168, 0x019C},  // XORETIMESTATUS -> XoRetimeStatus
            {0x03E0, 0x03E0},  // SCRATCH0 -> Scratch0
            {0x03E4, 0x03E4},  // SCRATCH1 -> Scratch1
            {0x03E8, 0x03E8},  // SCRATCH2 -> Scratch2
            {0x03EC, 0x03EC},  // SCRATCH3 -> Scratch3
            {0x03F0, 0x03F0},  // SCRATCH4 -> Scratch4
            {0x03F4, 0x03F4},  // SCRATCH5 -> Scratch5
            {0x03F8, 0x03F8},  // SCRATCH6 -> Scratch6
            {0x03FC, 0x03FC},  // SCRATCH7 -> Scratch7
        };
        private static readonly HashSet<long> xG21RacOnly = new HashSet<long>
        {
            0x0028, 0x002C, 0x0030, 0x0034, 0x0038, 0x003C, 0x0040, 0x0044, 0x0048, 0x004C, 0x0050, 0x0054, 0x0058, 0x0064, 0x0084, 0x0088, 0x008C, 0x0094, 0x00A0, 0x00A8, 0x00AC, 0x00B0, 0x00B4, 0x00B8, 0x00BC, 0x00C0, 0x00C4, 0x00C8, 0x00CC, 0x00D4, 0x00D8, 0x00DC, 0x00E4, 0x00E8, 0x00EC, 0x00F0, 0x00F4, 0x00F8, 0x00FC, 0x0100, 0x0104, 0x0108, 0x0110, 0x0114, 0x0118, 0x011C, 0x0120, 0x0128, 0x012C, 0x0144, 0x0158, 0x015C, 0x0160, 0x016C, 0x0174,
        };

    }
}
