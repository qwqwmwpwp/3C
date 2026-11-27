using System;
using UnityEngine.Profiling;

namespace ThreeC.CharacterBehavior
{
    // 主线程逐帧校验已发布轨迹，按需冻结完整快照，并用最新提交的状态验收在途任务。
    internal sealed class CharacterMovementPredictor
    {
#if UNITY_EDITOR
        internal readonly CharacterForecastDiagnostics Diagnostics = new CharacterForecastDiagnostics();
#endif
        internal readonly CharacterMovementForecast Output = new CharacterMovementForecast();
        private readonly CharacterForecastGeometry geometry;
        private readonly CharacterSimulationContinuity continuity;
        private readonly Func<CharacterBehaviorFlow> createFrameCopy;
        private readonly Func<bool, Func<CharacterBehaviorFlow>> captureTaskFactory;
        private CharacterBehaviorFlow latestBehavior;
        private CharacterForecastFrame latest, publishedFrame;
        private CharacterForecastWork active, retired;
        private long nextVersion, publishedVersion;
        private int revision;
        private bool obsolete, completed, suppressed, invalidSampling;
        private ForecastRefreshReason dirty;
        private double controlReturnsAt = double.NegativeInfinity;
        private float forcedBraking;
        internal bool HasFrameAssumptions => Output.Available || active != null && !obsolete;

        // 预演与独立调用者可让每个行为副本沿用原有工厂。
        internal CharacterMovementPredictor(Func<CharacterBehaviorFlow> createFrameCopy, CharacterForecastGeometry geometry,
            Func<CharacterBehaviorFlow> createCopy, CharacterSimulationContinuity continuity = null)
            : this(createFrameCopy, geometry, _ => createCopy, continuity) { }

        // 分别绑定帧末副本和本次任务的数据捕获策略，工厂只在主线程执行。
        internal CharacterMovementPredictor(Func<CharacterBehaviorFlow> createFrameCopy, CharacterForecastGeometry geometry,
            Func<bool, Func<CharacterBehaviorFlow>> captureTaskFactory, CharacterSimulationContinuity continuity)
        {
            this.createFrameCopy = createFrameCopy;
            this.geometry = geometry;
            this.continuity = continuity;
            this.captureTaskFactory = captureTaskFactory;
        }

        // 记录击退的控制归还时刻，并立即撤销受击前的预测。
        internal void ConfirmKnockback(double now, float seconds, float deceleration)
        {
            bool takingControl = now >= controlReturnsAt;
            controlReturnsAt = now + InputMath.NonNegative(seconds, 0f);
            forcedBraking = InputMath.NonNegative(deceleration, 0f);
            Invalidate(ForecastRefreshReason.Knockback, takingControl && controlReturnsAt > now
                ? CharacterForecastRevision.Takeover : CharacterForecastRevision.Invalidated);
        }

        // 提交新控制时段并撤回旧任务和样本，返回内置消费者完成清理后应发布的通知。
        internal CharacterForecastRevision PrepareBoundary(bool suppress, double returnsAt, float braking, CharacterMotionBoundaryKind kind)
        {
            RetireActive();
            latest = publishedFrame = default;
            suppressed = suppress;
            controlReturnsAt = returnsAt;
            forcedBraking = braking;
#if UNITY_EDITOR
            Diagnostics.Reset();
#endif
            var reason = suppress ? ForecastRefreshReason.Suppressed : kind == CharacterMotionBoundaryKind.Knockback
                ? ForecastRefreshReason.Knockback : ForecastRefreshReason.ExternalState;
            RequestRefresh(reason, false);
            Output.ClearSamples();
            return suppress ? CharacterForecastRevision.Withdraw : kind == CharacterMotionBoundaryKind.Knockback
                ? CharacterForecastRevision.Takeover : CharacterForecastRevision.Reset;
        }

        // 记录本帧刷新原因。方向、按下、物理反馈均在帧末通过同一入口启动计算。
        internal void RequestRefresh(ForecastRefreshReason reason, bool clearPublished = true)
        {
            revision++;
            dirty = reason;
            obsolete = active != null;
#if UNITY_EDITOR
            Diagnostics.ChangeAssumption();
#endif
            if (clearPublished && Output.Available) Output.Clear(CharacterForecastRevision.Invalidated);
        }

