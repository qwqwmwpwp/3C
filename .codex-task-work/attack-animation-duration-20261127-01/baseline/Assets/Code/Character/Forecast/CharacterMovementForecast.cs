using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [Serializable]
    public sealed class CharacterForecastSettings
    {
        [Header("启用条件")]
        [InspectorName("启用移动预测")] public bool Enabled = true;
        [Header("刷新条件")]
        [Min(0f), InspectorName("常规刷新间隔（秒）")] public float Interval = 0.2f;
        [Min(0f), InspectorName("摇杆变化阈值")] public float DirectionTolerance = 0.05f;
        [Header("预测时长")]
        [Min(0.02f), InspectorName("对外预测时长（秒）")] public float Horizon = 2f;
        [Header("结果用途")]
        [InspectorName("动画使用预测轨迹")] public bool DriveAnimation = true;
#if UNITY_EDITOR
        [InspectorName("击退预演片段（可选）")] public AnimationClip KnockbackPreview;
#endif
    }

    /// <summary>位置与速度使用世界坐标。行为和攻击阶段保持最近已完成步的值。</summary>
    public readonly struct CharacterForecastSample
    {
        public readonly Vector3 Position, Velocity;
        public readonly Quaternion Rotation;
        public readonly CharacterBehaviorId Behaviors;
        public readonly int AttackStateId;
#pragma warning disable CS0618 // 旧预测属性仍以历史枚举类型公开。
        [Obsolete("请使用 AttackStateId。")]
        public CharacterAttackMove Attack => (CharacterAttackMove)AttackStateId;
#pragma warning restore CS0618
        public readonly CharacterBehaviorPhase AttackPhase;
        public readonly float AttackProgress;
        public readonly long AttackInstance;
        internal readonly int BufferedStateId;
        internal readonly float LinkAtSeconds, Stamina;
        internal readonly Vector2 DashDirection;
        public readonly CharacterMoveGait Gait;
        public readonly bool MoveApproved, ExternallyControlled;
        internal bool AnticipatesMovementReturn => !ExternallyControlled &&
            (Behaviors & CharacterBehaviorId.Dash) != 0;
        public readonly CollisionFlags Contacts;

        // 保存同一时刻的物理样本和动作假设，供动画查询及慢任务回验。
        internal CharacterForecastSample(Vector3 position, Vector3 velocity, Quaternion rotation,
            CharacterBehaviorFrameResult behavior, CollisionFlags contacts, CharacterMoveGait gait, bool externallyControlled = false)
        {
            Position = position;
            Velocity = velocity;
            Rotation = rotation;
            ExternallyControlled = externallyControlled;
            Gait = gait;
            Behaviors = behavior.ActiveBehaviors;
            AttackStateId = behavior.Attack.StateId;
            AttackPhase = behavior.Attack.Phase;
            AttackProgress = behavior.Attack.Progress;
            AttackInstance = behavior.Attack.InstanceId;
            BufferedStateId = behavior.Attack.BufferedStateId;
            LinkAtSeconds = behavior.Attack.LinkAtSeconds;
            Stamina = behavior.StaminaRemaining;
            DashDirection = behavior.DashDirection;
            MoveApproved = behavior.MoveApproved;
            Contacts = contacts;
        }

        internal CharacterForecastSample UnderExternalControl() =>
            new CharacterForecastSample(Position, Velocity, Rotation, default, Contacts, Gait, true);

        // 在同一招式实例内插值相位，跨状态时保留前一已提交样本。
        private CharacterForecastSample(in CharacterForecastSample a, in CharacterForecastSample b, float fraction)
        {
            this = a;
            Position = Vector3.Lerp(a.Position, b.Position, fraction);
            Velocity = Vector3.Lerp(a.Velocity, b.Velocity, fraction);
            Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, fraction);
            if (a.AttackStateId == b.AttackStateId && a.AttackInstance == b.AttackInstance)
                AttackProgress = Mathf.Lerp(a.AttackProgress, b.AttackProgress, fraction);
        }

        internal static CharacterForecastSample Between(in CharacterForecastSample a, in CharacterForecastSample b, float t)
            => new CharacterForecastSample(a, b, t);
    }

    internal enum ForecastRefreshReason
    {
        None,
        Knockback,
        Suppressed,
        Resumed,
        DynamicContact,
        Expired,
        Scheduled,
        MovementInput,
        Settings,
        Retry,
        SimulationBoundary,
        Initial,
        RecheckContact,
        Behavior,
        Contact,
        Deviation,
        ExternalState,
        AttackPressed,
        DashPressed,
        RehearsalInput,
    }

    internal static class ForecastRefreshLabels
    {
        internal static string Label(this ForecastRefreshReason reason) => reason switch
        {
            ForecastRefreshReason.Knockback => "受击接管",
            ForecastRefreshReason.Suppressed => "外部行为接管",
            ForecastRefreshReason.Resumed => "恢复控制",
            ForecastRefreshReason.DynamicContact => "动态接触变化",
            ForecastRefreshReason.Expired => "预测已过期",
            ForecastRefreshReason.Scheduled => "定时",
            ForecastRefreshReason.MovementInput => "移动输入",
            ForecastRefreshReason.Settings => "参数变化",
            ForecastRefreshReason.Retry => "重试预测",
            ForecastRefreshReason.SimulationBoundary => "模拟边界",
            ForecastRefreshReason.Initial => "建立起点",
            ForecastRefreshReason.RecheckContact => "重新检查接触",
            ForecastRefreshReason.Behavior => "行为变化",
            ForecastRefreshReason.Contact => "接触变化",
            ForecastRefreshReason.Deviation => "物理偏差",
            ForecastRefreshReason.ExternalState => "外部状态变化",
            ForecastRefreshReason.AttackPressed => "攻击按下",
            ForecastRefreshReason.DashPressed => "短突进按下",
            ForecastRefreshReason.RehearsalInput => "预演输入变化",
            _ => null
        };
    }

    internal enum CharacterForecastRevision { Published, Invalidated, Takeover, Withdraw, Reset }

    /// <summary>已发布的时间序列。只有预测器可写，消费者只能按时间读取值。</summary>
    public sealed class CharacterMovementForecast
    {
        private CharacterForecastSample[] samples = Array.Empty<CharacterForecastSample>();
        private int count;
        private float step;
        public long Version { get; private set; }
        public long SourceStep { get; private set; }
        public double SourcePhysicsTime { get; private set; }
        public long SourceFrameVersion { get; private set; }
        public int SimulationVersion { get; private set; }
        public double StartTime { get; private set; }
        public float Horizon { get; private set; }
        public float ValidSeconds => Mathf.Max(0, count - 1) * step;
        public bool Available => count > 0;
        public bool EnvironmentLimited { get; private set; }
        public string RefreshReason { get; private set; }

        // 普通失效立即撤回公开结果，并通知已有轨迹或显式边界的观察者。
        internal void Clear(CharacterForecastRevision revision = CharacterForecastRevision.Reset)
        {
            bool notify = count > 0 || revision != CharacterForecastRevision.Invalidated;
            ClearSamples();
            if (notify) Notify(revision);
        }

        // 运动交接先清除全部可读取样本，内置动画完成清理后再发公开通知。
        internal void ClearSamples()
        {
            if (count > 0) Version++;
            count = 0;
            EnvironmentLimited = false;
        }

        // 绑定完整帧版本、真实求解编号与两个时刻，后续计算只写本结果的样本。
        internal void Begin(int capacity, double time, in CharacterForecastFrame frame, ForecastRefreshReason reason)
        {
            count = 0;
            if (samples.Length < capacity) Array.Resize(ref samples, capacity);
            Version++;
            StartTime = time;
            SourceStep = frame.Origin.Number;
            SourcePhysicsTime = frame.Origin.Time;
            SourceFrameVersion = frame.Version;
            SimulationVersion = frame.Generation;
            Horizon = frame.Window.Horizon;
            step = frame.Window.Step;
            EnvironmentLimited = false;
            RefreshReason = reason.Label();
        }

        internal void Publish(CharacterMovementForecast completed)
        {
            long next = Version + 1;
            CopyPublishedFrom(completed);
            Version = next;
            completed.Version = next;
            snapshot = completed;
            Notify(CharacterForecastRevision.Published);
        }

        internal void Append(in CharacterForecastSample sample) => samples[count++] = sample;
        internal void EndAtContact() => EnvironmentLimited = true;

        /// <summary>按角色模拟时钟查询；超过动态接触的可信边界时返回 false。</summary>
        public bool TrySample(double now, float secondsAhead, out CharacterForecastSample sample)
        {
            sample = default;
            double elapsed = now - StartTime + secondsAhead;
            if (count == 0 || !(secondsAhead >= 0f && secondsAhead <= Horizon) ||
                !(elapsed >= -0.000001 && elapsed <= ValidSeconds + 0.000001)) return false;
            double offset = Math.Max(0d, elapsed / step);
            int index = Math.Min(count - 1, (int)Math.Floor(offset + 0.00001));
            sample = index == count - 1 ? samples[index] : CharacterForecastSample.Between(samples[index],
                samples[index + 1], Mathf.Clamp01((float)(offset - index)));
            return true;
        }

        // 在突进结束后查找最早获准普通移动的样本。
        internal bool TryReturnToMovement(double now, out float ahead, out CharacterForecastSample next)
        {
            ahead = 0f;
            next = default;
            if (count < 2) return false;
            int first = Mathf.Max(0, Mathf.CeilToInt((float)(now - StartTime) / step));
            for (int i = first; i < count; i++)
            {
                var candidate = samples[i];
                ahead = (float)(StartTime - now + i * (double)step);
                if (ahead > Horizon) break;
                if (candidate.MoveApproved && (candidate.Behaviors & CharacterBehaviorId.Dash) == 0)
                {
                    next = candidate;
                    ahead = Mathf.Max(0f, ahead);
                    return true;
                }
            }
            return false;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        internal void Notify(CharacterForecastRevision revision)
        {
#if UNITY_EDITOR
            try { Revised?.Invoke(revision); }
            catch (Exception error) { Debug.LogException(error); } // 编辑器观察者异常不影响角色推演或后续通知。
#endif
        }

        private CharacterMovementForecast snapshot;

        internal CharacterMovementForecast Snapshot()
        {
            if (!Available) return null;
            if (snapshot == null || snapshot.Version != Version)
            {
                snapshot = new CharacterMovementForecast();
                snapshot.CopyPublishedFrom(this);
            }
            return snapshot;
        }

#if UNITY_EDITOR
        // 帧末采集完毕后，编辑器按这一帧已裁决的行为顺序核对轨迹打断。
        internal event Action FrameSubmitted;
        // 保留角色的正常调度，即使单个场景视图观察者抛出异常。
        internal void NotifyFrameSubmitted()
        {
            try { FrameSubmitted?.Invoke(); }
            catch (Exception error) { Debug.LogException(error); }
        }

        // 发布后复制，避免编辑器读取下一次重算正在复用的缓冲。
        internal event Action<CharacterForecastRevision> Revised;
        internal float SampleInterval => step;
#endif
        // 将采样数组和对应的帧末版本、求解游标及有效窗口一起复制。
        internal void CopyPublishedFrom(CharacterMovementForecast source)
        {
            if (samples.Length < source.count) Array.Resize(ref samples, source.count);
            Array.Copy(source.samples, samples, source.count);
            count = source.count;
            step = source.step;
            Version = source.Version;
            SourceStep = source.SourceStep;
            SourcePhysicsTime = source.SourcePhysicsTime;
            SourceFrameVersion = source.SourceFrameVersion;
            SimulationVersion = source.SimulationVersion;
            StartTime = source.StartTime;
            Horizon = source.Horizon;
            EnvironmentLimited = source.EnvironmentLimited;
            RefreshReason = source.RefreshReason;
        }
    }
}
