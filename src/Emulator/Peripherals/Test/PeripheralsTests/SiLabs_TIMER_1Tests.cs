//
// Copyright (c) 2010-2026 Antmicro
// Copyright (c) 2022-2026 Silicon Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.UnitTests
{
    // Covers the buffered compare/period registers, the counter's wrap point, the compare
    // outputs, input capture and the overflow DMA request. The buffered registers are easy to
    // regress into something that looks fine -- registers read back exactly what was
    // written -- while silently breaking any firmware that paces a PWM ramp on the update
    // event and reads the *active* register back to compute its next step.
    [TestFixture]
    public class SiLabs_TIMER_1Tests
    {
        [SetUp]
        public void CreatePeripheral()
        {
            // A fresh Machine per test: these tests advance the shared clock source, so a
            // fixture-wide instance would leak counter state between them.
            machine = new Machine();
            timer = new SiLabs_TIMER_1(machine, CounterWidth, TimerFrequency);
            timer.Reset();
            timer.WriteDoubleWord(EnOffset, 1);
        }

        [Test]
        public void ShouldNotTransferOcbBeforeUpdateEvent()
        {
            StartTimerWithTop(Top);
            timer.WriteDoubleWord(Cc0OcOffset, 100);
            timer.WriteDoubleWord(Cc0OcbOffset, 900);

            Assert.IsTrue(OcbValid(0), "STATUS.OCBV0 should advertise a pending buffered value");

            // Deliberately short of one period. The buffered value must still be waiting: a
            // model that writes OCB straight through to OC would fail here, and so would one
            // that transfers on any compare event rather than only on the wrap after TOP.
            AdvanceTicks(Top / 2);

            Assert.AreEqual(100, timer.ReadDoubleWord(Cc0OcOffset), "OC changed mid-period");
            Assert.AreEqual(900, timer.ReadDoubleWord(Cc0OcbOffset), "OCB should be unchanged");
            Assert.IsTrue(OcbValid(0), "STATUS.OCBV0 cleared without an update event");
        }

        [Test]
        public void ShouldTransferOcbToOcOnUpdateEvent()
        {
            StartTimerWithTop(Top);
            timer.WriteDoubleWord(Cc0OcOffset, 100);
            timer.WriteDoubleWord(Cc0OcbOffset, 900);

            AdvanceTicks(Period);

            Assert.AreEqual(900, timer.ReadDoubleWord(Cc0OcOffset), "OCB was not latched into OC");
            Assert.IsFalse(OcbValid(0), "STATUS.OCBV0 should clear once the buffer is consumed");
            Assert.IsTrue(OverflowFlagSet, "the wrap after TOP should raise IF.OF");
        }

        [Test]
        public void ShouldTransferEveryCompareChannelIndependently()
        {
            StartTimerWithTop(Top);
            // Only channels 0 and 2 get a buffered write; channel 1 must be left alone, since
            // transferring an untouched channel would clobber a compare value the firmware set
            // directly through OC.
            timer.WriteDoubleWord(Cc1OcOffset, 111);
            timer.WriteDoubleWord(Cc0OcbOffset, 300);
            timer.WriteDoubleWord(Cc2OcbOffset, 500);

            AdvanceTicks(Period);

            Assert.AreEqual(300, timer.ReadDoubleWord(Cc0OcOffset));
            Assert.AreEqual(111, timer.ReadDoubleWord(Cc1OcOffset), "CC1_OC was overwritten");
            Assert.AreEqual(500, timer.ReadDoubleWord(Cc2OcOffset));
        }

        [Test]
        public void ShouldTransferTopbToTopOnUpdateEvent()
        {
            StartTimerWithTop(Top);
            timer.WriteDoubleWord(TopbOffset, Top / 2);

            Assert.AreEqual(Top, timer.ReadDoubleWord(TopOffset), "TOP changed before the update event");

            AdvanceTicks(Period);

            Assert.AreEqual(Top / 2, timer.ReadDoubleWord(TopbOffset));
            Assert.AreEqual(Top / 2, timer.ReadDoubleWord(TopOffset), "TOPB was not latched into TOP");
        }

        [Test]
        public void ShouldWrapAtTopRatherThanCounterWidth()
        {
            StartTimerWithTop(Top);

            // A period and a third: unambiguously past the wrap point if the counter is
            // honoring TOP, and nowhere near the 16-bit width if it is not.
            AdvanceTicks(Period + (Top / 3));

            var count = timer.ReadDoubleWord(CntOffset);
            Assert.Less(count, Top, "CNT ran past TOP, so the counter is wrapping at its width");
            Assert.IsTrue(OverflowFlagSet, "no overflow was raised within one TOP period");
        }

        [Test]
        public void ShouldPaceUpdateEventsAtTopPeriod()
        {
            StartTimerWithTop(Top);
            timer.WriteDoubleWord(Cc0OcbOffset, 1);
            AdvanceTicks(Period);
            Assert.AreEqual(1, timer.ReadDoubleWord(Cc0OcOffset), "no transfer in the first period");

            // A second period must produce a second, independent update event -- the ramp
            // cadence firmware depends on, not a one-shot.
            timer.WriteDoubleWord(Cc0OcbOffset, 2);
            AdvanceTicks(Period);
            Assert.AreEqual(2, timer.ReadDoubleWord(Cc0OcOffset), "no transfer in the second period");
        }

        [Test]
        public void ShouldKeepBufferPendingWhileStopped()
        {
            // No CMD.START: with no counter running there is no update event, so the buffered
            // value must stay pending rather than quietly applying itself.
            timer.WriteDoubleWord(TopOffset, Top);
            timer.WriteDoubleWord(Cc0OcOffset, 7);
            timer.WriteDoubleWord(Cc0OcbOffset, 4242);

            AdvanceTicks(Top * 4);

            Assert.AreEqual(7, timer.ReadDoubleWord(Cc0OcOffset), "OC moved while the timer was stopped");
            Assert.IsTrue(OcbValid(0), "the buffered value should still be pending");

            // ...and it lands on the first update event after the timer is started.
            timer.WriteDoubleWord(CmdOffset, CmdStart);
            AdvanceTicks(Period);

            Assert.AreEqual(4242, timer.ReadDoubleWord(Cc0OcOffset));
            Assert.IsFalse(OcbValid(0));
        }

        [Test]
        public void ShouldResetBufferedRegistersAndValidBits()
        {
            StartTimerWithTop(Top);
            timer.WriteDoubleWord(Cc0OcbOffset, 1234);
            timer.WriteDoubleWord(TopbOffset, 4321);
            Assert.IsTrue(OcbValid(0));

            timer.Reset();

            Assert.IsFalse(OcbValid(0), "STATUS.OCBV0 survived a reset");
            Assert.AreEqual(0, timer.ReadDoubleWord(Cc0OcbOffset));
            Assert.AreEqual(0, timer.ReadDoubleWord(Cc0OcOffset));
        }

        // ---------------------------------------------------------------------------------
        // Compare outputs. These are what a PWMTester measures.
        // ---------------------------------------------------------------------------------

        [Test]
        public void ShouldNotDriveOutputWhenChannelIsOff()
        {
            // The default CC mode is OFF, which is how a timer used purely as a timebase is
            // left -- TIMER0 carries the sleeptimer on EFR32MG24. Such a timer must generate no
            // GPIO traffic at all, both because it is correct and because the cost of toggling
            // a pin every period would otherwise be paid by every platform using this model.
            var probe = ConnectProbe(0);
            StartTimerWithTop(Top);
            timer.WriteDoubleWord(Cc0OcOffset, Top / 2);

            AdvanceTicks(Top * 4);

            Assert.AreEqual(0, probe.Edges, "an OFF channel drove its output");
        }

        [Test]
        public void ShouldProduceTwoEdgesPerPeriodInPwmMode()
        {
            var probe = ConnectProbe(0);
            StartPwmChannel(0, compare: Top / 4);
            probe.Clear();

            // Three whole periods, stopping short of the fourth period's match.
            AdvanceTicks((Period * 3) + (Top / 8));

            // Deasserted at the match, asserted at the period start.
            Assert.AreEqual(6, probe.Edges, "expected exactly two edges per period");
        }

        [Test]
        public void ShouldAssertOutputForCompareTicksOfEachPeriod()
        {
            var probe = ConnectProbe(0);
            StartPwmChannel(0, compare: Top / 4);

            // Sample either side of the match point inside one period.
            AdvanceTicks(Top / 8);
            Assert.IsTrue(probe.State, "output should be asserted before the compare match");
            AdvanceTicks(Top / 4);
            Assert.IsFalse(probe.State, "output should be deasserted after the compare match");
        }

        [Test]
        public void ShouldHoldOutputLowForZeroCompare()
        {
            // 0% duty. The match coincides with the period start, so the output never asserts
            // and there are no edges at all -- a channel that is fully off. Getting this exactly
            // right matters more than the general case: an off-by-one would leave an LED
            // channel glowing at 1/TOP instead of dark.
            var probe = ConnectProbe(0);
            StartPwmChannel(0, compare: 0);
            probe.Clear();

            AdvanceTicks(Top * 4);

            Assert.IsFalse(probe.State, "a zero compare must leave the output deasserted");
            Assert.AreEqual(0, probe.Edges, "a zero compare must not produce a waveform");
        }

        [Test]
        public void ShouldHoldOutputHighWhenCompareReachesTop()
        {
            // 100% duty, the other endpoint: a compare value at or above TOP never deasserts
            // the output, so the pin is steady high with no edges.
            var probe = ConnectProbe(0);
            StartPwmChannel(0, compare: Top);
            probe.Clear();

            AdvanceTicks(Top * 4);

            Assert.IsTrue(probe.State, "a compare at TOP must leave the output asserted");
            Assert.AreEqual(0, probe.Edges, "a compare at TOP must not produce a waveform");
        }

        [Test]
        public void ShouldInvertOutputWhenOutinvIsSet()
        {
            // OUTINV is invisible to any duty reading taken from CCn_OC, so a model that
            // ignored it would look perfect at the register level while driving the pin
            // backwards.
            var probe = ConnectProbe(0);
            timer.WriteDoubleWord(Cc0CtrlOffset, CcCtrlOutinv);
            StartPwmChannel(0, compare: Top / 4);

            AdvanceTicks(Top / 8);
            Assert.IsFalse(probe.State, "inverted output should be low while the compare is asserted");
            AdvanceTicks(Top / 4);
            Assert.IsTrue(probe.State, "inverted output should be high after the compare match");
        }

        [Test]
        public void ShouldApplyBufferedCompareToTheOutputFromTheNextPeriod()
        {
            // The point of the buffered register: a duty written mid-period must not glitch the
            // pulse it lands in, and must take effect on the period boundary.
            var probe = ConnectProbe(0);
            StartPwmChannel(0, compare: Top / 2);
            AdvanceTicks(Top / 8);

            timer.WriteDoubleWord(Cc0OcbOffset, Top / 8);

            // Past where the *new*, shorter compare would have ended the pulse, but still
            // inside the pulse the old compare is producing.
            AdvanceTicks(Top / 8);
            Assert.IsTrue(probe.State, "the new compare took effect mid-period");
        }

        [Test]
        public void ShouldDriveChannelsIndependently()
        {
            // Catches a model sharing one output state across channels, and any wiring mistake
            // routing several compare channels to the same pin.
            var first = ConnectProbe(0);
            var second = ConnectProbe(1);
            StartPwmChannel(0, compare: Top / 4);
            SetPwmChannel(1, compare: Top * 3 / 4);

            AdvanceTicks(Period);              // begin a period with both channels configured
            AdvanceTicks(Top / 2);

            Assert.IsFalse(first.State, "channel 0 should be past its compare match");
            Assert.IsTrue(second.State, "channel 1 should still be before its compare match");
        }

        [Test]
        public void ShouldReleaseOutputWhenStopped()
        {
            var probe = ConnectProbe(0);
            StartPwmChannel(0, compare: Top / 4);
            AdvanceTicks(Top / 8);
            Assert.IsTrue(probe.State);

            timer.WriteDoubleWord(CmdOffset, CmdStop);
            Assert.IsFalse(probe.State, "a stopped timer should release its output to the COIST idle level");

            probe.Clear();
            AdvanceTicks(Top * 4);
            Assert.AreEqual(0, probe.Edges, "a stopped timer kept driving its output");
        }

        [Test]
        public void ShouldToggleOncePerPeriodWhenOutputCompareEqualsTop()
        {
            // A compare value of TOP in output-compare mode with CMOA = TOGGLE is how firmware
            // asks for a square wave at half the timer rate.
            var probe = ConnectProbe(0);
            timer.WriteDoubleWord(Cc0CtrlOffset, CcCtrlCmoaToggle);
            timer.WriteDoubleWord(Cc0OcOffset, Top);
            timer.WriteDoubleWord(Cc0CfgOffset, CcCfgModeOutputCompare);
            StartTimerWithTop(Top);
            probe.Clear();

            AdvanceTicks(Period * 4);

            Assert.AreEqual(4, probe.Edges, "expected exactly one toggle per period");
        }

        // ---------------------------------------------------------------------------------
        // Input capture and the overflow DMA request.
        // ---------------------------------------------------------------------------------

        [Test]
        public void ShouldCaptureCounterOnRisingInputEdge()
        {
            timer.WriteDoubleWord(Cc0CfgOffset, CcCfgModeInputCapture);
            StartTimerWithTop(Top);
            AdvanceTicks(100);

            timer.OnGPIO(0, true);
            // ICEDGE resets to RISING, so the falling edge is not a capture.
            timer.OnGPIO(0, false);

            Assert.AreEqual(1, timer.Captures);
            Assert.AreNotEqual(0, timer.ReadDoubleWord(IfOffset) & IfCc0, "a capture should raise IF.CC0");
            Assert.AreEqual(100, timer.ReadDoubleWord(Cc0IcofOffset), "ICOF should hold the newest capture");
            Assert.AreEqual(100, timer.ReadDoubleWord(Cc0IcfOffset), "ICF should pop the captured count");
        }

        [Test]
        public void ShouldPulseOverflowRequestOncePerUpdateEvent()
        {
            var probe = ConnectProbe(OverflowRequestOutput);
            StartTimerWithTop(Top);

            AdvanceTicks((Period * 2) + (Top / 2));

            Assert.AreEqual(4, probe.Edges, "expected one pulse (two edges) per overflow");
            Assert.IsFalse(probe.State, "the request should return low after each pulse");
        }

        private void StartPwmChannel(int channel, uint compare)
        {
            timer.WriteDoubleWord(TopOffset, Top);
            timer.WriteDoubleWord(CmdOffset, CmdStart);
            SetPwmChannel(channel, compare);
        }

        private void SetPwmChannel(int channel, uint compare)
        {
            timer.WriteDoubleWord(Cc0OcOffset + channel * CcStride, compare);
            timer.WriteDoubleWord(Cc0CfgOffset + channel * CcStride, CcCfgModePwm);
        }

        private GPIOProbe ConnectProbe(int channel)
        {
            var probe = new GPIOProbe();
            timer.Connections[channel].Connect(probe, 0);
            return probe;
        }

        // Minimal IGPIOReceiver: records the current level and counts transitions. PWMTester
        // could stand in, but it reports only a ratio -- these tests need the level at a chosen
        // instant, which is exactly what a duty meter cannot tell you.
        private class GPIOProbe : IGPIOReceiver
        {
            public void OnGPIO(int number, bool value)
            {
                // Genuine transitions only. GPIO.Connect() pushes the line's current level to a
                // newly attached receiver, so counting every call would score that initial
                // notification as an edge and make a quiet line look like it was toggling.
                if(value != State)
                {
                    Edges++;
                }
                State = value;
            }

            public void Clear()
            {
                Edges = 0;
            }

            public void Reset()
            {
            }

            public bool State { get; private set; }
            public int Edges { get; private set; }
        }

        private void StartTimerWithTop(uint top)
        {
            timer.WriteDoubleWord(TopOffset, top);
            timer.WriteDoubleWord(CmdOffset, CmdStart);
        }

        private void AdvanceTicks(uint ticks)
        {
            ((BaseClockSource)machine.ClockSource).Advance(
                TimeInterval.FromTicks(ticks * (TimeInterval.TicksPerSecond / TimerFrequency)));
        }

        private bool OcbValid(int channel) => (timer.ReadDoubleWord(StatusOffset) & (1u << (StatusOcbv0Bit + channel))) != 0;

        private bool OverflowFlagSet => (timer.ReadDoubleWord(IfOffset) & IfOverflow) != 0;

        // Machine, not IMachine: the peripheral's constructor takes the concrete type.
        private Machine machine;
        private SiLabs_TIMER_1 timer;

        // 1 MHz keeps tick-to-TimeInterval conversion exact, and TOP is small enough that a
        // whole period is a cheap amount of virtual time to advance.
        private const uint TimerFrequency = 1000000;
        private const int CounterWidth = 16;
        private const uint Top = 1200;
        // The counter holds every value from 0 through TOP, so the update event comes TOP + 1
        // ticks after the period starts.
        private const uint Period = Top + 1;

        private const long CmdOffset = 0xC;
        private const long StatusOffset = 0x10;
        private const long IfOffset = 0x14;
        private const long TopOffset = 0x1C;
        private const long TopbOffset = 0x20;
        private const long CntOffset = 0x24;
        private const long EnOffset = 0x30;
        private const long Cc0OcOffset = 0x68;
        private const long Cc0OcbOffset = 0x70;
        private const long Cc0IcfOffset = 0x74;
        private const long Cc0IcofOffset = 0x78;
        private const long Cc1OcOffset = 0x88;
        private const long Cc2OcOffset = 0xA8;
        private const long Cc2OcbOffset = 0xB0;

        private const long Cc0CfgOffset = 0x60;
        private const long Cc0CtrlOffset = 0x64;
        // CC register blocks are 0x20 apart: CFG 0x60/0x80/0xA0, OC 0x68/0x88/0xA8.
        private const long CcStride = 0x20;

        private const uint CmdStart = 0x1;
        private const uint CmdStop = 0x2;
        private const uint IfOverflow = 0x1;
        private const uint IfCc0 = 1u << 4;
        private const uint CcCfgModeInputCapture = 0x1;
        private const uint CcCfgModeOutputCompare = 0x2;
        private const uint CcCfgModePwm = 0x3;
        private const uint CcCtrlOutinv = 1u << 2;
        private const uint CcCtrlCmoaToggle = 1u << 8;
        // Connections[3] is the overflow DMA request; 0..2 are the compare outputs.
        private const int OverflowRequestOutput = 3;
        private const int StatusOcbv0Bit = 8;
    }
}
