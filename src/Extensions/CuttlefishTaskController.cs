using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using AindPhysiologyFip;
using Bonsai;
using Bonsai.Harp;
using FipExtensions;
using Cuttlefish = AllenNeuralDynamics.CuttlefishFip;

/// <summary>
/// Single owner of every task command sent to the CuttlefishFip device. Each request is applied as
/// one uninterrupted script: Stop, wait for the running cycle to finish, ClearAllTasks, AddTask per
/// light source, Start. Scripts never overlap; while one runs only the newest pending request is
/// kept, so bursts of clicks or edits collapse into a single final apply.
/// Source1 = requested light source task (ITriggerLightSourceTask),
/// Source2 = calibrated light sources (power edits re-apply Fip mode),
/// Source3 = clear requests (Stop + ClearAllTasks, no Start).
/// </summary>
/// <remarks>
/// Why the waits: on the device, a Stop only takes effect once the core running the sequence finishes
/// its current cycle, and only then does it drain pending commands, adds before clears. A ClearAllTasks
/// and AddTask that both land in that window would leave the new task wiped. A Start that arrives before
/// queued adds are drained runs the old (empty) task list. AddTask itself is rejected while started.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Serializes all CuttlefishFip task commands: Stop, wait one cycle, ClearAllTasks, AddTask per light source, Start. Source1 = ITriggerLightSourceTask requests, Source2 = calibrated light sources, Source3 = clear requests.")]
public class CuttlefishTaskController
{
    private int stopMargin = 20;
    [Description("Extra time (ms) waited after Stop, on top of one full cycle of the running tasks.")]
    public int StopMargin { get { return stopMargin; } set { stopMargin = value; } }

    private int settleTime = 10;
    [Description("Time (ms) waited after ClearAllTasks and after each AddTask so the device drains its command queues in order.")]
    public int SettleTime { get { return settleTime; } set { settleTime = value; } }

    private int powerChangeDelay = 250;
    [Description("Quiet time (ms) after the last power edit before Fip mode is re-applied. Explicit requests are applied immediately.")]
    public int PowerChangeDelay { get { return powerChangeDelay; } set { powerChangeDelay = value; } }

    public IObservable<HarpMessage> Process(
        IObservable<ITriggerLightSourceTask> requests,
        IObservable<IDictionary<FipCameraSource, CalibratedLightSource>> lightSources,
        IObservable<Unit> clear)
    {
        return Observable.Create<HarpMessage>(observer =>
        {
            var worker = new Worker(observer, StopMargin, SettleTime, PowerChangeDelay);
            // Light sources first, so a replayed request already sees them.
            var subscriptions = new CompositeDisposable();
            subscriptions.Add(lightSources.Subscribe(worker.SetLightSources, observer.OnError));
            subscriptions.Add(requests.Subscribe(worker.Request, observer.OnError));
            subscriptions.Add(clear.Subscribe(_ => worker.Request(null), observer.OnError));
            subscriptions.Add(worker);
            worker.Start();
            return subscriptions;
        });
    }

    class Worker : IDisposable
    {
        readonly object gate = new object();
        readonly ManualResetEvent disposed = new ManualResetEvent(false);
        readonly IObserver<HarpMessage> observer;
        readonly int stopMargin;
        readonly int settleTime;
        readonly int powerChangeDelay;
        readonly Thread thread;

        // Desired state; a null request means "cleared" (stopped, no tasks).
        ITriggerLightSourceTask request;
        IDictionary<FipCameraSource, CalibratedLightSource> lightSources;
        bool dirty;
        DateTime notBefore;
        int lastCycleUs = -1; // unknown until the first apply

        public Worker(IObserver<HarpMessage> observer, int stopMargin, int settleTime, int powerChangeDelay)
        {
            this.observer = observer;
            this.stopMargin = Math.Max(0, stopMargin);
            this.settleTime = Math.Max(0, settleTime);
            this.powerChangeDelay = Math.Max(0, powerChangeDelay);
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "CuttlefishTaskController";
        }

        public void Start()
        {
            thread.Start();
        }

        public void Request(ITriggerLightSourceTask value)
        {
            lock (gate)
            {
                request = value;
                dirty = true;
                notBefore = DateTime.UtcNow;
                Monitor.PulseAll(gate);
            }
        }

        public void SetLightSources(IDictionary<FipCameraSource, CalibratedLightSource> value)
        {
            lock (gate)
            {
                lightSources = value;
                // Only Fip mode uses the calibrated duty cycles; other modes pick them up on their next request.
                if (request is FipModeLightSourceTask)
                {
                    dirty = true;
                    notBefore = DateTime.UtcNow.AddMilliseconds(powerChangeDelay);
                    Monitor.PulseAll(gate);
                }
            }
        }

        void Run()
        {
            while (true)
            {
                ITriggerLightSourceTask target;
                IDictionary<FipCameraSource, CalibratedLightSource> sources;
                lock (gate)
                {
                    while (true)
                    {
                        if (disposed.WaitOne(0)) return;
                        if (dirty)
                        {
                            var wait = notBefore - DateTime.UtcNow;
                            if (wait <= TimeSpan.Zero) break;
                            Monitor.Wait(gate, wait);
                        }
                        else Monitor.Wait(gate);
                    }
                    dirty = false;
                    target = request;
                    sources = lightSources;
                }

                try
                {
                    if (!Apply(target, sources)) return;
                }
                catch (Exception ex)
                {
                    observer.OnError(ex);
                    return;
                }
            }
        }

