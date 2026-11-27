using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存基础播放配方及以库内 ID 标识的地面动作组。
    [CreateAssetMenu(menuName = "3C/基础动画数据组")]
    public sealed class CharacterMotionGroups : ScriptableObject
    {
#if UNITY_EDITOR
        // 标记已装配的角色搜索范围，使编辑器修改在下一批次重新生效。
        private void OnValidate() => GroundSearchLibraries.InvalidateBindings();
#endif
        [SerializeField, InspectorName("特征布局")] internal GroundFeatureSchema Schema;
        [SerializeField, InspectorName("身体分层")] internal CharacterClipLayer[] Layers;
        [SerializeField, InspectorName("直接播放的基础动作")] internal CharacterClipState[] Bindings;
        [SerializeField, InspectorName("地面动作组")] internal GroundAnimationGroup[] Groups;

        // 按库索引合法组 ID，并报告不可区分或缺失的组位置。
        internal Dictionary<string, int> IndexGroups(out string issue)
        {
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            for (int i = 0; i < (Groups?.Length ?? 0); i++)
            {
                var group = Groups[i];
                if (string.IsNullOrWhiteSpace(group?.GroupId)) errors.Add($"{GroupLabel(group, i)} 缺少组 ID");
                else if (!IsGroupId(group.GroupId)) errors.Add($"{GroupLabel(group, i)} 的组 ID {group.GroupId} 无效");
                else if (ids.TryGetValue(group.GroupId, out int previous))
                {
                    duplicates.Add(group.GroupId);
                    errors.Add($"{GroupLabel(group, i)} 与 {GroupLabel(Groups[previous], previous)} 的组 ID {group.GroupId} 重复");
                }
                else ids.Add(group.GroupId, i);
            }
            foreach (string duplicate in duplicates) ids.Remove(duplicate);
            issue = errors.Count == 0 ? null : $"{name} · " + string.Join("；", errors);
            return ids;
        }

        // 校验组 ID 为不带分隔符的非零 GUID。
        internal static bool IsGroupId(string id) => id?.Length == 32 && Guid.TryParseExact(id, "N", out var value) && value != Guid.Empty;

        // 为组诊断保留在源资产中的位置及可读名称。
        private static string GroupLabel(GroundAnimationGroup group, int index) =>
            $"第 {index + 1} 组{(string.IsNullOrWhiteSpace(group?.Name) ? string.Empty : $" {group.Name}")}";
    }

    // 将地面动作的稳定组身份与可编辑名称、片段及变体关联。
    [Serializable]
    internal sealed class GroundAnimationGroup
    {
        [HideInInspector] public string GroupId;
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
