using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>需求覆盖的身体范围；局部动画由 Animator 层上的 AvatarMask 限定骨骼。</summary>
    public enum CharacterAnimationBodyRegion
    {
        [InspectorName("全身")] FullBody,
        [InspectorName("上半身")] UpperBody,
        [InspectorName("下半身")] LowerBody,
        [InspectorName("全身叠加")] FullBodyOverlay
    }

    /// <summary>需求实际获选后被其他动作压制时的处理；尚未获选的需求继续等待。</summary>
    public enum CharacterAnimationInterruptionPolicy
    {
        [InspectorName("恢复时重播（兼容默认）")] Replay,
        [InspectorName("被打断后取消")] Cancel
    }

    /// <summary>第一阶段的语义需求，不包含 Animator 层索引、状态路径或资源引用。</summary>
    public readonly struct CharacterAnimationRequest
    {
        public string SemanticKey { get; }
        public long InstanceId { get; }
        /// <summary>局部覆盖权重，范围 0 至 1；全身基底始终以完整权重播放。</summary>
        public float Weight { get; }
        /// <summary>可选归一化起点；null 使用资源映射配置，仅在新实例或重新选中时应用。</summary>
        public float? StartNormalizedTime { get; }
        internal CharacterLocomotionVariant LocomotionVariant { get; }

        /// <summary>创建持续需求。同一语义键的实例编号必须为正数且递增；同实例可更新权重而不重播。</summary>
        public CharacterAnimationRequest(string semanticKey, long instanceId, float weight = 1f,
            float? startNormalizedTime = null)
            : this(semanticKey, instanceId, CharacterLocomotionVariant.None, weight, startNormalizedTime)
        {
        }

        internal CharacterAnimationRequest(string semanticKey, long instanceId, CharacterLocomotionVariant locomotionVariant,
            float weight = 1f, float? startNormalizedTime = null)
        {
            SemanticKey = semanticKey;
            InstanceId = instanceId;
            Weight = weight;
            StartNormalizedTime = startNormalizedTime;
            LocomotionVariant = locomotionVariant;
        }
    }

    /// <summary>此动作获选时，对各分区惯性的抑制比例。旧映射默认全部允许。</summary>
    [Serializable]
    public struct CharacterActionInertiaSuppression
    {
        [Range(0f, 1f), InspectorName("胸肩")] public float Chest;
        [Range(0f, 1f), InspectorName("左臂")] public float LeftArm;
        [Range(0f, 1f), InspectorName("右臂")] public float RightArm;

        internal Vector3 ToVector() => new Vector3(Clamp(Chest), Clamp(LeftArm), Clamp(RightArm));
        private static float Clamp(float value) => Mathf.Min(1f, InputMath.NonNegative(value, 0f));
    }

    /// <summary>动画系统拥有的语义资源映射，调用方无需知道 Animator 状态路径。</summary>
    [Serializable]
    public sealed class CharacterAnimationActionSettings
    {
        [Tooltip("供外部提交和撤销的唯一语义键，例如 upper.idle。")]
        public string Key;
        [Tooltip("全身需求作为基底；局部需求仅在优先级严格高于全身基底时覆盖对应部位。")]
        public CharacterAnimationBodyRegion Region;
        [Tooltip("数值越大越优先；同一部位同优先级时，后提交的新实例优先。")]
        public int Priority = 50;
        [Tooltip("资源完整状态路径，必须存在于所选部位的 Animator 层中。")]
        public string StateName;
        [InspectorName("移动姿势"), Tooltip("持续动作在实际移动时使用的同层状态。留空时沿用默认姿势。")]
        public string MovingStateName;
        [Range(0f, 1f), Tooltip("新实例或被压制后恢复时的归一化播放起点；运行时请求可覆盖此值。")]
        public float StartNormalizedTime;
        [Range(0f, 1f), Tooltip("切换不同动画状态时的归一化过渡时长，相对源状态长度；0 为立即切换。")]
        public float StateTransitionDuration;
        [Range(0f, 1f), Tooltip("离开该动作时的归一化过渡时长上限，与进入动作的时长取较小值。")]
        public float StateExitTransitionDuration = 1f;
        [Min(0f), Tooltip("局部层从权重 0 到 1 的过渡时长，单位秒；退出沿用当前动作配置，0 为立即切换。")]
        public float LayerBlendSeconds;
        [Tooltip("实际获选后被高优先级需求打断：默认保留并从起点重播，或取消该实例。首次获选前被压制仍等待。")]
        public CharacterAnimationInterruptionPolicy InterruptionPolicy;
        [InspectorName("分区惯性抑制"), Tooltip("动作获选时按其权重抑制相应部位的惯性。0 完全保留，1 完全抑制。")]
        public CharacterActionInertiaSuppression InertiaSuppression;

        internal static CharacterAnimationActionSettings[] CreateDefaults()
        {
            return new[]
            {
                new CharacterAnimationActionSettings { Key = "upper.idle", Region = CharacterAnimationBodyRegion.UpperBody,
                    StateName = "Upper Body.idle" },
                new CharacterAnimationActionSettings { Key = "upper.move", Region = CharacterAnimationBodyRegion.UpperBody,
                    StateName = "Upper Body.move" },
                new CharacterAnimationActionSettings { Key = "upper.dash", Region = CharacterAnimationBodyRegion.UpperBody,
                    StateName = "Upper Body.dash" },
                new CharacterAnimationActionSettings { Key = "lower.idle", Region = CharacterAnimationBodyRegion.LowerBody,
                    StateName = "Lower Body.idle" },
                new CharacterAnimationActionSettings { Key = "lower.move", Region = CharacterAnimationBodyRegion.LowerBody,
                    StateName = "Lower Body.move" },
                new CharacterAnimationActionSettings { Key = "lower.dash", Region = CharacterAnimationBodyRegion.LowerBody,
                    StateName = "Lower Body.dash" },
                new CharacterAnimationActionSettings { Key = "full.dash", Region = CharacterAnimationBodyRegion.FullBody,
                    Priority = 100, StateName = "Base Layer.dash" }
            };
        }
    }

    /// <summary>仲裁规则只描述语义、身体范围、优先级与中断方式，不包含资源信息。</summary>
    internal readonly struct CharacterAnimationRule
    {
        public string Key { get; }
        public CharacterAnimationBodyRegion Region { get; }
        public int Priority { get; }
        public CharacterAnimationInterruptionPolicy InterruptionPolicy { get; }
        public Vector3 InertiaSuppression { get; }

        public CharacterAnimationRule(string key, CharacterAnimationBodyRegion region, int priority,
            CharacterAnimationInterruptionPolicy interruptionPolicy = CharacterAnimationInterruptionPolicy.Replay,
            Vector3 inertiaSuppression = default)
        {
            Key = key;
            Region = region;
            Priority = priority;
            InterruptionPolicy = interruptionPolicy;
            InertiaSuppression = inertiaSuppression;
        }
    }

    /// <summary>仲裁中的语义需求；可选播放起点仍是调用意图，具体资源默认值留给资源解析。</summary>
    internal readonly struct CharacterAnimationDemand
    {
        public string Key { get; }
        public long InstanceId { get; }
        public CharacterAnimationBodyRegion Region { get; }
        public int Priority { get; }
        private readonly float weight;
        internal float MaximumWeight => weight;
        internal float Coverage => Region != CharacterAnimationBodyRegion.FullBody ? Playback?.Coverage ?? 1f : 1f;
        public float Weight => weight * Coverage;
        public float? StartNormalizedTime { get; }
        public CharacterAnimationInterruptionPolicy InterruptionPolicy { get; }
        public long SubmissionOrder { get; }
        public Vector3 WeightedInertiaSuppression { get; }
        public CharacterActionPlayback Playback { get; }
        public CharacterLocomotionVariant LocomotionVariant { get; }
        public bool IsValid => Key != null;
        public bool IsLocomotion => Key == CharacterAnimationIntentConverter.WalkKey ||
            Key == CharacterAnimationIntentConverter.MoveKey || Key == CharacterAnimationIntentConverter.SprintKey;

        public CharacterAnimationDemand(CharacterAnimationRule rule, CharacterAnimationRequest request,
            float weight, float? startNormalizedTime, long submissionOrder, CharacterActionPlayback playback = null)
        {
            Key = rule.Key;
            InstanceId = request.InstanceId;
            Region = rule.Region;
            Priority = rule.Priority;
            this.weight = weight;
            WeightedInertiaSuppression = rule.InertiaSuppression * weight;
            StartNormalizedTime = startNormalizedTime;
            InterruptionPolicy = rule.InterruptionPolicy;
            SubmissionOrder = submissionOrder;
            Playback = playback;
            LocomotionVariant = request.LocomotionVariant;
        }
    }
}