        // 外部接管与连续性边界应立即通知轨迹观察者。
        internal void Invalidate(ForecastRefreshReason reason, CharacterForecastRevision change = CharacterForecastRevision.Invalidated)
        {
            RequestRefresh(reason, false);
            Output.Clear(change);
        }

        // 模拟的外部接管立即停止发布；交还后由下一次完整帧末提交恢复推演。
        internal void Suppress(bool value)
        {
            if (value == suppressed) return;
            suppressed = value;
            RetireActive();
            latest = default;
            Invalidate(value ? ForecastRefreshReason.Suppressed : ForecastRefreshReason.Resumed,
                value ? CharacterForecastRevision.Withdraw : CharacterForecastRevision.Reset);
        }

        // 保留预测启用语义，空的已撤回结果由动画查询按各自样本回退。
        internal CharacterMovementForecast PublishedForAnimation(CharacterForecastSettings settings) =>
            settings.Enabled && settings.DriveAnimation ? Output : null;

        // 动画采集前检查输入、动作与连续运动参数，撤回已失效的假设。
        internal void CheckFrameAssumptions(double time, in CharacterBehaviorFrameResult behavior, in CharacterInputSnapshot held,
            in CharacterForecastTuning tuning)
        {
            ForecastRefreshReason reason = Output.Available
                ? CharacterForecastValidity.ChangedInput(publishedFrame, held) : ForecastRefreshReason.None;
            if (reason == ForecastRefreshReason.None && Output.Available)
                reason = CharacterForecastValidity.CompareBehavior(Output, time, publishedFrame.Window.Step, behavior);
            if (reason == ForecastRefreshReason.None && Output.Available && !publishedFrame.Observation.Tuning.Matches(tuning))
                reason = ForecastRefreshReason.Settings;
            if (reason != ForecastRefreshReason.None)
            {
                dirty = reason;
                int before = revision;
                Output.Clear(CharacterForecastRevision.Invalidated);
                if (revision != before) return; // 回调已改变模拟连续性或预测请求，旧观测不能再校验新的任务。
            }
            if (active != null && !obsolete)
            {
                reason = CharacterForecastValidity.ChangedInput(active.Frame, held);
                if (reason == ForecastRefreshReason.None && !active.Frame.Observation.Tuning.Matches(tuning))
                    reason = ForecastRefreshReason.Settings;
                if (reason != ForecastRefreshReason.None) RequestRefresh(reason, false);
            }
        }

        // 空闲帧先校验轻量观测；需要调度或有在途任务时才冻结命令、配置与行为种子。
        internal void SubmitObservation(in CharacterForecastObservation current, CharacterRigidbodyBlackboard commands,
            CharacterMovementSettings movement, in CharacterDriveParameters drive, CharacterBehaviorFlow live, bool threaded)
        {
            if (active == null && retired == null && Output.Available && dirty == ForecastRefreshReason.None &&
                current.Window.Steps > 0 && !RefreshDue(current) &&
                CharacterForecastValidity.Changed(publishedFrame, current, controlReturnsAt, forcedBraking) == ForecastRefreshReason.None &&
                CharacterForecastValidity.ValidatePublished(Output, current) == ForecastRefreshReason.None)
            {
                nextVersion++;
#if UNITY_EDITOR
                Output.NotifyFrameSubmitted();
#endif
                return;
            }
            int beforeCapture = revision;
            CharacterForecastFrame frame;
            using (continuity?.Protect() ?? default)
                frame = new CharacterForecastFrame(current, commands, movement, drive);
            if (revision == beforeCapture && !live.DispatchCancelled)
                SubmitFrame(frame, live, threaded);
        }

        // 常规刷新间隔到期或已发布窗口不足时，都需要重新从当前时刻推演。
        private bool RefreshDue(in CharacterForecastObservation current) =>
            current.Time + 0.000001 >= publishedFrame.Time + current.Window.Interval ||
            !Output.EnvironmentLimited && current.Time + current.Window.Horizon > Output.StartTime + Output.ValidSeconds;

