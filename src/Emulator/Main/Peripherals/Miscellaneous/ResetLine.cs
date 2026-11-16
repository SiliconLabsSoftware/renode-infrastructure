//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public static class ResetLineExtensions
    {
        public static void CreateResetLine(this Emulation emulation, string name)
        {
            emulation.ExternalsManager.AddExternal(new ResetLine(), name);
        }
    }

    // A reset wire from a GPIO output in one machine to the RESETn pin of another machine's MCU.
    //
    // A host MCU typically resets a companion MCU by pulsing a GPIO low for about a millisecond.
    // This external listens to that pin and,
    // on the RISING edge (reset released), resets the target machine the way a RESETn release
    // does - through Machine.RequestReset, which runs at the target's next synced state.
    //
    // Known simplification: while the line is held low the target keeps executing. On silicon it
    // is held in reset for the pulse. For a short pulse and a target whose reset path begins
    // with a full re-init, nothing observable depends on that time; it
    // is stated so that nobody measures reset-hold behavior here and believes the number.
    //
    //     emulation CreateResetLine "reset"
    //     mach set "host";    reset AttachInput sysbus.gpioPort 16      # PB0
    //     mach set "target";  reset AttachTarget sysbus.cpu             # any peripheral of the target
    public class ResetLine : IExternal, IGPIOReceiver
    {
        public void Reset()
        {
            // Attachments and the counter survive machine resets - see SPILink.Reset.
        }

        public void AttachInput(INumberedGPIOOutput port, int pinNumber)
        {
            if(!port.Connections.TryGetValue(pinNumber, out var gpio))
            {
                throw new RecoverableException($"{port.GetName()}@{pinNumber} is not a GPIO of that port");
            }
            gpio.Connect(this, 0);
        }

        // Pass the target's EMU (`reset AttachTarget sysbus.emu`) and the reset is reported to the
        // firmware as a PIN reset in RSTCAUSE; any other peripheral of the machine works too, but
        // then the firmware reads the cause of a software reset.
        public void AttachTarget(IPeripheral anyPeripheralOfTheTargetMachine)
        {
            target = anyPeripheralOfTheTargetMachine.GetMachine();
            causeSink = anyPeripheralOfTheTargetMachine as IResetCauseSink;
        }

        public void OnGPIO(int number, bool value)
        {
            if(value == level)
            {
                return;
            }
            level = value;
            if(!value)
            {
                Asserted = true;
                return;
            }
            Asserted = false;
            if(target == null)
            {
                this.Log(LogLevel.Warning, "Reset released but no target machine attached");
                return;
            }
            ResetCount++;
            this.Log(LogLevel.Info, "Reset line released; resetting the target machine (count {0})", ResetCount);
            causeSink?.ReportPinReset();
            target.RequestReset();
        }

        public ulong ResetCount { get; private set; }
        public bool Asserted { get; private set; }

        private IMachine target;
        private IResetCauseSink causeSink;
        private bool level = true;
    }
}
