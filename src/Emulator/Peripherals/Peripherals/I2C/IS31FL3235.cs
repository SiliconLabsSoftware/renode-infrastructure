//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.I2C
{
    // Lumissil IS31FL3235 28-channel I2C LED driver (8-bit PWM per channel, latched by the UPDATE
    // register). Modeled for the usual driver sequence: RESET, SHUTDOWN, a block write of the 28
    // LED control registers, block writes of PWM values, then UPDATE to latch. The address pointer
    // auto-increments on every byte, as on the part.
    //
    // Observation for tests: `sysbus.i2c0.ledDriver0 Level 5` returns the LATCHED, effective 8-bit
    // level of channel 5 (0 when the device is in shutdown or the channel is disabled), and `Levels`
    // dumps all 28. Each UPDATE that changes something is logged at Info with the new levels.
    public class IS31FL3235 : II2CPeripheral
    {
        public IS31FL3235()
        {
            Reset();
        }

        public void Reset()
        {
            Array.Clear(registers, 0, registers.Length);
            Array.Clear(activePwm, 0, activePwm.Length);
            Array.Clear(activeControl, 0, activeControl.Length);
            pointer = 0;
            collectingAddress = true;
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
                pointer++;
            }
        }

        public byte[] Read(int count)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++)
            {
                result[i] = pointer < registers.Length ? registers[pointer] : (byte)0;
                pointer++;
            }
            return result;
        }

        public void FinishTransmission()
        {
            collectingAddress = true;
        }

        // Effective, latched level of a channel (0..255), 0-based channel index.
        public int Level(int channel)
        {
            if(channel < 0 || channel >= Channels)
            {
                throw new RecoverableException("IS31FL3235 channels are 0..27");
            }
            var shutdown = (registers[Shutdown] & 0x01) == 0;
            var globalOff = (registers[GlobalControl] & 0x01) != 0;
            var enabled = (activeControl[channel] & 0x01) != 0;
            return (shutdown || globalOff || !enabled) ? 0 : activePwm[channel];
        }

        public string Levels => string.Join(",", Enumerable.Range(0, Channels).Select(Level));

        public bool IsShutdown => (registers[Shutdown] & 0x01) == 0;

        private void WriteRegister(byte address, byte value)
        {
            switch(address)
            {
            case ResetReg:
                this.Log(LogLevel.Info, "RESET");
                Array.Clear(registers, 0, registers.Length);
                Array.Clear(activePwm, 0, activePwm.Length);
                Array.Clear(activeControl, 0, activeControl.Length);
                return;
            case Update:
                var before = Levels;
                Array.Copy(registers, PwmBase, activePwm, 0, Channels);
                Array.Copy(registers, ControlBase, activeControl, 0, Channels);
                var after = Levels;
                if(before != after)
                {
                    this.Log(LogLevel.Info, "UPDATE: levels [{0}]", after);
                }
                return;
            default:
                if(address < registers.Length)
                {
                    registers[address] = value;
                }
                else
                {
                    this.Log(LogLevel.Warning, "Write to unknown register 0x{0:X2}", address);
                }
                return;
            }
        }

        private readonly byte[] registers = new byte[0x50];
        private readonly byte[] activePwm = new byte[Channels];
        private readonly byte[] activeControl = new byte[Channels];
        private byte pointer;
        private bool collectingAddress;

        private const int Channels = 28;
        private const byte Shutdown = 0x00;
        private const byte PwmBase = 0x05;      // 0x05..0x20
        private const byte Update = 0x25;
        private const byte ControlBase = 0x2A;  // 0x2A..0x45
        private const byte GlobalControl = 0x4A;
        private const byte ResetReg = 0x4F;
    }
}
