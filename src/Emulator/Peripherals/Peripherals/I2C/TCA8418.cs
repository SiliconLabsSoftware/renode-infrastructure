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
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.I2C
{
    // Texas Instruments TCA8418 I2C keypad scan controller (80-key matrix, 10 FIFO'd key events).
    //
    // Modeled for a typical keypad driver: a register file behind a one-byte address
    // pointer, KEY_EVENT_A draining a FIFO of key-press/release codes, KEY_LCK_EC reporting how many
    // are pending, INT_STAT.K_INT written 1-to-clear, and the GPIO_DAT_STATn registers reflecting the
    // matrix lines directly (active low) for the bootloader's pre-scan. Buttons are injected from the
    // Monitor:  sysbus.i2c0.keypad PressKey 3   /  ReleaseKey 3   (0-based column on row 0, which is
    // how the part numbers them: key code = row*10 + col + 1, button index = code - 1).
    public class TCA8418 : II2CPeripheral, IGPIOReceiver
    {
        public TCA8418()
        {
            IRQ = new GPIO();
            Reset();
        }

        public void Reset()
        {
            Array.Clear(registers, 0, registers.Length);
            events.Clear();
            pressed.Clear();
            pointer = 0;
            collectingAddress = true;
            // INT is open-drain, active low: idle high.
            IRQ.Set(true);
        }

        public void Write(byte[] data)
        {
            foreach(var b in data)
            {
                if(collectingAddress)
                {
                    pointer = b;
                    collectingAddress = false;
                    continue;
                }
                WriteRegister(pointer, b);
                AdvancePointer();
            }
        }

        public byte[] Read(int count)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++)
            {
                result[i] = ReadRegister(pointer);
                AdvancePointer();
            }
            return result;
        }

        public void FinishTransmission()
        {
            collectingAddress = true;
        }

        public void OnGPIO(int number, bool value)
        {
            // No GPIO inputs are modeled; keys are injected through PressKey/ReleaseKey.
        }

        // Row 0 is the only row the firmware configures (KP_GPIO1 = 0x01); columns 0..8 are the keys.
        public void PressKey(int column)
        {
            PressKey(0, column);
        }

        public void ReleaseKey(int column)
        {
            ReleaseKey(0, column);
        }

        public void PressKey(int row, int column)
        {
            CheckRowCol(row, column);
            var key = (row, column);
            if(pressed.Contains(key))
            {
                return;
            }
            pressed.Add(key);
            EnqueueEvent(row, column, true);
        }

        public void ReleaseKey(int row, int column)
        {
            CheckRowCol(row, column);
            var key = (row, column);
            if(!pressed.Remove(key))
            {
                return;
            }
            EnqueueEvent(row, column, false);
        }

        public void TapKey(int column)
        {
            PressKey(0, column);
            ReleaseKey(0, column);
        }

        public int PendingEvents => events.Count;

        public string PressedKeys => string.Join(",", pressed.OrderBy(k => k.row * 10 + k.col).Select(k => $"r{k.row}c{k.col}"));

        public GPIO IRQ { get; }

        private void EnqueueEvent(int row, int column, bool isPress)
        {
            if(events.Count >= FifoDepth)
            {
                registers[IntStat] |= OvrFlowInt;
                this.Log(LogLevel.Warning, "Key event FIFO overflow, dropping {0} of r{1}c{2}", isPress ? "press" : "release", row, column);
            }
            else
            {
                var code = (byte)(row * 10 + column + 1);
                events.Enqueue((byte)(code | (isPress ? 0x80 : 0x00)));
            }
            registers[IntStat] |= KInt;
            this.Log(LogLevel.Info, "Key r{0}c{1} {2} (code 0x{3:X2}), {4} event(s) pending", row, column, isPress ? "pressed" : "released", row * 10 + column + 1, events.Count);
            UpdateInterrupt();
        }

        private void UpdateInterrupt()
        {
            var keyIrqEnabled = (registers[Cfg] & CfgKeIen) != 0;
            var active = keyIrqEnabled && (registers[IntStat] & (KInt | OvrFlowInt)) != 0;
            // active low
            IRQ.Set(!active);
        }

        private byte ReadRegister(byte address)
        {
            switch(address)
            {
            case KeyLckEc:
                return (byte)Math.Min(events.Count, 0xF);
            case KeyEventA:
                return events.Count > 0 ? events.Dequeue() : (byte)0;
            case GpioDatStat1:
                // ROW0..7 - a pressed key pulls its row low during scan; report rows with a key down as 0.
                return (byte)~PressedMask(k => k.row < 8 ? 1 << k.row : 0);
            case GpioDatStat2:
                // COL0..7, active low.
                return (byte)~PressedMask(k => k.col < 8 ? 1 << k.col : 0);
            case GpioDatStat3:
                // COL8..9 in bits 1:0; upper bits read 1 (no such lines).
                return (byte)(0xFC | (~PressedMask(k => k.col >= 8 ? 1 << (k.col - 8) : 0) & 0x03));
            default:
                return address < registers.Length ? registers[address] : (byte)0;
            }
        }

        private int PressedMask(Func<(int row, int col), int> bitOf)
        {
            var m = 0;
            foreach(var k in pressed)
            {
                m |= bitOf(k);
            }
            return m;
        }

        private void WriteRegister(byte address, byte value)
        {
            if(address >= registers.Length)
            {
                this.Log(LogLevel.Warning, "Write to unknown register 0x{0:X2}", address);
                return;
            }
            switch(address)
            {
            case IntStat:
                // write-1-to-clear
                registers[IntStat] &= (byte)~value;
                if((value & KInt) != 0 && events.Count > 0)
                {
                    // Datasheet: K_INT cannot be cleared while events remain in the FIFO.
                    registers[IntStat] |= KInt;
                }
                break;
            case KeyLckEc:
            case KeyEventA:
            case GpioDatStat1:
            case GpioDatStat2:
            case GpioDatStat3:
                // read-only
                break;
            default:
                registers[address] = value;
                break;
            }
            UpdateInterrupt();
        }

        private void AdvancePointer()
        {
            // CFG.AI enables auto-increment of the register pointer.
            if((registers[Cfg] & CfgAi) != 0 && pointer < registers.Length - 1)
            {
                pointer++;
            }
        }

        private static void CheckRowCol(int row, int column)
        {
            if(row < 0 || row > 7 || column < 0 || column > 9)
            {
                throw new RecoverableException("TCA8418 keys are row 0..7, column 0..9");
            }
        }

        private readonly byte[] registers = new byte[0x2F];
        private readonly Queue<byte> events = new Queue<byte>();
        private readonly HashSet<(int row, int col)> pressed = new HashSet<(int row, int col)>();
        private byte pointer;
        private bool collectingAddress;

        private const int FifoDepth = 10;
        private const byte Cfg = 0x01;
        private const byte IntStat = 0x02;
        private const byte KeyLckEc = 0x03;
        private const byte KeyEventA = 0x04;
        private const byte GpioDatStat1 = 0x14;
        private const byte GpioDatStat2 = 0x15;
        private const byte GpioDatStat3 = 0x16;
        private const byte CfgKeIen = 0x01;
        private const byte CfgAi = 0x80;
        private const byte KInt = 0x01;
        private const byte OvrFlowInt = 0x08;
    }
}
