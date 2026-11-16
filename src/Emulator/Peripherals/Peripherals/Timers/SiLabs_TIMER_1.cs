//
// Copyright (c) 2010-2026 Antmicro
// Copyright (c) 2022-2026 Silicon Labs
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Miscellaneous.SiLabs;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    // Allows for the viewing of register contents when debugging
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public partial class SiLabs_TIMER_1 : INumberedGPIOOutput, IRegisterablePeripheral<IGPIOReceiver, NumberRegistrationPoint<int>>, IGPIOReceiver
    {
        // `frequency` is the clock feeding the prescaler, in Hz. 0 derives it from the CMU.
        public SiLabs_TIMER_1(Machine machine, int width, uint frequency = 0) : this(machine)
        {
            fixedFrequency = frequency;

            if(width < 1 || width > 32)
            {
                this.Log(LogLevel.Error, "Timer width {0} is outside 1..32, using 16", width);
                width = 16;
            }

            // 2^width. The counter maximum cannot exceed this, even if TOP is larger.
            counterCeiling = 1UL << width;
        }

        partial void SiLabs_TIMER_1_Constructor()
        {
            // Frequency and limit are replaced when the timer starts.
            timer = new LimitTimer(machine.ClockSource, 1, this, "timer", 0xFFFFFFFFUL, direction: Direction.Ascending,
                                   enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
            timer.LimitReached += OnTimerLimitReached;

            channels = new CompareChannel[NumberOfChannels];
            for(uint i = 0; i < NumberOfChannels; i++)
            {
                channels[i] = new CompareChannel(machine, this, i);
            }

            IRQ = new GPIO();

            // STATUS.OCBVn is generated as three individually named flags.
            ocbValidBits = new[] { status_ocbv0_bit, status_ocbv1_bit, status_ocbv2_bit };

            // Numbered outputs 0..2 are the compare outputs and output 3 is the overflow/underflow
            // DMA request. IRQ stays the only GPIO property, so a bare `-> nvic@N` in a platform
            // file keeps resolving to it.
            var outputs = new Dictionary<int, IGPIO>();
            outputLevels = new bool[NumberOfChannels];
            captureFifos = new Queue<uint>[NumberOfChannels];
            captureInputLevel = new bool[NumberOfChannels];
            for(var i = 0; i < NumberOfChannels; i++)
            {
                outputs[i] = new GPIO();
                captureFifos[i] = new Queue<uint>();
            }
            overflowRequest = new GPIO();
            outputs[OverflowRequestOutput] = overflowRequest;
            Connections = new ReadOnlyDictionary<int, IGPIO>(outputs);
        }

        // Lets a platform file attach an IGPIOReceiver, such as a PWMTester, to a compare output:
        // `pwm_red: PWMTester @ timer3 0` together with `timer3: { 0 -> pwm_red@0 }`.
        public void Register(IGPIOReceiver peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
        }

        public void Unregister(IGPIOReceiver peripheral)
        {
            machine.UnregisterAsAChildOf(this, peripheral);
        }

        // Input capture. Input `number` is the CC channel's input signal; a platform file wires a
        // source to `timerN@k`. The GPIO pin route registers are not consulted.
        // On the selected edge (CC_CTRL.ICEDGE) the count is pushed to the two-deep ICF FIFO and
        // IF.CCk is raised. ICOF always holds the newest capture; IF.ICFOFk is raised when a
        // capture finds the FIFO full.
        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= NumberOfChannels)
            {
                this.Log(LogLevel.Warning, "Capture input {0} does not exist (CC0-CC{1})", number, NumberOfChannels - 1);
                return;
            }

            var previous = captureInputLevel[number];
            captureInputLevel[number] = value;
            if(previous == value || !timerIsRunning || cc_cfg_mode_field[number].Value != CC_CFG_MODE.INPUTCAPTURE)
            {
                return;
            }

            switch(cc_ctrl_icedge_field[number].Value)
            {
                case CC_CTRL_ICEDGE.RISING:
                    if(!value)
                    {
                        return;
                    }
                    break;
                case CC_CTRL_ICEDGE.FALLING:
                    if(value)
                    {
                        return;
                    }
                    break;
                case CC_CTRL_ICEDGE.BOTH:
                    break;
                default:
                    return;
            }
            Capture(number);
        }

        partial void TIMER_Reset()
        {
            timerIsRunning = false;
            timer.Enabled = false;
            foreach(var channel in channels)
            {
                channel.Reset();
            }

            registers.Reset();
            foreach(var fifo in captureFifos)
            {
                fifo.Clear();
            }
            Captures = 0;
            overflowRequest.Unset();
            DriveOutputsIdle();
            UpdateInterrupts();
        }

        partial void Cmd_Start_Write(bool a, bool b)
        {
            if(b)
            {
                StartCommand();
            }
        }

        partial void Cmd_Stop_Write(bool a, bool b)
        {
            if(b)
            {
                StopCommand();
            }
        }

        partial void En_En_Write(bool a, bool b)
        {
            if(b)
            {
                return;
            }

            // Clearing EN halts the counter and drops hardware-updated interrupt flags.
            timerIsRunning = false;
            timer.Enabled = false;
            foreach(var channel in channels)
            {
                channel.Reset();
            }
            DriveOutputsIdle();
            if_of_bit.Value = false;
            if_uf_bit.Value = false;
            if_cc0_bit.Value = false;
            if_cc1_bit.Value = false;
            if_cc2_bit.Value = false;
            UpdateInterrupts();
        }

        partial void Cfg_Write(uint a, uint b)
        {
            RestartTimer(false);
        }

        partial void Cc_Cfg_Mode_Write(ulong index, CC_CFG_MODE a, CC_CFG_MODE b)
        {
            RestartTimer(false);
            // A channel switched to PWM while the counter runs takes its level for the current
            // count right away instead of waiting for the next period.
            if(timerIsRunning && b == CC_CFG_MODE.PWM && CountingUp)
            {
                DriveOutput((int)index, PwmLevel((int)index, TimerCounter));
            }
        }

        partial void Cc_Ctrl_Outinv_Write(ulong index, bool a, bool b)
        {
            // The pin follows the new polarity immediately.
            DriveOutput((int)index, outputLevels[(int)index]);
        }

        partial void Cc_Ocb_Ocb_Write(ulong index, ulong a, ulong b)
        {
            // OCB is the buffered compare value: it is loaded into OC at the next update event,
            // and STATUS.OCBVn reads 1 while a value is waiting.
            ocbValidBits[(int)index].Value = true;
            if(!timerIsRunning)
            {
                this.Log(LogLevel.Debug, "CC{0}_OCB written to 0x{1:X} while the timer is stopped; "
                         + "it loads into CC{0}_OC at the first update event after CMD.START", index, b);
            }
        }

        partial void Topb_Topb_Write(ulong a, ulong b)
        {
            // TOPB is the buffered period: it is loaded into TOP at the next update event.
            status_topbv_bit.Value = true;
        }

        partial void Status_Running_ValueProvider(bool a)
        {
            status_running_bit.Value = timerIsRunning;
        }

        partial void Status_Dir_ValueProvider(STATUS_DIR a)
        {
            status_dir_bit.Value = CountingUp ? STATUS_DIR.UP : STATUS_DIR.DOWN;
        }

        partial void Cnt_Cnt_ValueProvider(ulong a)
        {
            cnt_cnt_field.Value = TimerCounter;
        }

        partial void Cnt_Cnt_Write(ulong a, ulong b)
        {
            timer.Enabled = false;
            cnt_cnt_field.Value = b;
            timer.Value = b;
            if(hostAllClocksGated)
            {
                frozenCounter = (uint)b;
            }
            else if(timerIsRunning)
            {
                timer.Enabled = true;
            }
            ArmCompareChannels((uint)b);
        }

        partial void Cc_Oc_Oc_Write(ulong index, ulong a, ulong b)
        {
            RestartTimer(false);
        }

        partial void If_Write(uint a, uint b)
        {
            UpdateInterrupts();
        }

        partial void Ien_Write(uint a, uint b)
        {
            UpdateInterrupts();
        }

        partial void Top_Write(uint a, uint b)
        {
            RestartTimer(false);
        }

        public uint TimerCounter
        {
            get
            {
                if(hostAllClocksGated)
                {
                    return frozenCounter;
                }
                if(timerIsRunning)
                {
                    if(timer.Enabled)
                    {
                        TrySyncTime();
                        return (uint)timer.Value;
                    }
                    else
                    {
                        return (uint)timer.Limit;
                    }
                }
                return 0;
            }

            set
            {
                timer.Value = value;
                RestartTimer(false);
            }
        }

        // The clock feeding the prescaler follows this CMU's EM01GRPACLK selection, and every
        // later change of it, when set. It takes precedence over the constructor's `frequency`.
        public SiLabs_CMU_3 Cmu
        {
            get
            {
                return clockTreeCmu;
            }

            set
            {
                if(clockTreeCmu != null)
                {
                    clockTreeCmu.ClockTreeChanged -= OnClockTreeChanged;
                }
                clockTreeCmu = value;
                if(clockTreeCmu != null)
                {
                    clockTreeCmu.ClockTreeChanged += OnClockTreeChanged;
                }
                OnClockTreeChanged();
            }
        }

        // The clock feeding the prescaler, in Hz: EM01GRPACLK of `Cmu` when that is set, else the
        // constructor's `frequency` when nonzero, else the HFXO frequency of the machine's CMU.
        // 0 means the clock is disabled or unknown.
        public uint SourceFrequency
        {
            get
            {
                if(clockTreeCmu != null)
                {
                    return (uint)clockTreeCmu.Em01GrpaClkFrequency;
                }
                if(fixedFrequency != 0)
                {
                    return fixedFrequency;
                }
                return cmu == null ? 0 : (uint)cmu.HfxoFrequency;
            }
        }

        // Captures taken since reset, across channels.
        public ulong Captures { get; private set; }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; private set; }

        public GPIO IRQ { get; private set; }

        private void OnClockTreeChanged()
        {
            if(timerIsRunning)
            {
                RestartTimer(false);
            }
        }

        private void Capture(int index)
        {
            var count = TimerCounter;
            if(captureFifos[index].Count >= CaptureFifoDepth)
            {
                SetCaptureOverflowFlag(index);
            }
            else
            {
                captureFifos[index].Enqueue(count);
            }
            cc_icof_icof_field[index].Value = count;
            Captures++;
            SetCompareFlag((uint)index);
        }

        // CCn_ICF read: pops the oldest capture. An empty FIFO reads as the newest capture.
        private uint ReadCaptureFifo(int index)
        {
            if(captureFifos[index].Count == 0)
            {
                this.Log(LogLevel.Debug, "CC{0}_ICF read with an empty capture FIFO", index);
                return (uint)cc_icof_icof_field[index].Value;
            }
            return captureFifos[index].Dequeue();
        }

        private void SetCaptureOverflowFlag(int index)
        {
            switch(index)
            {
                case 0:
                    if_icfof0_bit.Value = true;
                    break;
                case 1:
                    if_icfof1_bit.Value = true;
                    break;
                case 2:
                    if_icfof2_bit.Value = true;
                    break;
            }
        }

        private void UpdateInterrupts()
        {
            machine.ClockSource.ExecuteInLock(delegate
            {
                var irq = (ien_of_bit.Value && if_of_bit.Value)
                          || (ien_uf_bit.Value && if_uf_bit.Value)
                          || (ien_cc0_bit.Value && if_cc0_bit.Value)
                          || (ien_cc1_bit.Value && if_cc1_bit.Value)
                          || (ien_cc2_bit.Value && if_cc2_bit.Value)
                          || (ien_icfof0_bit.Value && if_icfof0_bit.Value)
                          || (ien_icfof1_bit.Value && if_icfof1_bit.Value)
                          || (ien_icfof2_bit.Value && if_icfof2_bit.Value);
                IRQ.Set(irq);
            });
        }

        private void StartCommand()
        {
            if(!en_en_bit.Value)
            {
                this.Log(LogLevel.Error, "Timer is disabled, ignoring START");
                return;
            }

            var wasRunning = timerIsRunning;
            timerIsRunning = true;
            EnsureHostAllClockHooks();
            RestartTimer(true);
            if(!timerIsRunning)
            {
                return;
            }

            // A run starts from the CC_CFG.COIST level; an up-counting PWM channel then starts
            // its first period.
            for(var i = 0; i < NumberOfChannels; i++)
            {
                if(cc_cfg_mode_field[i].Value == CC_CFG_MODE.PWM && CountingUp)
                {
                    DriveOutput(i, PwmLevel(i, 0));
                }
                else if(!wasRunning)
                {
                    DriveOutput(i, IdleOutputLevel(i));
                }
            }
        }

        private void StopCommand()
        {
            timerIsRunning = false;
            timer.Enabled = false;
            foreach(var channel in channels)
            {
                channel.Reset();
            }
            DriveOutputsIdle();
        }

        private void OnTimerLimitReached()
        {
            // LimitTimer has already reloaded. Up-count reloads to 0; down-count reloads to TOP.
            if(CountingUp)
            {
                if_of_bit.Value = true;
            }
            else
            {
                if_uf_bit.Value = true;
            }

            // One DMA request per update event. The LDMA acts on the rising edge.
            overflowRequest.Set(true);
            overflowRequest.Set(false);

            // The buffered registers are latched before the interrupt is raised, so a handler
            // reads the values the new period runs with.
            TransferBufferedRegisters();
            UpdateInterrupts();

            if(cfg_osmen_bit.Value)
            {
                timerIsRunning = false;
                foreach(var channel in channels)
                {
                    channel.Reset();
                }
                return;
            }

            for(var i = 0; i < NumberOfChannels; i++)
            {
                BeginOutputPeriod(i);
            }
            RestartTimer(true);
        }

        // The update event copies each pending buffered register into its active register and
        // clears the valid flag, so a period or duty written mid-period takes effect at a period
        // boundary.
        private void TransferBufferedRegisters()
        {
            if(status_topbv_bit.Value)
            {
                this.Log(LogLevel.Noisy, "Update event: TOPB 0x{0:X} -> TOP", topb_topb_field.Value);
                top_top_field.Value = topb_topb_field.Value;
                status_topbv_bit.Value = false;
            }

            for(var i = 0; i < NumberOfChannels; i++)
            {
                if(!ocbValidBits[i].Value)
                {
                    continue;
                }
                this.Log(LogLevel.Noisy, "Update event: CC{0}_OCB 0x{1:X} -> CC{0}_OC", i, cc_ocb_ocb_field[i].Value);
                cc_oc_oc_field[i].Value = cc_ocb_ocb_field[i].Value;
                ocbValidBits[i].Value = false;
            }
        }

        // Compare outputs.
        //
        // PWM mode, counting up: the output is asserted when the counter wraps to 0 and deasserted
        // at the compare match, so it is high for OC counts of each period. OC == 0 never asserts
        // (0%) and OC >= TOP never deasserts (100%); neither produces an edge. The single-count
        // low pulse that OC == TOP would give is not driven. PWM mode counting down raises the
        // compare flags only.
        //
        // Output-compare mode: CMOA acts at the compare match, COFOA at overflow, CUFOA at
        // underflow. A compare value equal to TOP matches on the last count of the period, so
        // CMOA = TOGGLE with OC == TOP toggles once per period.
        //
        // The level tracked here is the compare output before CC_CTRL.OUTINV; the pin sees it
        // inverted when OUTINV is set. A channel in OFF or input-capture mode keeps its level.
        // The dead-time insertion unit and PRS output control are not modelled.
        private void BeginOutputPeriod(int index)
        {
            switch(cc_cfg_mode_field[index].Value)
            {
                case CC_CFG_MODE.PWM:
                    if(CountingUp)
                    {
                        DriveOutput(index, PwmLevel(index, 0));
                    }
                    break;
                case CC_CFG_MODE.OUTPUTCOMPARE:
                    ApplyOutputAction(index, CountingUp ? (OutputAction)cc_ctrl_cofoa_field[index].Value
                                                        : (OutputAction)cc_ctrl_cufoa_field[index].Value);
                    break;
                default:
                    break;
            }
        }

        private void OnCompareMatch(uint index)
        {
            SetCompareFlag(index);

            var channel = (int)index;
            switch(cc_cfg_mode_field[channel].Value)
            {
                case CC_CFG_MODE.PWM:
                    if(CountingUp && cc_oc_oc_field[channel].Value < TopValue())
                    {
                        DriveOutput(channel, false);
                    }
                    break;
                case CC_CFG_MODE.OUTPUTCOMPARE:
                    ApplyOutputAction(channel, (OutputAction)cc_ctrl_cmoa_field[channel].Value);
                    break;
                default:
                    break;
            }
        }

        // The PWM output level of an up-counting channel when the counter is at `counter`.
        private bool PwmLevel(int index, ulong counter)
        {
            ulong compare = cc_oc_oc_field[index].Value;
            return compare != 0 && (counter < compare || compare >= TopValue());
        }

        private void ApplyOutputAction(int index, OutputAction action)
        {
            switch(action)
            {
                case OutputAction.Toggle:
                    DriveOutput(index, !outputLevels[index]);
                    break;
                case OutputAction.Clear:
                    DriveOutput(index, false);
                    break;
                case OutputAction.Set:
                    DriveOutput(index, true);
                    break;
                default:
                    break;
            }
        }

        private void DriveOutput(int index, bool level)
        {
            outputLevels[index] = level;
            Connections[index].Set(level ^ cc_ctrl_outinv_bit[index].Value);
        }

        // CC_CFG.COIST is the compare output's level while the counter is not running.
        private bool IdleOutputLevel(int index)
        {
            return cc_cfg_coist_bit[index].Value;
        }

        private void DriveOutputsIdle()
        {
            for(var i = 0; i < NumberOfChannels; i++)
            {
                DriveOutput(i, IdleOutputLevel(i));
            }
        }

        private void RestartTimer(bool restartFromInitial)
        {
            if(!timerIsRunning)
            {
                return;
            }

            if(cfg_mode_field.Value != CFG_MODE.UP && cfg_mode_field.Value != CFG_MODE.DOWN)
            {
                this.Log(LogLevel.Error, "TIMER MODE {0} is not supported", cfg_mode_field.Value);
                timerIsRunning = false;
                timer.Enabled = false;
                return;
            }

            if(cfg_clksel_field.Value != CFG_CLKSEL.PRESCEM01GRPACLK)
            {
                this.Log(LogLevel.Error, "TIMER CLKSEL {0} is not supported", cfg_clksel_field.Value);
                timerIsRunning = false;
                timer.Enabled = false;
                return;
            }

            if(!en_en_bit.Value)
            {
                timerIsRunning = false;
                timer.Enabled = false;
                return;
            }

            ulong top = TopValue();
            uint currentValue = restartFromInitial ? (CountingUp ? 0 : (uint)top) : TimerCounter;
            if(currentValue > top)
            {
                // TOP was lowered below the count. The period restarts from its initial value.
                this.Log(LogLevel.Debug, "CNT 0x{0:X} is past TOP 0x{1:X}; restarting the period", currentValue, top);
                currentValue = CountingUp ? 0 : (uint)top;
            }

            timer.Enabled = false;
            timer.Frequency = CounterFrequency();
            timer.Direction = CountingUp ? Direction.Ascending : Direction.Descending;
            // Up-count holds every value from 0 through TOP, so the exclusive period is TOP + 1.
            // Down-count reloads to TOP, so the period is TOP.
            timer.Limit = CountingUp ? top + 1 : (top == 0 ? 1 : top);
            this.Log(LogLevel.Noisy, "SET timer limit to {0}", timer.Limit);
            timer.Value = currentValue;
            if(hostAllClocksGated)
            {
                frozenCounter = currentValue;
            }
            else
            {
                timer.Enabled = true;
            }

            ArmCompareChannels(currentValue);
        }

        private void ArmCompareChannels(uint counter)
        {
            ulong top = TopValue();
            foreach(var channel in channels)
            {
                channel.Arm(counter, top, timer.Direction);
            }
        }

        private void SetCompareFlag(uint index)
        {
            switch(index)
            {
                case 0:
                    if_cc0_bit.Value = true;
                    break;
                case 1:
                    if_cc1_bit.Value = true;
                    break;
                case 2:
                    if_cc2_bit.Value = true;
                    break;
                default:
                    this.Log(LogLevel.Error, "Compare channel {0} is outside 0..2", index);
                    return;
            }
            UpdateInterrupts();
        }

        // TOP is the inclusive maximum. A counter of the configured width cannot exceed 2^width - 1.
        private ulong TopValue()
        {
            ulong widthLimit = counterCeiling - 1;
            ulong top = top_top_field.Value;
            return top < widthLimit ? top : widthLimit;
        }

        // TIMERn_CFG.PRESC: the selected clock is divided by PRESC + 1.
        private ulong CounterFrequency()
        {
            ulong source = SourceFrequency;
            if(source == 0)
            {
                if(clockTreeCmu != null)
                {
                    // EM01GRPACLK is disabled. A LimitTimer cannot run at 0 Hz, so the counter
                    // all but stands still.
                    return 1;
                }
                this.Log(LogLevel.Error, "Timer has no CMU; using 1 Hz");
                return 1;
            }

            ulong divisor = (ulong)cfg_presc_field.Value + 1;
            ulong frequency = source / divisor;
            if(frequency == 0)
            {
                this.Log(LogLevel.Error, "Prescaled timer clock is 0 Hz; using 1 Hz");
                return 1;
            }
            return frequency;
        }

        private bool CountingUp => cfg_mode_field.Value != CFG_MODE.DOWN;

        private void EnsureHostAllClockHooks()
        {
            if(hostAllClockHooksReady)
            {
                return;
            }
            var emu = machine.GetPeripheralsOfType<ISiLabs_Emu>().FirstOrDefault();
            if(emu == null)
            {
                return;
            }
            emu.AddEnterHostAllClockGateHook(EnterHostAllClockGate);
            emu.AddExitHostAllClockGateHook(ExitHostAllClockGate);
            hostAllClockHooksReady = true;
            hostAllClocksGated = !emu.HostAllClocksEnabled;
        }

        private void EnterHostAllClockGate()
        {
            if(hostAllClocksGated)
            {
                return;
            }
            if(timerIsRunning && timer.Enabled)
            {
                TrySyncTime();
                frozenCounter = (uint)timer.Value;
                timer.Enabled = false;
            }
            hostAllClocksGated = true;
            foreach(var channel in channels)
            {
                channel.SetClockGated(true);
            }
        }

        private void ExitHostAllClockGate()
        {
            if(!hostAllClocksGated)
            {
                return;
            }
            var resume = frozenCounter;
            hostAllClocksGated = false;
            foreach(var channel in channels)
            {
                channel.SetClockGated(false);
            }
            if(!timerIsRunning)
            {
                return;
            }
            timer.Value = resume;
            timer.Enabled = true;
            ArmCompareChannels(resume);
        }

        private bool TrySyncTime()
        {
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu))
            {
                cpu.SyncTime();
                return true;
            }
            return false;
        }

        private LimitTimer timer;
        private CompareChannel[] channels;
        private bool timerIsRunning = false;
        private bool hostAllClockHooksReady;
        private bool hostAllClocksGated;
        private uint frozenCounter;
        // The generated `cmu` answers the default clock lookup; this one is the opt-in clock tree source.
        private SiLabs_CMU_3 clockTreeCmu;
        private GPIO overflowRequest;
        private bool[] outputLevels;
        private IFlagRegisterField[] ocbValidBits;
        private Queue<uint>[] captureFifos;
        private bool[] captureInputLevel;
        private readonly uint fixedFrequency;
        private readonly ulong counterCeiling = 1UL << 16;
        private const int NumberOfChannels = 3;
        private const int OverflowRequestOutput = NumberOfChannels;
        private const int CaptureFifoDepth = 2;

        // CMOA, COFOA and CUFOA are separately generated enums with identical encodings.
        private enum OutputAction
        {
            None = 0,
            Toggle = 1,
            Clear = 2,
            Set = 3,
        }

        private class CompareChannel
        {
            public CompareChannel(IMachine machine, SiLabs_TIMER_1 parent, uint index)
            {
                this.parent = parent;
                this.index = index;
                timer = new LimitTimer(machine.ClockSource, 1, parent, $"timer-cc{index}", 0xFFFFFFFFUL, direction: Direction.Ascending,
                                       enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true);
                timer.LimitReached += OnCompare;
            }

            public void SetClockGated(bool gated)
            {
                if(gated)
                {
                    if(timer.Enabled)
                    {
                        compareRunning = true;
                        timer.Enabled = false;
                    }
                    return;
                }
                if(compareRunning)
                {
                    compareRunning = false;
                    timer.Enabled = true;
                }
            }

            public void Arm(uint value, ulong top, Direction direction)
            {
                timer.Enabled = false;
                if(!parent.timerIsRunning || parent.hostAllClocksGated)
                {
                    return;
                }

                var mode = parent.cc_cfg_mode_field[(int)index].Value;
                if(mode != CC_CFG_MODE.OUTPUTCOMPARE && mode != CC_CFG_MODE.PWM)
                {
                    return;
                }

                ulong compare = parent.cc_oc_oc_field[(int)index].Value;
                if(compare > top)
                {
                    if(mode == CC_CFG_MODE.PWM)
                    {
                        // A PWM compare value above TOP is the 100% duty setting.
                        return;
                    }
                    parent.Log(LogLevel.Error, "CC{0}: OC 0x{1:X} is greater than TOP 0x{2:X}, compare will not match", index, compare, top);
                    return;
                }

                ulong delay = 0;
                if(direction == Direction.Ascending && compare > value)
                {
                    delay = compare - value;
                }
                else if(direction == Direction.Descending && compare < value)
                {
                    delay = value - compare;
                }
                if(delay == 0)
                {
                    return;
                }

                timer.Frequency = parent.CounterFrequency();
                timer.Limit = delay;
                timer.Enabled = true;
            }

            public void Reset()
            {
                timer.Enabled = false;
                compareRunning = false;
            }

            private void OnCompare()
            {
                timer.Enabled = false;
                parent.OnCompareMatch(index);
            }

            private bool compareRunning;
            private readonly SiLabs_TIMER_1 parent;
            private readonly uint index;
            private readonly LimitTimer timer;
        }
    }
}
