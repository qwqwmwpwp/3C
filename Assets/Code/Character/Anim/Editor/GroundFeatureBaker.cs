using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在原始片段姿态上记录与运行时相同的布局，并按轨道统一标记可用通道。
    internal sealed class GroundFeatureBaker
    {
        private readonly GroundFeatureLayout layout;
        private readonly CharacterMotionTrack track;
        private readonly Transform model;
        private readonly Transform[] bones, references;
        private readonly int frames;

        // 一次解析人形骨骼和参考物，缺骨通道对整条轨道禁用。
        internal GroundFeatureBaker(GroundFeatureLayout layout, CharacterMotionTrack track, Animator animator, int frames)
        {
            this.layout = layout;
            this.track = track;
            this.frames = frames;
            model = animator.transform;
            int count = layout.Channels.Length;
            bones = new Transform[count];
            references = new Transform[count];
            track.FeatureFingerprint = layout.Fingerprint;
            track.FeatureValues = new Vector3[count * frames];
            track.FeatureMask = layout.Available;
            for (int i = 0; i < count; i++)
            {
                var c = layout.Channels[i];
                if (c.Path) continue;
                bones[i] = animator.GetBoneTransform(c.Bone);
                references[i] = c.Space == GroundFeatureSpace.Model ? model : animator.GetBoneTransform(c.Reference);
                if (!bones[i] || !references[i]) track.FeatureMask &= ~(1u << i);
            }
        }

        // 从本次求值的模型或参考骨基准读取局部位置、朝向和待差分的位置。
        internal void Sample(int frame)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                var c = layout.Channels[i];
                if (c.Path || (track.FeatureMask & (1u << i)) == 0) continue;
                Quaternion basis = c.Space == GroundFeatureSpace.Bone ? references[i].rotation : model.rotation;
                var value = c.Value(bones[i].position, bones[i].rotation, references[i].position, basis);
                if (!(value.sqrMagnitude < float.PositiveInfinity)) track.FeatureMask &= ~(1u << i);
                else track.FeatureValues[frame * bones.Length + i] = value;
            }
        }

        // 使用首尾相位一致的中心差分，非循环两端使用单边差分。
        internal void Finish(float dt)
        {
            var values = track.FeatureValues;
            var source = (Vector3[])values.Clone();
            int end = frames - 1;
            for (int i = 0; i < bones.Length; i++)
            {
                if (layout.Channels[i].Kind != GroundFeatureKind.Velocity || (track.FeatureMask & (1u << i)) == 0) continue;
                for (int j = 0; j <= end; j++)
                {
                    int a = j > 0 ? j - 1 : track.IsLoop ? end - 1 : 0;
                    int b = j < end ? j + 1 : track.IsLoop ? 1 : end;
                    float span = !track.IsLoop && (j == 0 || j == end) ? dt : 2f * dt;
                    values[j * bones.Length + i] = (source[b * bones.Length + i] - source[a * bones.Length + i]) / span;
                }
            }
        }
    }
}
