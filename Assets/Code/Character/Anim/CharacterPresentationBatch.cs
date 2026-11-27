using Unity.Profiling;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>保存动画计划采集时的固定步结果与渲染输入，并允许用最新发布的预测更新本帧轨迹。</summary>
    internal readonly struct CharacterPresentationInput
    {
        internal readonly CharacterControllerFrameResult Frame;
        internal readonly CharacterBehaviorFrameResult Behavior;
        internal readonly CharacterAnimationMotion Motion;
        internal readonly CharacterMovementForecast Forecast;
        internal readonly CharacterPlaybackObservation Observation;
        internal readonly CharacterFootObservation Feet;
        internal readonly double Time;
        internal readonly float DeltaTime;
        internal readonly bool ValidGrant;
        internal readonly long GuardGrant;
        private readonly bool forecastEnabled;
        private readonly Vector2 windows;
        private readonly CharacterGroundTrajectory originalTrajectory; // 关闭预测时恢复采集固定步的动画投影。

        // 分别保存物理观测与当帧动作，在角色时间轴上查询已发布的预测。
        internal CharacterPresentationInput(CharacterControllerFrameResult frame, CharacterBehaviorFrameResult behavior, CharacterAnimationMotion motion,
            CharacterMovementForecast published, CharacterPlaybackObservation observation, CharacterFootObservation feet,
            float dt, double now, bool validGrant, long grant, Vector2 windows)
        {
            this.windows = windows;
            Frame = frame;
            Behavior = behavior;
            forecastEnabled = published != null;
            Forecast = published?.Snapshot();
            originalTrajectory = motion.Trajectory;
            Motion = new CharacterAnimationMotion(motion, CharacterForecastQuery.Project(frame, originalTrajectory, Forecast, now, forecastEnabled, windows));
            Observation = observation;
            Feet = feet;
            DeltaTime = dt;
            Time = now;
            ValidGrant = validGrant;
            GuardGrant = grant;
        }

        // 只更新轨迹和预测，沿用这次采集的时间、速度和播放观测。
        private CharacterPresentationInput(in CharacterPresentationInput captured,
            CharacterMovementForecast published, CharacterMovementForecast snapshot)
        {
            this = captured;
            forecastEnabled = published != null;
            Forecast = snapshot;
            var trajectory = CharacterForecastQuery.Project(Frame, originalTrajectory, snapshot, Time, forecastEnabled, windows);
            Motion = new CharacterAnimationMotion(captured.Motion, trajectory);
        }

        // 区分预测关闭、已撤回和有效发布，发现变化时用原始固定步重建表现轨迹。
        internal CharacterPresentationInput RefreshForecast(CharacterMovementForecast published, out bool changed)
        {
            var snapshot = published?.Snapshot();
            changed = forecastEnabled != (published != null) || !ReferenceEquals(snapshot, Forecast);
            return changed ? new CharacterPresentationInput(this, published, snapshot) : this;
        }

        // 用同一已发布预测重新投影指定库的时间窗，外部观测保留真实提供的窗长。
        internal CharacterMatchTrajectory MatchAt(Vector2 requested) => Motion.Trajectory.Match.Windows == requested
            ? Motion.Trajectory.Match : CharacterForecastQuery.Project(Frame, originalTrajectory, Forecast, Time, forecastEnabled, requested).Match;

        // 以原固定步的时刻为步态选择查询本帧已核对的预测。
        internal CharacterAnimationMotion ProjectStep(in CharacterControllerFrameResult frame,
            in CharacterAnimationMotion motion, double time)
        {
            var trajectory = CharacterForecastQuery.Project(frame, motion.Trajectory, Forecast, time, forecastEnabled, windows);
            return new CharacterAnimationMotion(motion, trajectory);
        }
    }

    /// <summary>角色 Update 发布私有快照，所有 Update 结束后、引擎动画求值前统一提交。</summary>
    internal static class CharacterPresentationBatch
    {
        internal static readonly ProfilerMarker Planning = new ProfilerMarker("3C.Animation.Plan");
        internal static readonly ProfilerMarker Capture = new ProfilerMarker("3C.Animation.Capture");
        internal static readonly ProfilerMarker Wait = new ProfilerMarker("3C.Animation.Wait");
        private static readonly CharacterFrameBatch<CharacterBehaviorAnimator> batch =
            new CharacterFrameBatch<CharacterBehaviorAnimator>("3C.Animation.Submit", actor => actor.FinishPresentation());

        // 新会话清空上一轮未提交的动画计划。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => batch.Reset();

        // 所有 Update 完成后、动画求值前提交本帧计划。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install() => batch.Install(typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate),
            typeof(CharacterPresentationBatch));

        // 将已完成动画采集的角色加入本帧交付。
        internal static void Queue(CharacterBehaviorAnimator actor) => batch.Queue(actor);
    }
}
