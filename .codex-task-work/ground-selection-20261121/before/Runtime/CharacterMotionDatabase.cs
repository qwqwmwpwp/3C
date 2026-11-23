using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    public enum CharacterMatchMode
    {
        [InspectorName("原规则")] Rules,
        [InspectorName("旁路观察")] Observe,
        [InspectorName("应用匹配")] Apply
    }

    [Serializable]
    internal struct CharacterFootPose
    {
        public Vector3 Left, Right;
        public Vector3 LeftVelocity, RightVelocity;
        public Vector2 Contact;

        public void SetVelocity(in CharacterFootPose before, in CharacterFootPose after, float seconds)
        {
            LeftVelocity = (after.Left - before.Left) / seconds;
            RightVelocity = (after.Right - before.Right) / seconds;
        }
    }

    [Serializable]
    internal sealed class CharacterMotionTrack
    {
        public AnimationClip Clip;
        public string[] States; // 同片段的起步和循环别名共用一份采样。
        [Min(0f)] public float ReferenceSpeed;
        [Tooltip("原地素材的参考制动时长；循环填 0。")] public float BrakeSeconds;
        [Tooltip("可进入区间的最后一秒；循环可用整个周期。")] public float LastEntrySeconds;
        [HideInInspector] public float Seconds;
        [HideInInspector] public CharacterFootPose[] Poses;
        public bool IsStop => BrakeSeconds > 0f;
        public bool IsReady => Clip && Poses != null && Poses.Length >= 2 && Seconds > 0f &&
            Mathf.Abs(Seconds - Clip.length) < 0.003f;

        public int Index(float phase)
        {
            float t = IsStop ? Mathf.Clamp01(phase) : Mathf.Repeat(phase, 1f);
            return Mathf.RoundToInt(t * (Poses.Length - 1));
        }

        public float TimeAt(int index) => index * Seconds / (Poses.Length - 1);

        // 这些素材为原地动画。参考根沿角色前方行进，制动段按匀减速到零。
        public float Distance(float seconds, float horizon)
        {
            if (!IsStop) return ReferenceSpeed * horizon;
            float moving = Mathf.Clamp(BrakeSeconds - seconds, 0f, horizon);
            float startSpeed = ReferenceSpeed * Mathf.Clamp01(1f - seconds / BrakeSeconds);
            return startSpeed * moving - 0.5f * ReferenceSpeed / BrakeSeconds * moving * moving;
        }
    }

    [CreateAssetMenu(menuName = "3C/地面动作数据库")]
    public sealed class CharacterMotionDatabase : ScriptableObject
    {
        [SerializeField] internal Avatar Avatar;
        [SerializeField] internal RuntimeAnimatorController Controller;
        [SerializeField] internal CharacterMotionTrack[] Tracks;
    }
}
