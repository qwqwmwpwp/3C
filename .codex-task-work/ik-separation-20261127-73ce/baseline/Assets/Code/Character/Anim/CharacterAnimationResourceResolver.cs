using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterAnimationPlaybackCommand { Keep, Play, CrossFade }

    // 记录最终播放命令由哪项资源、规则或匹配决策产生。
    internal enum CharacterAnimationPlaybackReason
    {
        Unselected,
        KeepInstance,
        KeepSharedResource,
        Enter,
        SwitchState,
        ReplayInstance,
        SynchronizeGait,
        MatchPose,
        ContinueMatch
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
    }

    /// <summary>保存单个播放源的片段、播放动作与混合参数。</summary>
    internal readonly struct CharacterAnimationLayerPlan
    {
        internal double? RestoredSeconds { get; }
        public bool HasState { get; }
        public CharacterAnimationDemand Demand { get; }
        public CharacterAnimationBinding Binding { get; }
        public string StateName => Binding.StateName;
        public int StateHash => Binding.StateHash;
        public CharacterAnimationPlaybackCommand Command { get; }
        public CharacterAnimationPlaybackReason Reason { get; }
        public float Weight { get; }
        public CharacterClipStart Start { get; }
        public CharacterBlendDuration Blend { get; }
        internal CharacterTransitionProfile EntryProfile { get; }
        internal CharacterTransitionProfile ExitProfile { get; }
        public bool ResumeLoop { get; }
        public float LayerBlendSeconds { get; }

        // 保存当前播放需求解析出的片段、入点和混合参数。
        public CharacterAnimationLayerPlan(CharacterAnimationDemand demand, CharacterAnimationBinding binding, CharacterAnimationPlaybackCommand command,
            CharacterAnimationPlaybackReason reason, float weight, CharacterClipStart start, CharacterBlendDuration transition, float blendSeconds,
            bool resumeLoop = false)
        {
            RestoredSeconds = null;
            EntryProfile = ExitProfile = null;
            HasState = true;
            Demand = demand;
            Binding = binding;
            Command = command;
            Reason = reason;
            Weight = weight;
            Start = start;
            Blend = transition;
            ResumeLoop = resumeLoop;
            LayerBlendSeconds = blendSeconds;
        }

        // 在统一计划阶段修改可调淡变时长，动作自己的交接时钟保持优先。
        internal CharacterAnimationLayerPlan(in CharacterAnimationLayerPlan source, in CharacterAnimationLayerPlan previous,
            CharacterTransitionProfiles profiles)
        {
            this = source;
            if (profiles == null) return;
            if (!HasState)
            {
                if (previous.HasState && !previous.Demand.HasHandoff)
                    LayerBlendSeconds = previous.ExitProfile?.Duration(LayerBlendSeconds) ?? LayerBlendSeconds;
                return;
            }
            EntryProfile = profiles.Select(source, false);
            ExitProfile = profiles.Select(source, true);
            if (EntryProfile == null || Demand.HasHandoff) return;
            LayerBlendSeconds = EntryProfile.Duration(LayerBlendSeconds);
            if (!previous.HasState || Command == CharacterAnimationPlaybackCommand.Keep) return;
            if (EntryProfile.Method == TransitionMethod.Inertial || EntryProfile.FadeSeconds >= 0f)
                Blend = CharacterBlendDuration.Seconds(EntryProfile.Duration(0f));
            Command = Blend.Value > 0f ? CharacterAnimationPlaybackCommand.CrossFade : CharacterAnimationPlaybackCommand.Play;
        }

        // 延续原图的部位与蒙版，从已显示的素材时刻重建同素材声部。
        internal CharacterAnimationLayerPlan(in CharacterAnimationLayerPlan source, in CharacterLayerObservation seen,
            CharacterClipPlayback clips, CharacterAnimationComposer composer, in CharacterAnimationPlaybackPlan frame,
            in CharacterPlaybackFrameStep step)
        {
            this = source;
            if (!source.HasState) return;
            var cursor = seen.Destination;
            var handle = source.Demand.Playback;
            if (!seen.Ready || cursor.fullPathHash != source.StateHash || !clips.MatchesMaterial(cursor) ||
                handle != null && (handle.EndSource != default || handle.PlaybackId != cursor.PlaybackId ||
                handle.StopSource == CharacterPlaybackEndSource.ResourceUnavailable))
            {
                this = default;
                return;
            }
            Command = CharacterAnimationPlaybackCommand.Keep;
            RestoredSeconds = cursor.Seconds;
            if (handle == null) return;
            var timing = source.Demand.Timing;
            if (handle.Clock.Valid)
                timing = new CharacterActionTiming(handle.Clock.Sample(step.End), handle.Cursor, handle.InitialPhase);
            else if (timing.WeightPhase.HasValue)
            {
                bool followClip = !timing.ClipPhase.HasValue;
                float phase = timing.WeightPhase.Value;
                var latest = composer.CurrentTiming(handle);
                if (followClip && seen.DestinationLength > 0f)
                {
                    float rate = clips.Pace(source.StateHash).Rate(frame.StrideSpeed, frame.StartSpeed, frame.StopRate, out _);
                    phase = Mathf.Clamp01((float)((cursor.Seconds + step.GraphDelta * rate) / seen.DestinationLength));
                }
                else if (latest.WeightPhase.HasValue)
                    phase = Mathf.Lerp(phase, latest.WeightPhase.Value, (float)step.Fraction);
                timing = new CharacterActionTiming(timing.StartClipPhase, phase,
                    latest.Handoff.Valid ? latest.Handoff : timing.Handoff, timing.StartAtZero, timing.Resumed, followClip);
            }
            Demand = Demand.WithTiming(timing);
            Weight = Demand.Weight;
        }

        public float WeightAfter(float current, float deltaTime)
        {
            float target = HasState ? Weight : 0f;
            return LayerBlendSeconds > 0f ? Mathf.MoveTowards(current, target, deltaTime / LayerBlendSeconds) : target;
        }

        public CharacterAnimationLayerPlan(float blendSeconds) : this()
        {
            LayerBlendSeconds = blendSeconds;
        }
    }

    /// <summary>最终播放计划：各层播放指令、共用的物理速度与收步进度。</summary>
    internal readonly struct CharacterAnimationPlaybackPlan
    {
        public CharacterAnimationLayerPlan FullBody { get; }
        public CharacterAnimationLayerPlan UpperOwner { get; }
        public CharacterAnimationLayerPlan LowerOwner { get; }
        private readonly float upperRelease, lowerRelease, sharedRelease;

        public CharacterAnimationLayerPlan Overlay => UpperOwner.HasState &&
            UpperOwner.Demand.Source == CharacterAnimationSource.FullBodyOverlay &&
            UpperOwner.Demand.Key == LowerOwner.Demand.Key && UpperOwner.Demand.InstanceId == LowerOwner.Demand.InstanceId
            ? UpperOwner : new CharacterAnimationLayerPlan(sharedRelease);
        public CharacterAnimationLayerPlan UpperBody => UpperOwner.Demand.Source == CharacterAnimationSource.UpperBody
            ? UpperOwner : new CharacterAnimationLayerPlan(upperRelease);
        public CharacterAnimationLayerPlan LowerBody => LowerOwner.Demand.Source == CharacterAnimationSource.LowerBody
            ? LowerOwner : new CharacterAnimationLayerPlan(lowerRelease);
        public float? StrideSpeed { get; }
        public float? StartSpeed { get; }
        public float? StopClipTime { get; }
        public bool StopObserved { get; }
        public float StopRate { get; }
        public float StopEntryTime { get; }
        public float StopEntryTravelPhase { get; }
        public CharacterStopPoseSample StopPose { get; }
        public CharacterReleaseDecision ReleaseDecision { get; }

        // 按上次真实提交的分层关系生成本段截止时刻的姿态，完成或换素材的声部退出。
        internal CharacterAnimationPlaybackPlan(in CharacterAnimationPlaybackPlan source, CharacterLayerObservation[] seen,
            CharacterClipPlayback clips, CharacterAnimationComposer composer, in CharacterPlaybackFrameStep step)
        {
            this = source;
            FullBody = new CharacterAnimationLayerPlan(source.FullBody, seen[(int)source.FullBody.Demand.Source], clips, composer, source, step);
            UpperOwner = new CharacterAnimationLayerPlan(source.UpperOwner, seen[(int)source.UpperOwner.Demand.Source], clips, composer, source, step);
            LowerOwner = new CharacterAnimationLayerPlan(source.LowerOwner, seen[(int)source.LowerOwner.Demand.Source], clips, composer, source, step);
        }

        // 保存已获选的基础和覆盖计划，给每个部位确定最终姿态来源。
        public CharacterAnimationPlaybackPlan(CharacterAnimationLayerPlan fullBody,
            CharacterAnimationLayerPlan lowerBody, CharacterAnimationLayerPlan upperBody, CharacterAnimationLayerPlan overlay,
            float? strideSpeed, float? stopClipTime, bool stopObserved,
            float stopRate, CharacterStopPoseSample stopPose, CharacterReleaseDecision releaseDecision,
            float stopEntryTime = 0f, float stopEntryTravelPhase = 0f, float? startSpeed = null)
        {
            FullBody = fullBody;
            UpperOwner = overlay.HasState ? overlay : upperBody.HasState ? upperBody : fullBody;
            LowerOwner = overlay.HasState ? overlay : lowerBody.HasState ? lowerBody : fullBody;
            upperRelease = upperBody.LayerBlendSeconds;
            lowerRelease = lowerBody.LayerBlendSeconds;
            sharedRelease = overlay.LayerBlendSeconds;
            StrideSpeed = strideSpeed;
            StartSpeed = startSpeed;
            StopClipTime = stopClipTime;
            StopObserved = stopObserved;
            StopRate = stopRate;
            StopEntryTime = stopEntryTime;
            StopEntryTravelPhase = stopEntryTravelPhase;
            StopPose = stopPose;
            ReleaseDecision = releaseDecision;
        }
    }
}
