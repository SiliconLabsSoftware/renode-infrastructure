//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.I2C;
using Antmicro.Renode.Peripherals.Sensor;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Sensors
{
    // TI HDC1080 temperature and humidity sensor, I2C, fixed address 0x40.
    //
    // Modeled for the datasheet's "sequential acquisition":
    //
    //     write [0x02, hi, lo]   configure; bit 12 (MODE) selects temperature+humidity together
    //     write [0x00]           point at the temperature register, which TRIGGERS a conversion
    //     read  4 bytes          temperature MSB, LSB, humidity MSB, LSB
    //
    // Conversions are the datasheet's:
    //     temperature_C = (raw / 65536) * 165 - 40
    //     humidity_RH   = (raw / 65536) * 100
    //
    // Drive it from a test or a platform in the units a person thinks in:
    //     sysbus.i2c0.hdc1080 Temperature 21.5
    //     sysbus.i2c0.hdc1080 Humidity 47
    //
    // NOT modeled, and each would change what a test sees:
    //  * CONVERSION TIME. The real part takes up to ~6.5 ms per channel and NACKs a read issued
    //    before the result is ready; here it is ready immediately. Firmware that polls by
    //    retrying a NACKed read would spin on hardware and sail through here, so timing of the
    //    acquisition belongs on a bench.
    //  * The HEATER (config bit 13) is stored and read back, but nothing warms up - there is no
    //    thermal model behind it, so enabling it does not move Temperature.
    //  * BTST (battery status) always reads 0, i.e. "supply above 2.8 V".
    //  * The 40-bit serial number reads as zeros.
    //
    // Resolution IS modeled, because it changes the value the firmware computes: an 11-bit
    // temperature or 8-bit humidity reading has its unused low bits cleared, exactly as the part
    // returns them.
    public class HDC1080 : II2CPeripheral, ITemperatureSensor, IHumiditySensor
    {
        public HDC1080()
        {
            outputBuffer = new Queue<byte>();
            Reset();
        }

        public void Reset()
        {
            pointer = (byte)Registers.Temperature;
            configuration = DefaultConfiguration;
            Temperature = DefaultTemperature;
            Humidity = DefaultHumidity;
            outputBuffer.Clear();
        }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                this.Log(LogLevel.Warning, "Received an empty write; ignoring it");
                return;
            }

            pointer = data[0];
            this.Log(LogLevel.Noisy, "Pointer set to 0x{0:X2} by a {1}-byte write", pointer, data.Length);

            if(data.Length > 1)
            {
                if(pointer != (byte)Registers.Configuration)
                {
                    this.Log(LogLevel.Warning,
                             "Write of {0} bytes to register 0x{1:X2}, which is read-only; only the pointer was taken",
                             data.Length, pointer);
                    return;
                }
                if(data.Length < 3)
                {
                    this.Log(LogLevel.Warning, "Short configuration write ({0} bytes); it takes two data bytes", data.Length);
                    return;
                }
                WriteConfiguration((ushort)((data[1] << 8) | data[2]));
                return;
            }

            // A bare pointer write to the temperature register is the acquisition trigger. In
            // MODE=1 the part converts temperature AND humidity, which is what makes the single
            // four-byte read below legal.
            if(pointer == (byte)Registers.Temperature)
            {
                StartConversion();
            }
        }

        public byte[] Read(int count = 1)
        {
            // The queue is filled at the point the transfer is set up rather than at the trigger,
            // so a read of the measurement registers without a preceding trigger still answers
            // with the current values instead of stalling the bus.
            if(outputBuffer.Count == 0)
            {
                FillOutputBuffer();
            }

            var result = new List<byte>();
            for(var i = 0; i < count && outputBuffer.Count > 0; ++i)
            {
                result.Add(outputBuffer.Dequeue());
            }
            if(result.Count < count)
            {
                this.Log(LogLevel.Warning, "Read of {0} bytes from register 0x{1:X2}, which has only {2}; padding with zeros",
                         count, pointer, result.Count);
                while(result.Count < count)
                {
                    result.Add(0);
                }
            }
            this.Log(LogLevel.Noisy, "Read 0x{0:X2} -> [{1}]", pointer, string.Join(", ", result.Select(x => $"0x{x:X2}")));
            return result.ToArray();
        }

        public void FinishTransmission()
        {
            outputBuffer.Clear();
        }

        public decimal Temperature
        {
            get => RawToTemperature(temperatureRaw);
            set
            {
                if(value < MinTemperature || value > MaxTemperature)
                {
                    throw new RecoverableException($"The temperature must be between {MinTemperature} and {MaxTemperature} degrees Celsius.");
                }
                // Round-trip through the raw encoding so what a test reads back is what the
                // firmware will compute, quantization included.
                temperatureRaw = TemperatureToRaw(value);
            }
        }

        public decimal Humidity
        {
            get => RawToHumidity(humidityRaw);
            set
            {
                if(value < MinHumidity || value > MaxHumidity)
                {
                    throw new RecoverableException($"The humidity must be between {MinHumidity} and {MaxHumidity} percent.");
                }
                humidityRaw = HumidityToRaw(value);
            }
        }

        private void WriteConfiguration(ushort value)
        {
            if((value & ResetBit) != 0)
            {
                this.Log(LogLevel.Debug, "Software reset requested");
                Reset();
                return;
            }
            configuration = (ushort)(value & WritableConfigurationMask);
            this.Log(LogLevel.Debug, "Configuration set to 0x{0:X4} (mode={1}, tres={2}, hres={3}, heater={4})",
                     configuration, BothChannels ? "temperature+humidity" : "single",
                     TemperatureResolutionBits, HumidityResolutionBits, (configuration & HeaterBit) != 0);
        }

        private void StartConversion()
        {
            // Nothing takes time here; the values a test set are simply made available. The
            // acquisition mode still matters, because it decides how many bytes the part returns.
            outputBuffer.Clear();
            FillOutputBuffer();
        }

        private void FillOutputBuffer()
        {
            outputBuffer.Clear();
            switch((Registers)pointer)
            {
                case Registers.Temperature:
                    EnqueueWord(QuantiseTemperature(temperatureRaw));
                    if(BothChannels)
                    {
                        EnqueueWord(QuantiseHumidity(humidityRaw));
                    }
                    break;
                case Registers.Humidity:
                    EnqueueWord(QuantiseHumidity(humidityRaw));
                    break;
                case Registers.Configuration:
                    EnqueueWord(configuration);
                    break;
                case Registers.ManufacturerId:
                    EnqueueWord(ManufacturerId);
                    break;
                case Registers.DeviceId:
                    EnqueueWord(DeviceId);
                    break;
                case Registers.SerialId0:
                case Registers.SerialId1:
                case Registers.SerialId2:
                    EnqueueWord(0);
                    break;
                default:
                    this.Log(LogLevel.Warning, "Read from unknown register 0x{0:X2}; answering with zeros", pointer);
                    EnqueueWord(0);
                    break;
            }
        }

        private void EnqueueWord(ushort value)
        {
            outputBuffer.Enqueue((byte)(value >> 8));
            outputBuffer.Enqueue((byte)(value & 0xFF));
        }

        // The part returns fewer significant bits at lower resolutions, with the unused low bits
        // read as zero. Modeled because the firmware divides the raw word, so the quantization
        // shows up in the value it prints.
        private ushort QuantiseTemperature(ushort raw) => Mask(raw, TemperatureResolutionBits);

        private ushort QuantiseHumidity(ushort raw) => Mask(raw, HumidityResolutionBits);

        private static ushort Mask(ushort raw, int bits) => (ushort)(raw & (0xFFFF << (16 - bits)));

        private static ushort TemperatureToRaw(decimal celsius)
            => (ushort)Math.Round((double)((celsius + TemperatureOffset) / TemperatureSpan * RawSpan));

        private static decimal RawToTemperature(ushort raw)
            => (decimal)raw / RawSpan * TemperatureSpan - TemperatureOffset;

        private static ushort HumidityToRaw(decimal percent)
            => (ushort)Math.Round((double)(percent / HumiditySpan * RawSpan));

        private static decimal RawToHumidity(ushort raw)
            => (decimal)raw / RawSpan * HumiditySpan;

        private bool BothChannels => (configuration & ModeBit) != 0;

        private int TemperatureResolutionBits => (configuration & TemperatureResolutionBit) != 0 ? 11 : 14;

        private int HumidityResolutionBits
        {
            get
            {
                switch((configuration >> HumidityResolutionShift) & 0x3)
                {
                    case 0:
                        return 14;
                    case 1:
                        return 11;
                    case 2:
                        return 8;
                    default:
                        this.Log(LogLevel.Warning, "Reserved humidity resolution selected; treating it as 14-bit");
                        return 14;
                }
            }
        }

        private byte pointer;
        private ushort configuration;
        private ushort temperatureRaw;
        private ushort humidityRaw;

        private readonly Queue<byte> outputBuffer;

        // Defaults a board can be in rather than zeros: a part reading -40 C and 0 %RH at reset
        // looks broken, and 0 C is a value the firmware cannot distinguish from a real cold room.
        private const decimal DefaultTemperature = 25m;
        private const decimal DefaultHumidity = 45m;
        private const ushort DefaultConfiguration = ModeBit;   // the datasheet's reset value is 0x1000

        private const decimal MinTemperature = -40m;
        private const decimal MaxTemperature = 125m;
        private const decimal MinHumidity = 0m;
        private const decimal MaxHumidity = 100m;

        private const decimal TemperatureSpan = 165m;
        private const decimal TemperatureOffset = 40m;
        private const decimal HumiditySpan = 100m;
        private const decimal RawSpan = 65536m;

        private const ushort ResetBit = 1 << 15;
        private const ushort HeaterBit = 1 << 13;
        private const ushort ModeBit = 1 << 12;
        private const ushort TemperatureResolutionBit = 1 << 10;
        private const int HumidityResolutionShift = 8;
        // Bit 11 (BTST) is read-only and always 0 here; bits 7:0 and 14 are reserved.
        private const ushort WritableConfigurationMask = HeaterBit | ModeBit | TemperatureResolutionBit | (0x3 << HumidityResolutionShift);

        private const ushort ManufacturerId = 0x5449;   // "TI"
        private const ushort DeviceId = 0x1050;

        private enum Registers : byte
        {
            Temperature = 0x00,
            Humidity = 0x01,
            Configuration = 0x02,
            SerialId0 = 0xFB,
            SerialId1 = 0xFC,
            SerialId2 = 0xFD,
            ManufacturerId = 0xFE,
            DeviceId = 0xFF,
        }
    }
}