        // Returns false when disposed mid-script.
        bool Apply(ITriggerLightSourceTask target, IDictionary<FipCameraSource, CalibratedLightSource> sources)
        {
            var payloads = new List<byte[]>();
            var cycleUs = BuildTasks(target, sources, payloads);

            // 1. Stop, then let the device finish the cycle it is running.
            var runningCycleUs = lastCycleUs >= 0 ? lastCycleUs : FullFipCycleUs(sources);
            if (!Send(Cuttlefish.SetTaskState.FromPayload(MessageType.Write, Cuttlefish.TaskState.Stop))) return false;
            if (!Sleep(runningCycleUs / 1000 + 1 + stopMargin)) return false;
            lastCycleUs = 0;

            // 2. Clear everything.
            if (!Send(Cuttlefish.ClearAllTasks.FromPayload(MessageType.Write, EnableFlag.Enabled))) return false;
            if (!Sleep(settleTime)) return false;
            if (target == null || payloads.Count == 0) return true;

            // 3. Add the tasks, then 4. start.
            foreach (var payload in payloads)
            {
                if (!Send(Cuttlefish.AddTask.FromPayload(MessageType.Write, payload))) return false;
                if (!Sleep(settleTime)) return false;
            }
            if (!Send(Cuttlefish.SetTaskState.FromPayload(MessageType.Write, Cuttlefish.TaskState.Start))) return false;
            lastCycleUs = cycleUs;
            return true;
        }

        // Fills the AddTask payloads for the requested mode and returns the resulting cycle length (us).
        static int BuildTasks(
            ITriggerLightSourceTask target,
            IDictionary<FipCameraSource, CalibratedLightSource> sources,
            List<byte[]> payloads)
        {
            // Stop (and clear requests, target == null) leave the schedule stopped and empty: no tasks, no Start.
            if (target == null || target is StopLightSourceTask || sources == null) return 0;
            var cycleUs = 0;

            var continuous = target as ContinuousLightSourceTask;
            if (continuous != null)
            {
                CalibratedLightSource source;
                if (!sources.TryGetValue(continuous.Channel, out source) || source == null) return 0;
                var task = source.LightSource.Task;
                // Same settings the Continuous workflow branch used: fixed 10 kHz, no camera trigger, 10 ms period.
                payloads.Add(CreatePayload(task.LightSourcePort, continuous.Power, 10000, Ports.None, false, false, 10000, 0, 0, 0));
                return 10000;
            }

            var useCalibratedDuty = target is FipModeLightSourceTask;
            if (!useCalibratedDuty && !(target is OffLightSourceTask)) return 0;

            // Off is the Fip schedule (cameras still triggered) with the lights at zero duty.
            foreach (var pair in sources)
            {
                if (pair.Value == null) continue;
                var task = pair.Value.LightSource.Task;
                var duty = useCalibratedDuty ? pair.Value.CalibratedDutyCycle : 0;
                payloads.Add(CreatePayload(
                    task.LightSourcePort, duty, task.PwmFrequency, task.CameraPort,
                    task.EventsEnabled, task.MuteOutput,
                    task.Delta1, task.Delta2, task.Delta3, task.Delta4));
                cycleUs += task.Delta1 + task.Delta2 + task.Delta3 + task.Delta4;
            }
            return cycleUs;
        }

        static int FullFipCycleUs(IDictionary<FipCameraSource, CalibratedLightSource> sources)
        {
            var cycleUs = 0;
            if (sources == null) return cycleUs;
            foreach (var pair in sources)
            {
                if (pair.Value == null) continue;
                var task = pair.Value.LightSource.Task;
                cycleUs += task.Delta1 + task.Delta2 + task.Delta3 + task.Delta4;
            }
            return cycleUs;
        }

        // AddTask layout (device.yml): U32 IOPin, float DutyCycle, float Frequency, U32 OutputMask,
        // U8 Events, U8 Mute, U32 Delta1-4 (us). 34 bytes, little-endian, packed.
        static byte[] CreatePayload(
            Ports pwmPort, double dutyCycle, double frequency, Ports triggerPorts,
            bool events, bool mute, int delta1, int delta2, int delta3, int delta4)
        {
            var payload = new byte[34];
            BitConverter.GetBytes((uint)pwmPort).CopyTo(payload, 0);
            BitConverter.GetBytes((float)Math.Max(0.0, Math.Min(1.0, dutyCycle))).CopyTo(payload, 4);
            BitConverter.GetBytes((float)frequency).CopyTo(payload, 8);
            BitConverter.GetBytes((uint)triggerPorts).CopyTo(payload, 12);
            payload[16] = (byte)(events ? 1 : 0);
            payload[17] = (byte)(mute ? 1 : 0);
            BitConverter.GetBytes((uint)delta1).CopyTo(payload, 18);
            BitConverter.GetBytes((uint)delta2).CopyTo(payload, 22);
            BitConverter.GetBytes((uint)delta3).CopyTo(payload, 26);
            BitConverter.GetBytes((uint)delta4).CopyTo(payload, 30);
            return payload;
        }

        bool Send(HarpMessage message)
        {
            if (disposed.WaitOne(0)) return false;
            observer.OnNext(message);
            return true;
        }

        // Returns false when disposed while waiting.
        bool Sleep(int milliseconds)
        {
            return !disposed.WaitOne(Math.Max(0, milliseconds));
        }

        public void Dispose()
        {
            disposed.Set();
            lock (gate) Monitor.PulseAll(gate);
        }
    }
}
