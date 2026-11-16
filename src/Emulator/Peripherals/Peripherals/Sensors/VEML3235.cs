//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.I2C;

namespace Antmicro.Renode.Peripherals.Sensors
{
    // Vishay VEML3235 ambient light sensor, I2C, address 0x10. Modeled for a driver that does
    // the following:
    //
    //     probe the address, read DEVICE_ID (0x09) and require its low byte to be 0x35,
    //     write CONFIG (0x00) with gain / digital gain / integration time / shutdown,
    //     read ALS (0x05) and WHITE (0x04) as 16-bit counts, and compute lux from the counts and
    //     the resolution its configuration implies.
    //
    // Registers are 16 bits, LOW byte first on the wire. A command byte selects the register; a read
    // then returns that register's two bytes.
    //
    // What a test drives is the light: `sysbus.i2c0.als AlsCounts 1234` sets the raw ALS count the
    // firmware converts, `WhiteCounts` the white channel. Counts rather than lux, deliberately:
    // lux depends on the gain and integration time the FIRMWARE chose, and the firmware's own
    // arithmetic is what a test should exercise. The datasheet's typical resolution is
    // 0.0034 lux/count at gain 4 / DG 1 / 800 ms, so
    // AlsCounts 29412 is about 100 lux there. Nothing here converts light to counts over time:
    // the count is what it was set to, immediately, whatever integration time is configured.
    //
    // Not modeled: the integration period (readings are instantaneous), the interrupt output
    // (the part has none on this variant's pinout), and saturation behavior beyond the 16-bit
    // ceiling.
    public class VEML3235 : II2CPeripheral
    {
        public VEML3235()
        {
            outputBuffer = new Queue<byte>();
            Reset();
        }

        public void Reset()
        {
            pointer = 0;
            configuration = ConfigurationResetValue;
            AlsCounts = DefaultCounts;
            WhiteCounts = DefaultCounts;
            outputBuffer.Clear();
        }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                // An address probe: the HAL writes nothing and only wants the ACK.
                return;
            }
            pointer = data[0];
            if(data.Length >= 3)
            {
                var value = (ushort)(data[1] | (data[2] << 8));
                if(pointer == (byte)Registers.Configuration)
                {
                    configuration = value;
                    this.Log(LogLevel.Debug, "CONFIG = 0x{0:X4} (gain code {1}, DG {2}, IT code {3}, shutdown {4})",
                             configuration, (configuration >> 11) & 0x3, (configuration >> 13) & 0x1,
                             (configuration >> 4) & 0x7, (configuration & 0x1) != 0 || (configuration & 0x8000) != 0);
                }
                else
                {
                    this.Log(LogLevel.Warning, "Write of 0x{0:X4} to read-only register 0x{1:X2} ignored", value, pointer);
                }
            }
            else if(data.Length == 2)
            {
                this.Log(LogLevel.Warning, "Short write to register 0x{0:X2}; registers are 16 bits", pointer);
            }
            outputBuffer.Clear();
        }

        public byte[] Read(int count = 1)
        {
            if(outputBuffer.Count == 0)
            {
                ushort word;
                switch((Registers)pointer)
                {
                    case Registers.Configuration:
                        word = configuration;
                        break;
                    case Registers.WhiteData:
                        word = WhiteCounts;
                        break;
                    case Registers.AlsData:
                        word = AlsCounts;
                        break;
                    case Registers.DeviceId:
                        word = DeviceId;
                        break;
                    default:
                        this.Log(LogLevel.Warning, "Read from unknown register 0x{0:X2}; answering zeros", pointer);
                        word = 0;
                        break;
                }
                outputBuffer.Enqueue((byte)(word & 0xFF));
                outputBuffer.Enqueue((byte)(word >> 8));
            }
            var result = new byte[count];
            for(var i = 0; i < count; ++i)
            {
                result[i] = outputBuffer.Count > 0 ? outputBuffer.Dequeue() : (byte)0;
            }
            this.Log(LogLevel.Noisy, "Read 0x{0:X2} -> [{1}]", pointer, string.Join(", ", result.Select(x => $"0x{x:X2}")));
            return result;
        }

        public void FinishTransmission()
        {
            outputBuffer.Clear();
        }

        // Raw 16-bit counts the firmware reads and converts with its own resolution.
        public ushort AlsCounts { get; set; }
        public ushort WhiteCounts { get; set; }

        // The CONFIG word as the firmware last wrote it, for a test that wants to see the gain and
        // integration time the firmware chose.
        public ushort Configuration => configuration;

        private byte pointer;
        private ushort configuration;
        private readonly Queue<byte> outputBuffer;

        // Datasheet: the low data byte is the part number 0x35; the high byte carries the slave
        // address option and version, 0x01 on the SL variant.
        private const ushort DeviceId = 0x0135;
        // Reserved bit 8 of the high byte reads 1 (datasheet table 1); shutdown bits clear.
        private const ushort ConfigurationResetValue = 0x0100;
        // Roughly an office at gain 4 / DG 1 / 800 ms (0.0034 lux/count):
        // a value that is neither dark nor saturated, so an untouched setup reads plausible lux.
        private const ushort DefaultCounts = 29412;

        private enum Registers : byte
        {
            Configuration = 0x00,
            WhiteData = 0x04,
            AlsData = 0x05,
            DeviceId = 0x09,
        }
    }
}
