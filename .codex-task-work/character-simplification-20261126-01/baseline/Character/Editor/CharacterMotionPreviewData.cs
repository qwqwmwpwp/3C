using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 结合隔离场景的实际根和骨骼，与匹配参考轨迹构造地面预览。
    internal sealed class CharacterMotionPreviewData
    {
        public readonly CharacterMotionTrack Track;
        public readonly CharacterMotionBlend Blend;
        public readonly List<string> Warnings;
        public readonly Vector3[] SourceRoots, ReferenceRoots, Hips, Left, Right;
        public readonly Quaternion[] Facing;
        public readonly Vector2 ContactTravel; // 左、右脚各自连续接触中的最大水平位移，单位为米。

        // 将真实根相对骨骼放到参考根上，无根动作明确使用参考位移估算。
        public CharacterMotionPreviewData(CharacterMotionTrack track, CharacterMotionBlend blend,
            CharacterMotionPoseFrame[] frames, List<string> warnings)
        {
            Track = track;
            Blend = blend;
            Warnings = warnings;
            int count = frames.Length;
            SourceRoots = new Vector3[count];
            ReferenceRoots = new Vector3[count];
            Hips = new Vector3[count];
            Left = new Vector3[count];
            Right = new Vector3[count];
            Facing = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                var frame = frames[i];
                SourceRoots[i] = frame.Root;
                if (track.PathSource == GroundPathSource.RootMotion)
                {
                    ReferenceRoots[i] = frame.Root;
                    Facing[i] = frame.RootRotation;
                }
                else
                {
                    var key = GroundMotionPath.Relative(track, 0f, track.TimeAt(i));
                    ReferenceRoots[i] = new Vector3(key.Position.x, 0f, key.Position.y);
                    Facing[i] = Quaternion.AngleAxis(key.Yaw, Vector3.up);
                }
                Hips[i] = ReferenceRoots[i] + Facing[i] * frame.Hips;
                Left[i] = ReferenceRoots[i] + Facing[i] * frame.Left;
                Right[i] = ReferenceRoots[i] + Facing[i] * frame.Right;
            }
            ContactTravel = new Vector2(MaxContactTravel(Left, true), MaxContactTravel(Right, false));
        }

        // 计算同一段连续接触中，脚相对于该段起点的最大水平位移。
        private float MaxContactTravel(Vector3[] positions, bool left)
        {
            float max = 0f;
            int start = -1;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!IsPlanted(i, left))
                {
                    start = -1;
                    continue;
                }
                if (start < 0) start = i;
                max = Mathf.Max(max, HorizontalDistanceSquared(positions[i], positions[start]));
            }

            // 末样点已覆盖下一周期的相位 0；整周期接触时只统计一周期。
            if (Track.IsLoop && start > 0 && IsPlanted(0, left))
            {
                var endRoot = ReferenceRoots[ReferenceRoots.Length - 1];
                var turn = GroundMotionPath.Relative(Track, 0f, Track.Seconds).Yaw;
                var rotation = Quaternion.AngleAxis(turn, Vector3.up);
                for (int i = 1; i < start && IsPlanted(i, left); i++)
                {
                    var next = endRoot + rotation * (positions[i] - ReferenceRoots[0]);
                    max = Mathf.Max(max, HorizontalDistanceSquared(next, positions[start]));
                }
            }
            return Mathf.Sqrt(max);
        }

        // 判断选定脚在指定样点是否达到预览接触阈值。
        private bool IsPlanted(int index, bool left)
        {
            var contact = Track.Poses[index].Contact;
            return (left ? contact.x : contact.y) >= CharacterMotionTrackAudit.PlantedContact;
        }

        // 在地面水平面计算两点距离的平方。
        private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
        {
            var delta = a - b;
            return delta.x * delta.x + delta.z * delta.z;
        }
    }
}