        // 接受主线程已完整冻结的帧，为它分配版本并保留可独立推进的行为副本。
        internal void SubmitFrame(in CharacterForecastFrame frame, CharacterBehaviorFlow live, bool threaded)
        {
            if (suppressed) return;
            if (frame.Window.Steps == 0)
            {
                if (!invalidSampling)
                {
                    invalidSampling = true;
                    Release();
                }
                return;
            }
            if (invalidSampling) dirty = ForecastRefreshReason.Settings;
            invalidSampling = false;
            int beforeCopy = revision;
            try
            {
                using (continuity?.Protect() ?? default)
                {
                    latestBehavior ??= createFrameCopy();
                    if (!live.DispatchCancelled) live.CopyStateTo(latestBehavior);
                    if (live.DispatchCancelled) { latestBehavior = null; return; }
                }
                if (revision != beforeCopy) return;
            }
            catch
            {
                // 未完成的帧没有发布资格；下一帧以相同定义策略重建目标。
                latestBehavior = null;
                latest = default;
                RetireActive();
                dirty = ForecastRefreshReason.Retry;
                throw;
            }
            latest = frame.ForSubmission(++nextVersion, controlReturnsAt, forcedBraking);
#if UNITY_EDITOR
            int before = revision;
            Output.NotifyFrameSubmitted();
            if (revision != before) return;
#endif
            ScheduleLatest(threaded);
        }

        // 每次通知都覆盖尚未启动的待办；旧任务若仍可信可先发布，再从最新版本补算窗口。
        private void ScheduleLatest(bool threaded)
        {
            int before = revision;
            if (Output.Available)
            {
                var change = CharacterForecastValidity.Changed(publishedFrame, latest.Observation, latest.ControlReturnsAt, latest.ForcedBraking);
                if (change == ForecastRefreshReason.None) change = CharacterForecastValidity.ValidatePublished(Output, latest.Observation);
                if (change != ForecastRefreshReason.None)
                {
                    dirty = change;
                    Output.Clear(CharacterForecastRevision.Invalidated);
                    if (revision != before) return;
                }
            }
            if (active != null)
            {
                var change = CharacterForecastValidity.Changed(active.Frame, latest.Observation, latest.ControlReturnsAt, latest.ForcedBraking);
                if (change == ForecastRefreshReason.None && latest.Time > active.Frame.Time + active.Frame.Window.Step * (double)active.Frame.Window.Steps)
                    change = ForecastRefreshReason.Expired;
                if (change != ForecastRefreshReason.None || !threaded)
                {
                    obsolete = true;
                    if (change != ForecastRefreshReason.None) dirty = change;
                }
                Pump();
                if (!completed) return;
                Collect();
                if (revision != before) return;
            }
            if (retired != null)
            {
                if (retired.Released) retired = null;
                else return;
            }
            var reason = dirty;
            if (reason == ForecastRefreshReason.None)
            {
                if (!Output.Available) reason = ForecastRefreshReason.Initial;
                else if (RefreshDue(latest.Observation))
                    reason = ForecastRefreshReason.Scheduled;
                else return;
            }
            dirty = ForecastRefreshReason.None;
            StartLatest(threaded, reason);
        }

        // 调度后台任务时复制独立种子，同步模式也从相同的帧末黑板推演。
        private void StartLatest(bool threaded, ForecastRefreshReason reason)
        {
#if UNITY_EDITOR
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
#endif
            var source = latest;
            var behavior = latestBehavior;
            int startRevision = revision;
            bool background = threaded && !source.Origin.DynamicContact;
            CharacterBehaviorFlow seed, simulation = null;
            try
            {
                // 工厂、状态复制和定义重绑均可调用扩展；它们须在角色生命周期稳定时成组完成。
                using (continuity?.Protect() ?? default)
                {
                    var createCopy = captureTaskFactory(behavior.SupportsSharedForecastAttack);
                    if (!CanStart(source.Version, startRevision)) return;
                    seed = createCopy();
                    if (!CanStart(source.Version, startRevision)) return;
                    behavior.CopyStateTo(seed);
                    if (!CanStart(source.Version, startRevision)) return;
                    if (background) simulation = createCopy();
                }
            }
            catch
            {
                if (CanStart(source.Version, startRevision)) dirty = ForecastRefreshReason.Retry;
                throw;
            }

            // 退出保护可能立即执行重生或暂停；此后只用已经核准的本地帧构造查询与任务。
            if (!CanStart(source.Version, startRevision)) return;
            geometry.Begin(source.Drive.Settings);
            if (background)
            {
                active = new CharacterForecastWork(seed, simulation, geometry, source, reason);
                obsolete = completed = false;
#if UNITY_EDITOR
                active.RecordSchedule(ticks, allocated);
#endif
                return;
            }
            Profiler.BeginSample("3C.MovementForecast");
            try
            {
                var result = new CharacterMovementForecast();
                using (continuity?.Protect() ?? default)
                    CharacterForecastRollout.Simulate(seed, geometry, result, source, reason);
                if (!CanStart(source.Version, startRevision) || !AcceptResult(result, source)) return;
            }
            finally { Profiler.EndSample(); }
#if UNITY_EDITOR
            if (CanStart(source.Version, startRevision) && Output.Available)
                Diagnostics.Published(Output, source.Window.Step, System.Diagnostics.Stopwatch.GetTimestamp() - ticks,
                    GC.GetAllocatedBytesForCurrentThread() - allocated);
#endif
        }

