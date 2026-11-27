using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 校验匹配轨道的可搜索性，并标出需要查看动作表现的采样位置。
    internal static class CharacterMotionTrackAudit
    {
        private const float TimeTolerance = 0.001f;
        internal const float PlantedContact = 0.7f; // 用于预览高亮，与运行时明确承重的接触下界一致。
        private const float ReleasedContact = 0.45f;

        // 校验轨道用途、入点、退出和作者轨迹，在构建配方时即可运行。
        internal static void ValidateRecipe(CharacterMotionTrack track)
        {
            if (track == null || !track.Clip) throw new InvalidOperationException("轨道缺少基础片段。");
            if (!(track.Seconds > 0f) || !Finite(track.Seconds) || !Finite(track.ReferenceSpeed) ||
                !Finite(track.BrakeSeconds) || !Finite(track.FirstEntrySeconds) || !Finite(track.LastEntrySeconds) ||
                !Finite(track.ExitSeconds) || !Finite(track.MinimumSpeed) || !Finite(track.MaximumSpeed) ||
                !Unit(track.Blend) || !Unit(track.FirstPhase) || !Unit(track.SecondPhase))
                throw new InvalidOperationException("时长、速度、混合和相位参数无效。");
            if (track.Use < GroundMotionUse.Cycle || track.Use > GroundMotionUse.Transition ||
                !Enum.IsDefined(typeof(GroundTrackGait), track.Gait) || !Enum.IsDefined(typeof(GroundTrackGait), track.FromGait) ||
                !Enum.IsDefined(typeof(GroundTrackSide), track.Side) || !Enum.IsDefined(typeof(GroundPathSource), track.PathSource) ||
                !track.Mixed && track.Loop != track.Clip.isLooping || track.Loop != (track.Use == GroundMotionUse.Cycle))
                throw new InvalidOperationException("用途、步态、侧别、轨迹类型或循环设置无效。");
            if (!(track.ReferenceSpeed > 0f) || track.MinimumSpeed < 0f || track.MaximumSpeed < 0f ||
                track.MaximumSpeed > 0f && track.MaximumSpeed < track.MinimumSpeed ||
                track.FirstEntrySeconds < 0f || track.LastEntrySeconds < track.FirstEntrySeconds ||
                track.LastEntrySeconds > track.Seconds + TimeTolerance || track.ExitSeconds < 0f ||
                track.ExitSeconds > track.Seconds + TimeTolerance || !track.IsLoop && track.ExitTime <= track.LastEntrySeconds)
                throw new InvalidOperationException("速度、入点或退出区间无效。");
            if (track.BrakeSeconds < 0f || track.IsStop && (track.Side == GroundTrackSide.Either ||
                track.BrakeSeconds <= track.FirstEntrySeconds || track.BrakeSeconds > track.ExitTime))
                throw new InvalidOperationException("收步需要承重脚，以及入点后、退出前的主制动标记。");
            if (!track.IsLoop && !track.IsStop && track.LastEntrySeconds + CharacterMatchTrajectory.FarSeconds > track.Seconds + TimeTolerance)
                throw new InvalidOperationException("末入点之后必须覆盖远端预测时间窗。");
            if (track.Use == GroundMotionUse.Transition && (track.FromGait == GroundTrackGait.Any ||
                track.Gait == GroundTrackGait.Any || track.FromGait == track.Gait || track.PathSource == GroundPathSource.Straight))
                throw new InvalidOperationException("换步需要不同的来源与目标步态及有效速度轨迹。");
            if (!Finite(track.TurnDegrees) || track.TurnDegrees < 0f || track.TurnDegrees > 180f)
                throw new InvalidOperationException("参考转角必须在 0 至 180 度。");
            if (track.Use == GroundMotionUse.Turn && (track.Side == GroundTrackSide.Either ||
                track.PathSource == GroundPathSource.Straight || track.TurnDegrees <= 0f))
                throw new InvalidOperationException("转向需要侧别、正的参考转角，以及根轨迹或作者参考轨迹。");
            if (track.PathSource == GroundPathSource.Authored) ValidatePath(track.ReferencePath, track.Seconds);
        }

        // 校验已采样姿势和根轨迹，并核对搜索循环能访问至少一个有效入点。
        internal static void ValidateSamples(CharacterMotionTrack track)
        {
            if (!track.ReadyForSearch)
                throw new InvalidOperationException("缺少匹配片段长度、足部姿势或轨迹采样。");
            if (track.PathSource == GroundPathSource.RootMotion) ValidatePath(track.RootPath, track.Seconds);
            bool hasEntry = false;
            for (int i = 0; i < track.Poses.Length; i++)
            {
                var pose = track.Poses[i];
                if (!Finite(pose.Left) || !Finite(pose.Right) || !Finite(pose.LeftVelocity) || !Finite(pose.RightVelocity) ||
                    !Unit(pose.Contact.x) || !Unit(pose.Contact.y) || pose.HasPelvis != 0 && !Finite(pose.Pelvis))
                    throw new InvalidOperationException($"{track.TimeAt(i):F3}s 的脚位、脚速、接触值或骨盆位置无效。");
                float time = track.TimeAt(i);
                if ((!track.IsLoop || i < track.Poses.Length - 1) && time >= track.FirstEntrySeconds &&
                    time <= track.LastEntrySeconds + 0.00001f && (!track.IsStop || time < track.BrakeSeconds)) hasEntry = true;
            }
            if (!hasEntry) throw new InvalidOperationException("入点窗口内没有可搜索的样点。");
        }

        // 检查参考轨迹从局部原点开始、时间严格递增，并覆盖整个片段。
        private static void ValidatePath(GroundPathKey[] keys, float seconds)
        {
            if (keys == null || keys.Length < 2 || keys[0].Seconds != 0f || keys[0].Position.sqrMagnitude > 0.000001f ||
                Mathf.Abs(keys[0].Yaw) > TimeTolerance || keys[keys.Length - 1].Seconds < seconds - TimeTolerance)
                throw new InvalidOperationException("参考轨迹应从局部原点开始并覆盖整个片段。");
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                if (!Finite(key.Seconds) || !Finite(key.Position.x) || !Finite(key.Position.y) || !Finite(key.Yaw) ||
                    i > 0 && key.Seconds <= keys[i - 1].Seconds)
                    throw new InvalidOperationException($"参考轨迹第 {i + 1} 个键的数值无效或时间未严格递增。");
            }
        }

        // 输出左右脚接触、收步承重、转向标记和循环衔接的复核提示。
        internal static void AddQualityWarnings(CharacterMotionTrack track, ICollection<string> warnings)
        {
            var poses = track.Poses;
            if (!Array.Exists(poses, pose => pose.Contact.x >= PlantedContact)) warnings.Add("整段左脚缺少明确接触样点。");
            if (!Array.Exists(poses, pose => pose.Contact.y >= PlantedContact)) warnings.Add("整段右脚缺少明确接触样点。");
            if (track.IsStop) AddBrakeWarnings(track, warnings);
            if (track.IsLoop) AddLoopWarnings(track, warnings);
            if (track.Use == GroundMotionUse.Turn || track.IsLoop && track.TurnDegrees > 0f)
                AddTurnWarnings(track, warnings);
        }

        // 检查标记的承重脚能否贯穿入点窗口及主制动时刻。
        private static void AddBrakeWarnings(CharacterMotionTrack track, ICollection<string> warnings)
        {
            bool left = track.Side == GroundTrackSide.Left;
            string support = left ? "左脚" : "右脚";
            var contact = ContactAt(track, track.BrakeSeconds);
            if ((left ? contact.x : contact.y) < PlantedContact)
                warnings.Add($"主制动 {track.BrakeSeconds:F3}s 时标注的{support}接触较弱（左 {contact.x:F2}，右 {contact.y:F2}）。");
            if (track.LastEntrySeconds >= track.BrakeSeconds)
                warnings.Add("末入点达到主制动时间，制动后的入点会被搜索跳过。");
            for (int i = 0; i < track.Poses.Length; i++)
            {
                float time = track.TimeAt(i);
                if (time < track.FirstEntrySeconds || time > track.LastEntrySeconds || time >= track.BrakeSeconds) continue;
                if (CharacterFootObservation.SupportOf(track.Poses[i].Contact) == track.Side) return;
            }
            warnings.Add($"有效入点内没有明确由{support}单脚承重的样点。");
        }

        // 对均匀采样的左右接触插值，以便检查位于两个样点之间的标记。
        internal static Vector2 ContactAt(CharacterMotionTrack track, float seconds)
        {
            float sample = Mathf.Clamp01(seconds / track.Seconds) * (track.Poses.Length - 1);
            int start = Mathf.Min((int)sample, track.Poses.Length - 2);
            return Vector2.Lerp(track.Poses[start].Contact, track.Poses[start + 1].Contact, sample - start);
        }

        // 核对运行时退出时可以使用的转角，以及循环轨迹每周期的转角。
        private static void AddTurnWarnings(CharacterMotionTrack track, ICollection<string> warnings)
        {
            float end = track.IsLoop ? track.Seconds : track.ExitTime;
            float yaw = GroundMotionPath.Relative(track, 0f, end).Yaw;
            float signed = track.Side == GroundTrackSide.Left ? -track.TurnDegrees : track.TurnDegrees;
            if (track.Side == GroundTrackSide.Either || Mathf.Sign(yaw) != Mathf.Sign(signed) || Mathf.Abs(yaw - signed) > 5f)
                warnings.Add($"{end:F3}s 参考偏航 {yaw:F1}° 与标注的 {track.Side} {track.TurnDegrees:F1}° 不一致。");
        }

        // 检查循环首尾姿势、接触和根轨迹速度是否能平滑连接。
        private static void AddLoopWarnings(CharacterMotionTrack track, ICollection<string> warnings)
        {
            var poses = track.Poses;
            AddSeamWarning(poses[0], poses[poses.Length - 1], warnings);
            if (track.PathSource == GroundPathSource.Straight) return;
            var keys = track.PathSource == GroundPathSource.Authored ? track.ReferencePath : track.RootPath;
            float step = Mathf.Min(keys[1].Seconds, track.Seconds / (poses.Length - 1));
            var incoming = GroundMotionPath.Relative(track, track.Seconds - step, step);
            var outgoing = GroundMotionPath.Relative(track, track.Seconds, step);
            float rotation = -incoming.Yaw * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rotation), sin = Mathf.Sin(rotation);
            var arrive = new Vector2(cos * incoming.Position.x + sin * incoming.Position.y,
                -sin * incoming.Position.x + cos * incoming.Position.y);
            if ((arrive - outgoing.Position).magnitude / step > Mathf.Max(0.4f, track.ReferenceSpeed * 0.35f) ||
                Mathf.Abs(incoming.Yaw - outgoing.Yaw) / step > 60f)
                warnings.Add("循环参考轨迹首尾的切向速度或偏航速度变化较大。");
        }

        // 以脚位距离和单脚接触差值提示可能看得见的循环接缝。
        internal static void AddSeamWarning(in CharacterFootPose first, in CharacterFootPose end, ICollection<string> warnings)
        {
            float position = Mathf.Max(Vector3.Distance(first.Left, end.Left), Vector3.Distance(first.Right, end.Right));
            float contact = Mathf.Max(Mathf.Abs(first.Contact.x - end.Contact.x), Mathf.Abs(first.Contact.y - end.Contact.y));
            if (position > 0.04f || contact > 0.25f)
                warnings.Add($"循环首尾脚位差 {position:F3}m、接触差 {contact:F2}，请复核接缝。");
        }

        // 使用实际混合相位、速率和权重扫描各脚两个来源之间的接触冲突。
        internal static void AddMixedContactWarnings(CharacterMotionTrack track, CharacterMotionBlend blend,
            CharacterClipCurves first, CharacterClipCurves second, ICollection<string> warnings)
        {
            if (!blend.Second || first == null || second == null) return;
            foreach (string curve in new[] { CharacterFootSampler.LeftContactCurve, CharacterFootSampler.RightContactCurve })
            {
                if (first.Find(curve) == null || second.Find(curve) == null) continue;
                int count = 0;
                float firstConflict = 0f, lastConflict = 0f;
                for (int i = 0; i < track.Poses.Length; i++)
                {
                    float time = track.TimeAt(i);
                    float logicalTime = blend.Time(time);
                    float weight = blend.Weight(logicalTime);
                    if (weight < 0.2f || weight > 0.8f ||
                        !first.TryRead(curve, CharacterMotionBlend.ClipTime(first.Clip, blend.FirstPhase, blend.FirstRate, logicalTime), out float a) ||
                        !second.TryRead(curve, CharacterMotionBlend.ClipTime(second.Clip, blend.SecondPhase, blend.SecondRate, logicalTime), out float b) ||
                        Mathf.Max(a, b) < PlantedContact || Mathf.Min(a, b) >= ReleasedContact) continue;
                    if (count++ == 0) firstConflict = time;
                    lastConflict = time;
                }
                if (count > 0) warnings.Add($"{curve} 双源接触冲突 {count} 个样点（首次 {firstConflict:F3}s，末次 {lastConflict:F3}s）。");
            }
        }

        // 判断单精度数能否参与轨迹或姿势计算。
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // 检查空间向量的三个坐标均有效。
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

        // 检查接触、相位或混合权重是否在单位区间。
        private static bool Unit(float value) => value >= 0f && value <= 1f;
    }
}
