//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.Analog
{
    // An ADC model whose input channels can carry a sine on top of their DC level, evaluated from
    // virtual time at each conversion. Lets a model of an AC circuit put a waveform on a channel
    // without knowing which ADC it is talking to.
    public interface ISineInputADC : IPeripheral
    {
        // The channel's DC level in microvolts, without any sine riding on it.
        uint GetADCBias(int channel);

        // Adds amplitude * sin(2*pi*f*t + phase) to the channel, t being virtual time.
        void SetADCSine(int channel, uint amplitudeMicrovolts, uint frequencyMilliHertz, int phaseDegrees = 0);

        void ClearADCSine(int channel);
    }
}
