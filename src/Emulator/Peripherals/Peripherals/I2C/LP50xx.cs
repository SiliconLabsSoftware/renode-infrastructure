//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Linq;

using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.I2C
{
    // TI LP5024 (24 outputs) / LP5036 (36 outputs) I2C RGB LED drivers, modeled for the usual
    // driver sequence: a RESET write,
    // DEVICE_CONFIG1 (log scale, power save, auto-increment, dithering), DEVICE_CONFIG0 CHIP_EN,
    // then per-LED brightness and per-output color writes, single bytes or auto-incremented
    // blocks. The two parts share one register layout shifted by the extra LED_CONFIG byte the
    // LP5036 needs for its twelve LED groups:
    //
    //                        LP5024   LP5036
    //     DEVICE_CONFIG0      0x00     0x00     bit 6 CHIP_EN
    //     DEVICE_CONFIG1      0x01     0x01     bit 0 LED_GLOBAL_OFF, bit 3 AUTO_INCR_EN
    //     LED_CONFIG0[/1]     0x02     0x02-03  bank-control bit per LED group
    //     BANK_BRIGHTNESS     0x03     0x04
    //     BANK_A/B/C_COLOR    0x04-06  0x05-07
    //     LEDn_BRIGHTNESS     0x07+    0x08+    one per group of three outputs (8 / 12)
    //     OUTn_COLOR          0x0F+    0x14+    one per output (24 / 36)
    //     RESET               0x27     0x38     write 0xFF
    //
    // Observation for tests: `Level n` is the EFFECTIVE 8-bit drive of output n - its color
    // scaled by its group's brightness (or the bank's, when the group is in bank mode), 0 while
    // the chip is disabled or globally off - and `Levels` dumps all of them. That is the quantity
    // a person would see on the LED.
    //
    // Not modeled: the logarithmic brightness curve and PWM dithering (a level is reported as the
    // linear product), the power-save timing, and the actual PWM waveform on the output pins.
    public class LP50xx : II2CPeripheral
    {
        public LP50xx(int outputs = 24)
        {
            if(outputs != 24 && outputs != 36)
            {
                throw new ConstructionException("LP50xx: outputs must be 24 (LP5024) or 36 (LP5036)");
            }
            Outputs = outputs;
            Groups = outputs / 3;
            ledConfigBytes = outputs == 36 ? 2 : 1;
            bankBrightnessRegister = 2 + ledConfigBytes;
            bankColorRegister = bankBrightnessRegister + 1;
            ledBrightnessRegister = bankColorRegister + 3;
            outputColorRegister = ledBrightnessRegister + Groups;
            resetRegister = outputColorRegister + Outputs;
            registers = new byte[resetRegister + 1];
            Reset();
        }

        public void Reset()
        {
            Array.Clear(registers, 0, registers.Length);
            // DEVICE_CONFIG1 reset value: log scale, power save, auto-increment and dithering on.
            registers[1] = 0x3C;
            pointer = 0;
            writesSinceReset = 0;
        }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                return; // address probe
            }
            pointer = data[0];
            for(var i = 1; i < data.Length; ++i)
            {
                WriteRegister(pointer, data[i]);
                if(AutoIncrement)
                {
                    pointer++;
                }
            }
        }

        public byte[] Read(int count = 1)
        {
            var result = new byte[count];
            for(var i = 0; i < count; ++i)
            {
                result[i] = pointer < registers.Length ? registers[pointer] : (byte)0;
                if(AutoIncrement)
                {
                    pointer++;
                }
            }
            return result;
        }

        public void FinishTransmission()
        {
        }

        public int Outputs { get; }
        public int Groups { get; }

        public bool ChipEnabled => (registers[0] & 0x40) != 0;
        public bool GlobalOff => (registers[1] & 0x01) != 0;
        public bool AutoIncrement => (registers[1] & 0x08) != 0;

        // Register writes since the last reset - a cheap "is the firmware driving this part" probe.
        public ulong WritesSinceReset => writesSinceReset;

        public byte OutputColor(int output) => registers[outputColorRegister + Check(output, Outputs)];

        public byte LedBrightness(int group) => registers[ledBrightnessRegister + Check(group, Groups)];

        public bool GroupInBankMode(int group)
        {
            Check(group, Groups);
            var bit = registers[2 + group / 8] & (1 << (group % 8));
            return bit != 0;
        }

        // The effective drive of one output, 0-255.
        public int Level(int output)
        {
            Check(output, Outputs);
            if(!ChipEnabled || GlobalOff)
            {
                return 0;
            }
            var group = output / 3;
            int brightness, color;
            if(GroupInBankMode(group))
            {
                brightness = registers[bankBrightnessRegister];
                color = registers[bankColorRegister + output % 3];
            }
            else
            {
                brightness = LedBrightness(group);
                color = OutputColor(output);
            }
            return (color * brightness + 127) / 255;
        }

        public string Levels => string.Join(" ", Enumerable.Range(0, Outputs).Select(Level));

        private void WriteRegister(byte register, byte value)
        {
            if(register == resetRegister)
            {
                if(value == 0xFF)
                {
                    this.Log(LogLevel.Debug, "Reset");
                    Reset();
                }
                return;
            }
            if(register >= registers.Length)
            {
                this.Log(LogLevel.Warning, "Write of 0x{0:X2} to register 0x{1:X2}, beyond the part's map; ignored", value, register);
                return;
            }
            registers[register] = value;
            writesSinceReset++;
            if(register == 0)
            {
                this.Log(LogLevel.Debug, "CHIP_EN {0}", ChipEnabled ? "set" : "clear");
            }
        }

        private static int Check(int index, int count)
        {
            if(index < 0 || index >= count)
            {
                throw new RecoverableException($"Index {index} is out of range; this part has {count}.");
            }
            return index;
        }

        private readonly byte[] registers;
        private readonly int ledConfigBytes;
        private readonly int bankBrightnessRegister;
        private readonly int bankColorRegister;
        private readonly int ledBrightnessRegister;
        private readonly int outputColorRegister;
        private readonly int resetRegister;
        private byte pointer;
        private ulong writesSinceReset;
    }
}