        // 扩展回调或其延期生命周期操作撤销了帧、接管了预测或启动了新任务时，旧提交必须停止。
        private bool CanStart(long sourceVersion, int startRevision) =>
            revision == startRevision && sourceVersion != 0 && latest.Version == sourceVersion && active == null &&
            !suppressed && !invalidSampling && continuity?.Pending != true;

        // 主线程仅推进已就绪的批次；最终发布只能在完整的帧末黑板提交后发生。
        private void Pump()
        {
            if (active == null || completed) return;
            try { completed = active.Poll(obsolete); }
            catch
            {
                RetireActive();
                dirty = ForecastRefreshReason.Retry;
                throw;
            }
        }

        // 将完工任务与最新提交和沿途实测核对，避免老版本倒盖已发布的新结果。
        private void Collect()
        {
            var work = active;
            active = null;
            completed = false;
            try
            {
                if (obsolete || !AcceptResult(work.Result, work.Frame, work)) return;
#if UNITY_EDITOR
                Diagnostics.Published(Output, work.Frame.Window.Step, work.MainTicks, work.MainAllocated, work.WorkerTicks, work.ElapsedTicks, work.Batches);
#endif
            }
            finally { work.Dispose(); }
        }

        // 以最新提交验收同步或异步轨迹，异步任务还要比对计算期间的实测记录。
        private bool AcceptResult(CharacterMovementForecast result, in CharacterForecastFrame source, CharacterForecastWork work = null)
        {
            if (source.Version <= publishedVersion || latest.Version == 0 || source.Generation != latest.Generation) return false;
            var difference = work == null ? CharacterForecastValidity.ValidatePublished(result, latest.Observation)
                : CharacterForecastValidity.ValidateCompleted(result, work.Observed, latest);
            if (difference != ForecastRefreshReason.None)
            {
                if (work == null)
                {
                    if (Output.Available) Invalidate(difference);
                }
                else
                {
                    dirty = difference;
                    if (result.EnvironmentLimited && Output.Available) Output.Clear(CharacterForecastRevision.Invalidated);
                }
                return false;
            }
            publishedFrame = source;
            publishedVersion = source.Version;
            Output.Publish(result);
            return true;
        }

        // 记录沿途已解步进行回验，物理偏差会在动画读取前立即撤回旧输出。
        internal void ObserveSolvedStep(in CharacterForecastOrigin origin)
        {
#if UNITY_EDITOR
            Diagnostics.Observe(origin.Number, origin.Pose);
#endif
            if (!obsolete && active != null) active.Observed.Add((origin.Time, origin.Pose));
            var difference = CharacterForecastValidity.CompareAt(Output, origin.Time, origin.Pose);
            if (difference != ForecastRefreshReason.None)
            {
                // 正在重算的任务可能出自更晚的起点，由它自己的沿途实测决定发布资格。
                dirty = difference;
                Output.Clear(CharacterForecastRevision.Invalidated);
            }
        }

        // 将被撤销的任务交给主线程回收器，在其真正释放缓冲之前不启动本角色的下一任务。
        private void RetireActive()
        {
            if (active == null) return;
            retired = active;
            active = null;
            completed = obsolete = false;
            CharacterForecastRetirement.Retire(retired);
        }

        // 模拟边界立即清除待办和发布资格；已经启动的工作安全退役。
        internal void Release()
        {
            RetireActive();
            latest = publishedFrame = default;
            if (dirty != ForecastRefreshReason.SimulationBoundary || Output.Available)
                Invalidate(ForecastRefreshReason.SimulationBoundary, CharacterForecastRevision.Reset);
            else revision++;
        }

        // 在暂停、重置或角色离场边界清理运行中的预测和上一代诊断。
        internal void EndContinuity()
        {
            Release();
#if UNITY_EDITOR
            Diagnostics.Reset();
#endif
        }
    }
}
