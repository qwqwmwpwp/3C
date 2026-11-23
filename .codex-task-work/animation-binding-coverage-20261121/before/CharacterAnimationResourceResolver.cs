using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterAnimationPlaybackCommand { Keep, Play, CrossFade }

    internal enum CharacterAnimationPlaybackReason
    {
        Unselected,
        KeepInstance,
        KeepSharedResource,
        Enter,
        SwitchState,
        ReplayInstance,
        SynchronizeGait,
        MatchPose
    }

    internal readonly struct CharacterClipStart
    {
        public readonly float Phase;
        public readonly float? OffsetSeconds;

        private CharacterClipStart(float phase, float? seconds)
        {
            Phase = phase;
            OffsetSeconds = seconds;
        }

        public static CharacterClipStart Normalized(float phase) => new CharacterClipStart(phase, null);
        public static CharacterClipStart Fixed(float seconds, float phase) => new CharacterClipStart(phase, seconds);
        public float InSeconds(float clipLength) => OffsetSeconds ?? Phase * clipLength;
    }

    internal readonly struct CharacterBlendDuration
    {
        public readonly float Value;
        public readonly bool IsSeconds;

        private CharacterBlendDuration(float value, bool isSeconds)
        {
            Value = value;
            IsSeconds = isSeconds;
        }

        public static CharacterBlendDuration SourceFraction(float value) => new CharacterBlendDuration(value, false);
        public static CharacterBlendDuration Seconds(float value) => new CharacterBlendDuration(value, true);
        public float InSeconds(float sourceLength) => IsSeconds ? Value : Value * sourceLength;
        public float InSourceFraction(float sourceLength) => !IsSeconds ? Value : sourceLength > 0f ? Value / sourceLength : 0f;
    }

    /// <summary>资源解析后的单层执行指令；播放端无需再判断动作语义或素材回退。</summary>
    internal readonly struct CharacterAnimationLayerPlan
    {
        public bool HasState { get; }
        public CharacterAnimationDemand Demand { get; }
        public CharacterAnimationBinding Binding { get; }
        public string StateName => Binding.StateName;
        public int StateHash => Binding.StateHash;
        public CharacterAnimationPlaybackCommand Command { get; }
        public CharacterAnimationPlaybackReason Reason { get; }
        public bool UsedResourceFallback { get; }
        public float Weight { get; }
        public CharacterClipStart Start { get; }
        public CharacterBlendDuration Blend { get; }
        public bool ResumeLoop { get; }
        public float LayerBlendSeconds { get; }

        public CharacterAnimationLayerPlan(CharacterAnimationDemand demand, CharacterAnimationBinding binding, CharacterAnimationPlaybackCommand command,
            CharacterAnimationPlaybackReason reason, bool usedResourceFallback,
            float weight, CharacterClipStart start, CharacterBlendDuration transition, float blendSeconds,
            bool resumeLoop = false)
        {
            HasState = true;
            Demand = demand;
            Binding = binding;
            Command = command;
            Reason = reason;
            UsedResourceFallback = usedResourceFallback;
            Weight = weight;
            Start = start;
            Blend = transition;
            ResumeLoop = resumeLoop;
            LayerBlendSeconds = blendSeconds;
        }

        public CharacterAnimationLayerPlan(float blendSeconds) : this()
        {
            LayerBlendSeconds = blendSeconds;
        }
    }

    /// <summary>最终播放计划：各层播放指令、播放倍率与收步进度。</summary>
    internal readonly struct CharacterAnimationPlaybackPlan
    {
        public CharacterAnimationLayerPlan FullBody { get; }
        public CharacterAnimationLayerPlan Overlay { get; }
        public CharacterAnimationLayerPlan LowerBody { get; }
        public CharacterAnimationLayerPlan UpperBody { get; }
        public CharacterAnimationPlaybackRates Rates { get; }
        public float? StopClipTime { get; }
        public bool StopObserved { get; }
        public float StopRate { get; }
        public float StopEntryTime { get; }
        public float StopEntryTravelPhase { get; }
        public CharacterStopPoseSample StopPose { get; }
        public CharacterReleaseDecision ReleaseDecision { get; }

        public CharacterAnimationPlaybackPlan(CharacterAnimationLayerPlan fullBody,
            CharacterAnimationLayerPlan lowerBody, CharacterAnimationLayerPlan upperBody, CharacterAnimationLayerPlan overlay,
            CharacterAnimationPlaybackRates rates, float? stopClipTime, bool stopObserved,
            float stopRate, CharacterStopPoseSample stopPose, CharacterReleaseDecision releaseDecision,
            float stopEntryTime = 0f, float stopEntryTravelPhase = 0f)
        {
            FullBody = fullBody;
            Overlay = overlay;
            LowerBody = lowerBody;
            UpperBody = upperBody;
            Rates = rates;
            StopClipTime = stopClipTime;
            StopObserved = stopObserved;
            StopRate = stopRate;
            StopEntryTime = stopEntryTime;
            StopEntryTravelPhase = stopEntryTravelPhase;
            StopPose = stopPose;
            ReleaseDecision = releaseDecision;
        }
    }

    /// <summary>将已选的片段与入点转换为一次图层播放命令；只读上次实际提交的结果。</summary>
    internal static class CharacterAnimationResourceResolver
    {
        public static CharacterAnimationPlaybackCommand ChooseCommand(CharacterAnimationDemand demand,
            CharacterAnimationBinding binding, in CharacterAnimationLayerPlan previous, bool shouldFade,
            out CharacterAnimationPlaybackReason reason)
        {
            bool sameResource = previous.HasState && previous.StateHash == binding.StateHash;
            bool consecutive = previous.Demand.IsLocomotion && demand.IsLocomotion;
            bool sameInstance = sameResource && previous.Binding.Variant == binding.Variant &&
                previous.Demand.InstanceId == demand.InstanceId && (previous.Demand.Key == demand.Key ||
                    consecutive && binding.Variant != CharacterLocomotionVariant.None);
            bool continuous = sameResource && consecutive && binding.Variant == CharacterLocomotionVariant.None &&
                (previous.Binding.Variant == CharacterLocomotionVariant.None ||
                    previous.Binding.Variant == CharacterLocomotionVariant.Start);
            CharacterAnimationPlaybackCommand command;
            if (sameInstance || continuous)
            {
                command = CharacterAnimationPlaybackCommand.Keep;
                reason = sameInstance ? CharacterAnimationPlaybackReason.KeepInstance :
                    CharacterAnimationPlaybackReason.KeepSharedResource;
            }
            else if (!previous.HasState)
            {
                command = CharacterAnimationPlaybackCommand.Play;
                reason = CharacterAnimationPlaybackReason.Enter;
            }
            else
            {
                command = shouldFade ? CharacterAnimationPlaybackCommand.CrossFade : CharacterAnimationPlaybackCommand.Play;
                reason = sameResource ? CharacterAnimationPlaybackReason.ReplayInstance : CharacterAnimationPlaybackReason.SwitchState;
            }
            return command;
        }
    }
}
