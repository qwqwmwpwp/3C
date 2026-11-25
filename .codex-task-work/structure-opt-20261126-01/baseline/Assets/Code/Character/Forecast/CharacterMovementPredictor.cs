using System;
using UnityEngine.Profiling;

namespace ThreeC.CharacterBehavior
{
    // 主线程每帧提交一份完整的预测黑板，最多推进一个任务，并以最近提交的状态校验发布。
    internal sealed class CharacterMovementPredictor
    {
#if UNITY_EDITOR
        internal readonly CharacterForecastDiagnostics Diagnostics = new CharacterForecastDiagnostics();
#endif
        internal readonly CharacterMovementForecast Output = new CharacterMovementForecast();
        private readonly CharacterForecastGeometry geometry;
        private readonly Func<CharacterBehaviorFlow> createFrameCopy;
        private readonly Func<CharacterBehaviorFlow> createCopy;
        private CharacterBehaviorFlow latestBehavior;
        private CharacterForecastFrame latest, publishedFrame;
        private CharacterForecastWork active, retired;
        private long nextVersion, publishedVersion;
        private int revision;
        private bool obsolete, completed, suppressed, invalidSampling;
        private ForecastRefreshReason dirty;
        private double controlReturnsAt = double.NegativeInfinity;
        private float forcedBraking;

        // 分别绑定帧末行为副本和任务行为副本的创建策略。
        internal CharacterMovementPredictor(Func<CharacterBehaviorFlow> createFrameCopy, CharacterForecastGeometry geometry,
            Func<CharacterBehaviorFlow> createCopy)
        {
            this.createFrameCopy = createFrameCopy;
            this.geometry = geometry;
            this.createCopy = createCopy;
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

        // 重生后丢弃前一代的外部控制时段。
        internal void ClearControlLock() => controlReturnsAt = double.NegativeInfinity;

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
        internal CharacterMovementForecast PublishedForAnimation(bool simulating, CharacterForecastSettings settings) =>
            simulating && settings.Enabled && settings.DriveAnimation ? Output : null;

        // 当帧行为与输入已经改变时，动画采集之前立即撤回不再成立的假设。
        internal void CheckFrameAssumptions(double time, in CharacterBehaviorFrameResult behavior, in CharacterInputSnapshot held)
        {
            ForecastRefreshReason reason = Output.Available
                ? CharacterForecastValidity.ChangedInput(publishedFrame, held) : ForecastRefreshReason.None;
            if (reason == ForecastRefreshReason.None && Output.Available)
                reason = CharacterForecastValidity.CompareBehavior(Output, time, publishedFrame.Window.Step, behavior);
            if (reason != ForecastRefreshReason.None)
            {
                dirty = reason;
                Output.Clear(CharacterForecastRevision.Invalidated);
            }
            if (active != null && !obsolete)
            {
                reason = CharacterForecastValidity.ChangedInput(active.Frame, held);
                if (reason != ForecastRefreshReason.None) RequestRefresh(reason, false);
            }
        }

        // 接受主线程已完整冻结的帧，为它分配版本并保留可独立推进的行为副本。
        internal void SubmitFrame(in CharacterForecastFrame frame, CharacterBehaviorFlow live, bool threaded,
            bool immediateAlignment = false, bool frameEnd = true)
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
            try
            {
                latestBehavior ??= createFrameCopy();
                live.CopyStateTo(latestBehavior);
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
            if (frameEnd) Output.NotifyFrameSubmitted();
            if (revision != before) return;
#endif
            // 跟随请求的同步推演只访问主线程几何；已退役任务持有独立的数值快照和查询缓冲。
            if (immediateAlignment) RetireActive();
            ScheduleLatest(threaded && !immediateAlignment, immediateAlignment);
        }

        // 每次通知都覆盖尚未启动的待办；旧任务若仍可信可先发布，再从最新版本补算窗口。
        private void ScheduleLatest(bool threaded, bool immediateAlignment)
        {
            int before = revision;
            if (Output.Available)
            {
                var change = CharacterForecastValidity.Changed(publishedFrame, latest);
                if (change == ForecastRefreshReason.None) change = CharacterForecastValidity.ValidatePublished(Output, latest);
                if (change != ForecastRefreshReason.None)
                {
                    dirty = change;
                    Output.Clear(CharacterForecastRevision.Invalidated);
                    if (revision != before) return;
                }
            }
            if (active != null)
            {
                var change = CharacterForecastValidity.Changed(active.Frame, latest);
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
                else if (!immediateAlignment) return;
            }
            var reason = dirty;
            if (reason == ForecastRefreshReason.None)
            {
                if (!Output.Available) reason = ForecastRefreshReason.Initial;
                else if (immediateAlignment && latest.Time == publishedFrame.Time && latest.Origin.Number == publishedFrame.Origin.Number)
                    return; // 同一时刻的提前补发已通过上方校验，零刷新间隔也不在帧末重复推演。
                else if (latest.Time + 0.000001 >= publishedFrame.Time + latest.Window.Interval ||
                    !Output.EnvironmentLimited && latest.Time + latest.Window.Horizon > Output.StartTime + Output.ValidSeconds)
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
            geometry.Begin(latest.Drive.Settings);
            if (threaded && !latest.Origin.DynamicContact)
            {
                var seed = CopyTaskBehavior();
                active = new CharacterForecastWork(seed, createCopy(), geometry, latest, reason);
                obsolete = completed = false;
#if UNITY_EDITOR
                active.RecordSchedule(ticks, allocated);
#endif
                return;
            }
            Profiler.BeginSample("3C.MovementForecast");
            try
            {
                // 同步副本使用当前配置构造，包含该帧已解析的招式资源和连招表。
                int startRevision = revision;
                var source = latest;
                var synchronousBehavior = CopyTaskBehavior();
                var result = new CharacterMovementForecast();
                CharacterForecastRollout.Simulate(synchronousBehavior, geometry, result, source, reason);
                if (startRevision != revision || !AcceptResult(result, source)) return;
            }
            finally { Profiler.EndSample(); }
#if UNITY_EDITOR
            if (Output.Available && latest.Window.Step > 0f)
                Diagnostics.Published(Output, latest.Window.Step, System.Diagnostics.Stopwatch.GetTimestamp() - ticks,
                    GC.GetAllocatedBytesForCurrentThread() - allocated);
#endif
        }

        // 从完整的帧末副本建立任务种子，复制失败时直接废弃该目标。
        private CharacterBehaviorFlow CopyTaskBehavior()
        {
            try
            {
                var target = createCopy();
                latestBehavior.CopyStateTo(target);
                return target;
            }
            catch
            {
                dirty = ForecastRefreshReason.Retry;
                throw;
            }
        }

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
            var difference = work == null ? CharacterForecastValidity.ValidatePublished(result, latest)
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
