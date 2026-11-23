using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/基础动画数据组")]
    public sealed class CharacterMotionGroups : ScriptableObject
    {
        [SerializeField, InspectorName("身体分层")] internal CharacterClipLayer[] Layers;
        [SerializeField, InspectorName("直接播放的基础动作")] internal CharacterClipState[] Bindings;
        [SerializeField, InspectorName("地面动作组")] internal GroundAnimationGroup[] Groups;
    }

    [Serializable]
    internal sealed class GroundAnimationGroup
    {
        public string Name;
        [InspectorName("基础片段")] public AnimationClip First;
        [InspectorName("混合片段")] public AnimationClip Second;
        [Tooltip("混合片段在开始、中点、结束时的权重，区间之间平滑插值。")]
        public Vector3 Weights = new Vector3(0f, 0.5f, 1f);
        [Min(0f)] public float FirstRate = 1f, SecondRate = 1f;
        [Tooltip("轨道共用本组的基础片段和混合片段，轨道上的 Clip 可留空。")]
        public CharacterMotionTrack[] Variants;
    }

    // 一个逻辑动作有独立的时钟。两个基础片段使用各自的步相和速率。
    [Serializable]
    internal struct CharacterMotionBlend
    {
        public AnimationClip Second;
        public Vector3 Weights;
        public float Seconds, FirstPhase, SecondPhase, FirstRate, SecondRate;
        public bool Loop;
        public float Time(double seconds) => Loop ? Mathf.Repeat((float)(seconds % Seconds), Seconds) : Mathf.Clamp((float)seconds, 0f, Seconds);
        public float Weight(float time)
        {
            float t = 2f * time / Seconds;
            return t <= 1f ? Mathf.SmoothStep(Weights.x, Weights.y, t) : Mathf.SmoothStep(Weights.y, Weights.z, t - 1f);
        }

        public static float ClipTime(AnimationClip clip, float phase, float rate, float time)
        {
            float value = phase * clip.length + rate * time;
            return clip.isLooping ? Mathf.Repeat(value, clip.length) : Mathf.Clamp(value, 0f, clip.length);
        }
    }
}
