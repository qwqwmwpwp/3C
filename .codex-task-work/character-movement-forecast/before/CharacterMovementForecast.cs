using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [Serializable]
    public sealed class CharacterForecastSettings
    {
        [InspectorName("启用移动预测")] public bool Enabled = true;
        [Min(0f), InspectorName("常规刷新间隔（秒）")] public float Interval = 0.2f;
        [Min(0.02f), InspectorName("对外预测时长（秒）")] public float Horizon = 2f;
        [Min(0f), InspectorName("摇杆变化阈值")] public float DirectionTolerance = 0.05f;
    }

    /// <summary>位置与速度使用世界坐标。行为和攻击阶段保持最近已完成步的值。</summary>
    public readonly struct CharacterForecastSample
    {
        public readonly Vector3 Position, Velocity;
        public readonly Quaternion Rotation;
        public readonly CharacterBehaviorId Behaviors;
        public readonly CharacterAttackMove Attack;
        public readonly CharacterBehaviorPhase AttackPhase;
        public readonly bool MoveApproved;
        public readonly CollisionFlags Contacts;

        internal CharacterForecastSample(Vector3 position, Vector3 velocity, Quaternion rotation,
            CharacterBehaviorFrameResult behavior, CollisionFlags contacts)
        {
            Position = position;
            Velocity = velocity;
            Rotation = rotation;
            Behaviors = behavior.ActiveBehaviors;
            Attack = behavior.Attack.Move;
            AttackPhase = behavior.Attack.Phase;
            MoveApproved = behavior.MoveApproved;
            Contacts = contacts;
        }

        private CharacterForecastSample(in CharacterForecastSample a, in CharacterForecastSample b, float fraction)
        {
            this = a;
            Position = Vector3.Lerp(a.Position, b.Position, fraction);
            Velocity = Vector3.Lerp(a.Velocity, b.Velocity, fraction);
            Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, fraction);
        }

        internal static CharacterForecastSample Between(in CharacterForecastSample a, in CharacterForecastSample b, float t)
            => new CharacterForecastSample(a, b, t);
    }

    /// <summary>已发布的时间序列。只有预测器可写，消费者只能按时间读取值。</summary>
    public sealed class CharacterMovementForecast
    {
        private CharacterForecastSample[] samples = Array.Empty<CharacterForecastSample>();
        private int count;
        private float step;
        public long Version { get; private set; }
        public long SourceStep { get; private set; }
        public double StartTime { get; private set; }
        public float Horizon { get; private set; }
        public float ValidSeconds => Mathf.Max(0, count - 1) * step;
        public bool Available => count > 0;
        public bool EnvironmentLimited { get; private set; }
        public string RefreshReason { get; private set; }

        internal void Clear()
        {
            if (count > 0) Version++;
            count = 0;
            EnvironmentLimited = false;
        }

        internal void Begin(int capacity, double time, long source, float dt, float horizon, string reason)
        {
            count = 0;
            if (samples.Length < capacity) Array.Resize(ref samples, capacity);
            Version++;
            StartTime = time;
            SourceStep = source;
            Horizon = horizon;
            step = dt;
            EnvironmentLimited = false;
            RefreshReason = reason;
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

#if UNITY_EDITOR
        internal void Draw()
        {
            for (int i = 1; i < count; i++)
            {
                Gizmos.color = (samples[i].Behaviors & CharacterBehaviorId.Dash) != 0 ? Color.yellow :
                    samples[i].Attack != CharacterAttackMove.None ? Color.magenta : Color.cyan;
                Gizmos.DrawLine(samples[i - 1].Position, samples[i].Position);
            }
            if (EnvironmentLimited && count > 0) Gizmos.DrawWireSphere(samples[count - 1].Position, 0.05f);
        }
#endif
    }
}
