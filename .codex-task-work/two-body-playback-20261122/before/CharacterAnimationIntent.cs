using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>需求覆盖的身体范围；局部动画由 Animator 层上的 AvatarMask 限定骨骼。</summary>
    public enum CharacterAnimationBodyRegion
    {
        [InspectorName("全身（基础层素材）")] FullBody,
        [InspectorName("上半身")] UpperBody,
        [InspectorName("下半身")] LowerBody,
        [InspectorName("全身（叠加接管）")] FullBodyOverlay
    }

    public enum CharacterPartnerMode
    {
        [InspectorName("允许所有")] Any,
        [InspectorName("只允许列表中的动作")] AllowListed,
        [InspectorName("禁止列表中的动作")] DenyListed
    }

    [Serializable]
    public struct CharacterActionPartners
    {
        [InspectorName("另一部位的动作")] public CharacterPartnerMode Mode;
        [InspectorName("动作键")] public string[] Keys;

        internal bool Allows(string key)
        {
            if (Mode == CharacterPartnerMode.Any) return true;
            bool listed = Keys != null && Array.IndexOf(Keys, key) >= 0;
            return Mode == CharacterPartnerMode.AllowListed ? listed : !listed;
        }
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
        [InspectorName("申请部位"), Tooltip("全身动作同时申请上下身。按素材绑定选择基础层或叠加播放。")]
        public CharacterAnimationBodyRegion Region;
        [InspectorName("局部动作兼容"), Tooltip("上身和下身动作必须互相允许。未被接管的部位继续基础姿势。")]
        public CharacterActionPartners Partners;
        [SerializeField, InspectorName("单次动作接管"), Tooltip("可选。行为有招式时钟时跟随招式，独立调用时跟随片段进度。")]
        internal CharacterActionHandoffSettings Handoff = new CharacterActionHandoffSettings();
        [Tooltip("数值越大越优先。同优先级先处理全身申请，同范围再按新实例提交顺序决定。")]
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
    internal sealed class CharacterAnimationRule
    {
        public string Key { get; }
        public CharacterAnimationBodyRegion Region { get; }
        public int Priority { get; }
        public CharacterAnimationInterruptionPolicy InterruptionPolicy { get; }
        public Vector3 InertiaSuppression { get; }
        public CharacterActionPartners Partners { get; }
        public CharacterActionHandoffSettings Handoff { get; }
        public bool WholeBody => Region == CharacterAnimationBodyRegion.FullBody || Region == CharacterAnimationBodyRegion.FullBodyOverlay;

        public CharacterAnimationRule(string key, CharacterAnimationBodyRegion region, int priority,
            CharacterAnimationInterruptionPolicy interruptionPolicy = CharacterAnimationInterruptionPolicy.Replay,
            Vector3 inertiaSuppression = default, CharacterActionPartners partners = default, CharacterActionHandoffSettings handoff = null)
        {
            Key = key;
            Region = region;
            Priority = priority;
            InterruptionPolicy = interruptionPolicy;
            InertiaSuppression = inertiaSuppression;
            Partners = partners;
            Handoff = handoff;
        }
    }

    /// <summary>仲裁中的语义需求；可选播放起点仍是调用意图，具体资源默认值留给资源解析。</summary>
    internal struct CharacterAnimationDemand
    {
        public CharacterAnimationRule Rule { get; }
        public string Key => Rule?.Key;
        public long InstanceId { get; }
        public CharacterAnimationBodyRegion Region => Rule?.Region ?? CharacterAnimationBodyRegion.FullBody;
        public int Priority => Rule.Priority;
        private readonly float weight;
        internal float MaximumWeight => weight;
        public CharacterActionTiming Timing { get; private set; }
        public float EntryCoverage { get; private set; }
        internal bool HasHandoff => Region != CharacterAnimationBodyRegion.FullBody && Timing.Handoff.Valid;
        internal float Coverage => HasHandoff
            ? Timing.Handoff.WeightAt(Timing.SamplePhase.GetValueOrDefault(), EntryCoverage) : 1f;
        public float Weight => weight * Coverage;
        public float? StartNormalizedTime { get; }
        public CharacterAnimationInterruptionPolicy InterruptionPolicy => Rule.InterruptionPolicy;
        public long SubmissionOrder { get; }
        public Vector3 WeightedInertiaSuppression { get; }
        public CharacterActionPlayback Playback { get; }
        public CharacterLocomotionVariant LocomotionVariant { get; }
        public bool IsValid => Rule != null;
        public bool IsLocomotion => Key == CharacterAnimationIntentConverter.WalkKey ||
            Key == CharacterAnimationIntentConverter.MoveKey || Key == CharacterAnimationIntentConverter.SprintKey;

        public bool Precedes(in CharacterAnimationDemand other)
        {
            if (Priority != other.Priority) return Priority > other.Priority;
            if (Rule.WholeBody != other.Rule.WholeBody) return Rule.WholeBody;
            return SubmissionOrder > other.SubmissionOrder;
        }

        public CharacterAnimationDemand WithTiming(CharacterActionTiming timing)
        {
            var result = this;
            result.Timing = timing;
            return result;
        }

        public CharacterAnimationDemand WithEntryCoverage(float entryCoverage)
        {
            var result = this;
            result.EntryCoverage = entryCoverage;
            return result;
        }

        public CharacterAnimationDemand(CharacterAnimationRule rule, CharacterAnimationRequest request,
            float weight, float? startNormalizedTime, long submissionOrder, CharacterActionPlayback playback = null)
        {
            Rule = rule;
            InstanceId = request.InstanceId;
            this.weight = weight;
            Timing = default;
            EntryCoverage = 0f;
            WeightedInertiaSuppression = rule.InertiaSuppression * weight;
            StartNormalizedTime = startNormalizedTime;
            SubmissionOrder = submissionOrder;
            Playback = playback;
            LocomotionVariant = request.LocomotionVariant;
        }
    }
}
