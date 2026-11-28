using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存相对发布起点的时间戳及该时刻的完整动画观测。
    public readonly struct AnimationTrajectoryKey
    {
        public readonly float SecondsAhead;
        public readonly AnimationTrajectorySample Sample;

        // 以相对秒数标记一个世界空间样本。
        public AnimationTrajectoryKey(float secondsAhead, AnimationTrajectorySample sample)
        {
            SecondsAhead = secondsAhead;
            Sample = sample;
        }
    }

    // 冻结可不等距的轨迹样本，在可信时间段内插值；发布后可从后台线程查询。
    public sealed class AnimationTrajectorySequence : IAnimationTrajectorySnapshot
    {
        private readonly AnimationTrajectoryKey[] keys;
        public long Version { get; }
        public double StartTime { get; }
        public bool Available => keys.Length > 0;
        public float FirstSeconds => Available ? keys[0].SecondsAhead : 0f;
        public float LastSeconds => Available ? keys[keys.Length - 1].SecondsAhead : 0f;

        // 复制时间严格递增的样本，拒绝无效时刻、空间量和零长度旋转。
        public AnimationTrajectorySequence(double startTime, long version, params AnimationTrajectoryKey[] samples)
        {
            if (double.IsNaN(startTime) || double.IsInfinity(startTime)) throw new ArgumentOutOfRangeException(nameof(startTime));
            StartTime = startTime;
            Version = version;
            keys = samples == null ? Array.Empty<AnimationTrajectoryKey>() : (AnimationTrajectoryKey[])samples.Clone();
            float previous = -1f;
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                var pose = key.Sample;
                float length = Quaternion.Dot(pose.Rotation, pose.Rotation);
                if (!(key.SecondsAhead >= 0f && key.SecondsAhead > previous && Finite(key.SecondsAhead) &&
                    Finite(pose.Position.sqrMagnitude) && Finite(pose.Velocity.sqrMagnitude) && length > 0.000001f && Finite(length)))
                    throw new ArgumentException($"第 {i + 1} 个轨迹点的时刻、位置、速度或旋转无效。", nameof(samples));
                pose.Rotation = Quaternion.Normalize(pose.Rotation);
                keys[i] = new AnimationTrajectoryKey(key.SecondsAhead, pose);
                previous = key.SecondsAhead;
            }
        }

        // 检查采样数据能否作为插值输入。
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // 以来源时钟查询世界姿态，离散归属使用该时刻最近已到达的键。
        public bool TrySample(double now, float secondsAhead, out AnimationTrajectorySample sample)
        {
            sample = default;
            if (!Available || !(secondsAhead >= 0f && secondsAhead <= LastSeconds)) return false;
            double offset = now - StartTime + secondsAhead;
            if (!(offset >= FirstSeconds - 0.000001 && offset <= LastSeconds + 0.000001)) return false;
            int next = AtOrAfter(offset);
            if (next == keys.Length) next--;
            var b = keys[next];
            if (next == 0 || offset >= b.SecondsAhead - 0.000001)
            {
                sample = b.Sample;
                return true;
            }
            var a = keys[next - 1];
            float t = (float)((offset - a.SecondsAhead) / (b.SecondsAhead - a.SecondsAhead));
            sample = a.Sample;
            sample.Position = Vector3.Lerp(a.Sample.Position, b.Sample.Position, t);
            sample.Velocity = Vector3.Lerp(a.Sample.Velocity, b.Sample.Velocity, t);
            sample.Rotation = Quaternion.Slerp(a.Sample.Rotation, b.Sample.Rotation, t);
            return true;
        }

        // 返回当前时刻之后首个明确标记为普通移动且仍有控制权的离散样本。
        public bool TryReturnToMovement(double now, out float secondsAhead, out AnimationTrajectorySample sample)
        {
            secondsAhead = 0f;
            sample = default;
            if (double.IsNaN(now) || double.IsInfinity(now)) return false;
            double offset = now - StartTime;
            for (int i = AtOrAfter(offset - 0.000001); i < keys.Length; i++)
            {
                var key = keys[i];
                double ahead = key.SecondsAhead - offset;
                if (ahead > LastSeconds + 0.000001) return false;
                if (!key.Sample.IsLocomotion || key.Sample.ExternallyControlled) continue;
                secondsAhead = Mathf.Max(0f, (float)ahead);
                sample = key.Sample;
                return true;
            }
            return false;
        }

        // 从已排序的可信时间戳中定位第一个不早于指定时刻的键。
        private int AtOrAfter(double offset)
        {
            int low = 0, high = keys.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (keys[mid].SecondsAhead < offset) low = mid + 1;
                else high = mid;
            }
            return low;
        }
    }
}
