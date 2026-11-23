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
        public float StartNormalizedTime { get; }
        public float TransitionDuration { get; }
        public float? FixedStartSeconds { get; }
        public float LayerBlendSeconds { get; }

        public CharacterAnimationLayerPlan(CharacterAnimationDemand demand, CharacterAnimationBinding binding, CharacterAnimationPlaybackCommand command,
            CharacterAnimationPlaybackReason reason, bool usedResourceFallback,
            float weight, float start, float transition, float blendSeconds, float? fixedStartSeconds = null)
        {
            HasState = true;
            Demand = demand;
            Binding = binding;
            Command = command;
            Reason = reason;
            UsedResourceFallback = usedResourceFallback;
            Weight = weight;
            StartNormalizedTime = start;
            TransitionDuration = transition;
            FixedStartSeconds = fixedStartSeconds;
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
            CharacterAnimationLayerPlan lowerBody, CharacterAnimationLayerPlan upperBody,
            CharacterAnimationPlaybackRates rates, float? stopClipTime, bool stopObserved,
            float stopRate, CharacterStopPoseSample stopPose, CharacterReleaseDecision releaseDecision,
            float stopEntryTime = 0f, float stopEntryTravelPhase = 0f)
        {
            FullBody = fullBody;
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
            else if (!previous.HasState || sameResource)
            {
                command = CharacterAnimationPlaybackCommand.Play;
                reason = previous.HasState ? CharacterAnimationPlaybackReason.ReplayInstance : CharacterAnimationPlaybackReason.Enter;
            }
            else
            {
                command = shouldFade ? CharacterAnimationPlaybackCommand.CrossFade : CharacterAnimationPlaybackCommand.Play;
                reason = CharacterAnimationPlaybackReason.SwitchState;
            }
            return command;
        }
    }
}
