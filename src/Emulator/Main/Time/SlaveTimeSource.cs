//
// Copyright (c) 2010-2022 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Threading;

using Antmicro.Migrant;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Time
{
    /// <summary>
    /// Represents an intermediate entity that distribute interval granted from master time sources to other sinks.
    /// </summary>
    public class SlaveTimeSource : TimeSourceBase, ITimeSource, ITimeSink, IDisposable
    {
        /// <summary>
        /// Creates a new instance of <see cref="SlaveTimeSource">.
        /// </summary>
        public SlaveTimeSource()
        {
            locker = new object();
            TimePassed += HandleTimePassed;
        }

        /// <summary>
        /// Disposes this instance.
        /// </summary>
        public override void Dispose()
        {
            this.Trace("Disposing...");
            base.Dispose();
            base.Stop();
            lock(locker)
            {
                TimeHandle?.Dispose();
            }
            StopDispatcher();
            this.Trace("Disposed");
        }

        /// <summary>
        /// Pauses the execution of the time source. It can be resumed using <see cref="Resume"> method.
        /// </summary>
        public void Pause()
        {
            lock(locker)
            {
                this.Trace("Pausing...");
                if(!isStarted)
                {
                    this.Trace();
                    return;
                }
                if(isPaused)
                {
                    this.Trace();
                    return;
                }
                RequestStop();
                using(sync.HighPriority)
                {
                    stopwatch.Stop();
                    isPaused = true;
                    DeactivateSlavesSourceSide();
                }
                this.Trace("Paused");
            }
        }

        /// <summary>
        /// Resumes execution of the time source.
        /// </summary>
        public void Resume()
        {
            this.Trace("Resuming...");
            lock(locker)
            {
                using(sync.HighPriority)
                {
                    ActivateSlavesSourceSide();
                    isPaused = false;
                    stopwatch.Start();
                }
            }
            this.Trace("Resumed");
        }

        /// <summary>
        /// Name of the owner of this time source (e.g. the machine), used to name the dispatcher thread.
        /// </summary>
        /// <remarks>
        /// <see cref="Antmicro.Renode.Core.PseudorandomNumberGenerator"/> keeps one generator per thread name,
        /// so dispatcher threads of different slaves must have unique names that are stable across runs:
        /// otherwise concurrently running dispatchers share one non-thread-safe generator.
        /// It must be set before the dispatcher starts, i.e. before this slave is attached to a master.
        /// </remarks>
        public string OwnerName { get; set; }

        /// <see cref="ITimeSource.Domain">
        /// <remarks>
        /// If this slave is not attached to any master time source, the domain is null.
        /// </remarks>
        public override ITimeDomain Domain { get { return timeHandle?.TimeSource.Domain; } }

        /// <see cref="ITimeSink.TimeHandle">
        /// <remarks>
        /// If this time source is already connected to a master, old handle is disposed before accepting the new one.
        /// </remarks>
        public TimeHandle TimeHandle
        {
            get
            {
                return timeHandle;
            }

            set
            {
                lock(locker)
                {
                    StopDispatcher();
                    TimeHandle?.Dispose();
                    this.Trace("About to attach to the new master");
                    timeHandle = value;
                    timeHandle.PauseRequested += RequestStop;
                    timeHandle.StartRequested += HandleStartRequest;
                    ResetVirtualTime(timeHandle.TotalElapsedTime);
                    StartDispatcher();
                }
            }
        }

        private void HandleStartRequest()
        {
            this.Trace();
            lock(locker)
            {
                this.Trace();
                if(dispatcherThread == null)
                {
                    this.Trace();
                    // if the dispatcher is not started yet - start it
                    StartDispatcher();
                }
                else
                {
                    this.Trace();
                    // if the dispatcher is already running - set the restart flag
                    dispatcherStartRequested = true;
                }
            }
        }

        /// <summary>
        /// Provides the implementation of time-distribution among slaves.
        /// </summary>
        private void Dispatch()
        {
            var isLocked = false;
            try
            {
#if DEBUG
                using(this.TraceRegion("Dispatcher loop"))
#endif
                using(this.ObtainSourceActiveState())
                using(this.ObtainSinkActiveState())
                using(TimeDomainsManager.Instance.RegisterCurrentThread(() => new TimeStamp(TimeHandle.TimeSource.NearestSyncPoint, TimeHandle.TimeSource.Domain)))
                {
                    while(true)
                    {
                        try
                        {
                            while(isStarted)
                            {
                                WaitIfBlocked();
                                if(!DispatchInner())
                                {
                                    break;
                                }
                            }
                        }
                        catch(Exception e)
                        {
                            this.Trace(LogLevel.Error, $"Got an exception: {e.Message} {e.StackTrace}");
                            throw;
                        }

                        lock(locker)
                        {
                            if(!dispatcherStartRequested)
                            {
                                this.Trace();
                                // the `locker` is re-acquired here to
                                // make sure that dispose-related code of all usings
                                // is executed before setting `dispatcherThread` to
                                // null (what allows to start new dispatcher thread);
                                // otherwise there could be a race condition when
                                // new thread enters usings (e.g., activates source side)
                                // and then the old one exits them (deactivating source
                                // side as a result)
                                Monitor.Enter(locker, ref isLocked);
                                break;
                            }

                            dispatcherStartRequested = false;
                            this.Trace();
                        }
                    }
                }
            }
            finally
            {
                dispatcherThread = null;
                if(isLocked)
                {
                    this.Trace();
                    Monitor.Exit(locker);
                }
                this.Trace();
            }
        }

        private bool DispatchInner()
        {
            if(!TimeHandle.RequestTimeInterval(out var intervalGranted))
            {
                this.Trace("Time interval request interrupted");
                return false;
            }

            if(isPaused)
            {
                this.Trace("Handle paused");
                TimeHandle.ReportBackAndBreak(intervalGranted);
                return true;
            }

            var quantum = Quantum;
            this.Trace($"Current QUANTUM is {quantum.Ticks} ticks");
            var timeLeft = intervalGranted;

            while(waitingForSlave || (timeLeft >= quantum && isStarted))
            {
                waitingForSlave = false;
                var syncPointReached = InnerExecute(out var elapsed);
                timeLeft -= elapsed;
                if(!syncPointReached)
                {
                    // we should not ask for time grant since the current one is not finished yet
                    waitingForSlave = true;
                    TimeHandle.ReportBackAndBreak(timeLeft);
                    return true;
                }
            }

            TimeHandle.ReportBackAndContinue(timeLeft);
            return true;
        }

        private void StartDispatcher()
        {
            lock(locker)
            {
                if(dispatcherThread != null || TimeHandle == null)
                {
                    return;
                }

                isStarted = true;
                var threadName = OwnerName == null ? "SlaveTimeSource Dispatcher Thread" : $"SlaveTimeSource Dispatcher Thread ({OwnerName})";
                dispatcherThread = new Thread(Dispatch) { IsBackground = true, Name = threadName };
                dispatcherThread.Start();
            }
        }

        private void StopDispatcher()
        {
            Thread threadToJoin = null;
            lock(locker)
            {
                isStarted = false;
                if(dispatcherThread != null && Thread.CurrentThread.ManagedThreadId != dispatcherThread.ManagedThreadId)
                {
                    threadToJoin = dispatcherThread;
                }
            }
            threadToJoin?.Join();
        }

        private void HandleTimePassed(TimeInterval diff)
        {
            TimeHandle?.ReportProgress(diff);
        }

        [Transient]
        private Thread dispatcherThread;
        private TimeHandle timeHandle;
        private bool waitingForSlave;
        private bool dispatcherStartRequested;
        private readonly object locker;
    }
}