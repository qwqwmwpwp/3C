#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>同一次直角转向的物理采样和对应渲染观测。缺失的渲染帧保留为空。</summary>
    internal sealed class CharacterTurnReplay
    {
        private struct Sample
        {
            internal long Number;
            internal float Seconds, InputYaw, BodyYaw, Speed;
            internal float? TravelYaw, Entry, LeftSlip, RightSlip;
            internal Vector3 Position, Forward;
            internal CharacterMatchTrajectory ShortPrediction, FullPrediction;
            internal bool Rendered, LeftSupport, RightSupport;
            internal bool? ObservedLeftSupport;
            internal string Clip, EntryClip;
        }

        private readonly Vector3 forward;
        private readonly List<Sample> samples = new List<Sample>(64);
        internal int Count => samples.Count;

        internal CharacterTurnReplay(Vector2 forward) => this.forward = new Vector3(forward.x, 0f, forward.y);

        internal void Record(in CharacterControllerFrameResult frame, in CharacterForecastSample solved,
            double stepTime, CharacterMovementForecast forecast)
        {
            var previous = Count > 0 ? samples[Count - 1] : default;
            var velocity = Vector3.ProjectOnPlane(solved.Velocity, Vector3.up);
            var full = default(CharacterMatchTrajectory);
            if (forecast != null && forecast.TrySample(stepTime, CharacterMatchTrajectory.NearSeconds, out var near) &&
                forecast.TrySample(stepTime, CharacterMatchTrajectory.FarSeconds, out var far))
                full = new CharacterMatchTrajectory(solved, near, far);
            samples.Add(new Sample
            {
                Number = frame.FrameNumber,
                Seconds = previous.Seconds + frame.DeltaTime,
                InputYaw = Vector3.SignedAngle(forward, new Vector3(frame.Input.Direction.x, 0f, frame.Input.Direction.y), Vector3.up),
                BodyYaw = Vector3.SignedAngle(forward, frame.FacingDirection, Vector3.up),
                TravelYaw = velocity.sqrMagnitude > 0.000001f ? Vector3.SignedAngle(forward, velocity, Vector3.up) : (float?)null,
                Speed = velocity.magnitude,
                Position = Vector3.ProjectOnPlane(solved.Position, Vector3.up),
                Forward = Vector3.ProjectOnPlane(frame.FacingDirection, Vector3.up).normalized,
                ShortPrediction = frame.Trajectory.Match, FullPrediction = full
            });
        }

        internal void Observe(in CharacterAnimationDebugSnapshot snapshot, bool left, bool right, float? leftSlip, float? rightSlip)
        {
            if (Count == 0 || snapshot.Status != CharacterAnimationDebugStatus.Running) return;
            int index = Count - 1;
            var sample = samples[index];
            if (snapshot.Motion.FrameNumber != sample.Number) return;
            var layer = snapshot.LowerBody;
            sample.Rendered = true;
            sample.Clip = layer.StateName;
            if (layer.SentStart.HasValue) { sample.Entry = layer.SentStart; sample.EntryClip = layer.StateName; }
            sample.LeftSupport = left;
            sample.RightSupport = right;
            sample.ObservedLeftSupport = snapshot.ObservedLeftSupport;
            if (leftSlip.HasValue) sample.LeftSlip = sample.LeftSlip.GetValueOrDefault() + leftSlip.Value;
            if (rightSlip.HasValue) sample.RightSlip = sample.RightSlip.GetValueOrDefault() + rightSlip.Value;
            samples[index] = sample;
        }

        internal string Describe(int index)
        {
            var s = samples[index];
            string travel = s.TravelYaw.HasValue ? $"{s.TravelYaw:F1}°" : "静止";
            string text = $"转向 {s.Seconds:F2} s · 固定步 {s.Number} · 相对入弯方向\n" +
                $"输入 {s.InputYaw:F1}° / 实际速度 {travel}（{s.Speed:F2} m/s）/ 身体 {s.BodyYaw:F1}°" +
                "\n固定步短程误差 " + Errors(index, s.ShortPrediction) + "\n完整预测误差 " + Errors(index, s.FullPrediction);
            if (!s.Rendered) return text + "\n此物理步没有对应动画观测";
            string support = s.LeftSupport ? (s.RightSupport ? "双脚" : "左脚") : (s.RightSupport ? "右脚" : "无已确认承重脚");
            string entry = s.Entry.HasValue ? $"，下发 {s.EntryClip} @ {s.Entry:F3}（归一化）" : "";
            string foot = s.ObservedLeftSupport.HasValue ? (s.ObservedLeftSupport.Value ? "左" : "右") : "未知";
            return text + $"\n下身 {s.Clip}{entry} · 步态支撑 {foot} / 地面 IK {support} · 滑移 左 {Slip(s.LeftSlip)} / 右 {Slip(s.RightSlip)}";
        }

        private string Errors(int index, in CharacterMatchTrajectory prediction) => prediction.Valid
            ? ErrorAt(index, CharacterMatchTrajectory.NearSeconds, prediction.Near, prediction.NearYaw) + "，" +
                ErrorAt(index, CharacterMatchTrajectory.FarSeconds, prediction.Far, prediction.FarYaw)
            : "无有效预测";

        private string ErrorAt(int origin, float horizon, Vector2 local, float yaw)
        {
            var start = samples[origin];
            float when = start.Seconds + horizon;
            for (int i = origin + 1; i < Count; i++)
            {
                var end = samples[i];
                if (end.Seconds + 0.00001f < when) continue;
                var before = samples[i - 1];
                float t = Mathf.InverseLerp(before.Seconds, end.Seconds, when);
                Vector3 actual = Vector3.Lerp(before.Position, end.Position, t);
                Vector3 predicted = start.Position + start.Forward * local.y + Vector3.Cross(Vector3.up, start.Forward) * local.x;
                float angle = Mathf.LerpAngle(before.BodyYaw, end.BodyYaw, t);
                return $"{horizon:F2} s {Vector3.Distance(actual, predicted) * 100f:F1} cm / " +
                    $"{Mathf.Abs(Mathf.DeltaAngle(angle, start.BodyYaw + yaw)):F1}°";
            }
            return $"{horizon:F2} s 后续采样不足";
        }

        private static string Slip(float? value) => value.HasValue ? $"{100f * value.Value:F2} cm" : "无连续样本";
    }
}
#endif
