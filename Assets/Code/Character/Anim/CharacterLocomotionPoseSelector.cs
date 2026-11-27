using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterReleaseDecision
    {
        None, ContinueStride, WaitForEvaluation, EnterOnContact, EnterOnBraking, FollowStop,
        FeetOverridden, Finished, MissingStop, Interrupted, EnterOnMatch
    }

    // 保存一次部位仲裁对应的下身计划和足部匹配许可。
    internal readonly struct CharacterPoseOwners
    {
        internal readonly CharacterAnimationComposition Selection;
        internal readonly CharacterAnimationLayerPlan Lower;
        internal readonly bool LowerCoversFeet, MatchRecovery;

        // 记录已选择的部位及其对足部匹配的影响。
        internal CharacterPoseOwners(in CharacterAnimationComposition selection, in CharacterAnimationLayerPlan lower,
            bool lowerCoversFeet, bool matchRecovery)
        {
            Selection = selection;
            Lower = lower;
            LowerCoversFeet = lowerCoversFeet;
            MatchRecovery = matchRecovery;
        }
    }

    // 保存一次完整选姿的部位归属、收步决策和匹配结果。
    internal readonly struct CharacterPoseCandidate
    {
        internal readonly CharacterPoseOwners Owners;
        internal readonly CharacterReleaseDecision Release;
        internal readonly CharacterMatchResult Match;

        // 将本次部位归属与已确定的地面决策作为整体交给播放规划。
        internal CharacterPoseCandidate(in CharacterPoseOwners owners, CharacterReleaseDecision release, in CharacterMatchResult match)
        {
            Owners = owners;
            Release = release;
            Match = match;
        }
    }

    /// <summary>基于已选部位和播放观测决定地面入点及各层播放计划。</summary>
    internal sealed class CharacterLocomotionPoseSelector
    {
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly AnimationDecisionEvaluator releaseDecisions;
        private readonly AnimationClip runStartClip;
        private readonly float walkToRunBlend;
        private float? runStartSeconds;

        // 缓存配置中的起步素材长度，供切换走跑时补齐未配置的库状态时长。
        internal void BindClips() => runStartSeconds = runStartClip ? runStartClip.length : (float?)null;

        // 绑定播放库并为当前角色建立收步时机的求值工作区。
        public CharacterLocomotionPoseSelector(CharacterAnimationResourceCatalog resources, CharacterAnimationConfiguration config)
        {
            this.resources = resources;
            releaseDecisions = new AnimationDecisionEvaluator(config.Decisions, AnimationDecisionStage.ReleaseTiming);
            runStartClip = config.RunStartClip;
            walkToRunBlend = InputMath.NonNegative(config.WalkToRunBlendSeconds, 0.14f);
        }

        // 规划下身和全身接管的交接，判断双脚是否可以参与匹配。
        public CharacterPoseOwners SelectOwners(in CharacterAnimationComposition owners, in CharacterPlaybackObservation seen,
            in CharacterAnimationPlaybackPlan previous, bool useMovingVariant)
        {
            var lower = SelectLayer(owners.LowerBody, seen.Lower, previous.LowerBody, useMovingVariant);
            float overlayWeight = resources.LayerWeight(CharacterAnimationSource.FullBodyOverlay);
            float projectedLower = resources.ProjectLayerWeight(CharacterAnimationSource.LowerBody, lower);
            var overlay = owners.Overlay;
            var timing = overlay.Timing;
            bool coversFeet = overlay.IsValid || overlayWeight > 0f || projectedLower > 0f;
            bool matchRecovery = timing.Sample.Valid && seen.Overlay.Ready && overlay.Playback == previous.Overlay.Demand.Playback &&
                overlayWeight < 1f && owners.FullBody.IsLocomotion && !owners.LowerBody.IsValid && projectedLower <= 0f &&
                resources.LayerWeight(CharacterAnimationSource.LowerBody) <= 0f &&
                timing.Sample.Fading;
            return new CharacterPoseOwners(owners, lower, coversFeet, matchRecovery);
        }

        // 按本次部位归属评估地面匹配和收步决策，返回完整候选。
        public CharacterPoseCandidate EvaluateCandidate(in CharacterPoseOwners owners, CharacterAnimationIntentConverter intent,
            CharacterMotionMatcher matcher, in CharacterPresentationInput input, in CharacterPlaybackObservation seen,
            in CharacterAnimationPlaybackPlan committedPlan)
        {
#if UNITY_EDITOR
            releaseDecisions.ClearTrace();
#endif
            var fullBody = owners.Selection.FullBody;
            matcher.Input = input;
            matcher.PrepareReturn(input.Forecast, input.Time, fullBody, owners.LowerCoversFeet, owners.MatchRecovery, input.Motion, input.Feet,
                input.Behavior, intent.Phase);
            var release = DecideRelease(intent, fullBody, owners.LowerCoversFeet, seen, committedPlan, input.Motion.ActualSpeed);
            var match = matcher.Evaluate(intent, fullBody, owners.LowerCoversFeet, owners.MatchRecovery, seen, committedPlan,
                input.Motion, input.Feet, input.DeltaTime, input.Behavior);
            if (intent.Phase == CharacterLocomotionPhase.BrakeLeadIn)
            {
                if (match.Choice == CharacterMatchChoice.Stop && !intent.MainBrakeDue)
                    release = CharacterReleaseDecision.EnterOnMatch;
                else if (match.Choice == CharacterMatchChoice.Continue)
                    release = CharacterReleaseDecision.ContinueStride;
            }
            return new CharacterPoseCandidate(owners, release, match);
        }

        // 按实际部位归属、播放游标和当前阶段确定收步结果。
        private CharacterReleaseDecision DecideRelease(CharacterAnimationIntentConverter intent,
            CharacterAnimationDemand fullBody, bool lowerCoversFeet, in CharacterPlaybackObservation seen,
            in CharacterAnimationPlaybackPlan last, float speed)
        {
            bool pending = intent.Phase == CharacterLocomotionPhase.BrakeLeadIn;
            if (!pending && intent.Phase != CharacterLocomotionPhase.Stopping)
            {
                bool wasReleasing = last.FullBody.Demand.LocomotionVariant == CharacterLocomotionVariant.Stop ||
                    last.ReleaseDecision == CharacterReleaseDecision.ContinueStride ||
                    last.ReleaseDecision == CharacterReleaseDecision.WaitForEvaluation;
                return wasReleasing ? CharacterReleaseDecision.Interrupted : CharacterReleaseDecision.None;
            }
            if (lowerCoversFeet || !fullBody.IsLocomotion)
                return CharacterReleaseDecision.FeetOverridden;
            if (!seen.Full.Ready) return CharacterReleaseDecision.WaitForEvaluation;
            if (pending)
                return DecideReleaseTiming(intent.MainBrakeDue, last.FullBody.Demand.IsLocomotion, seen.EarlyStance, speed);
            if (last.FullBody.Demand.InstanceId == intent.Current.InstanceId &&
                last.FullBody.Demand.LocomotionVariant == CharacterLocomotionVariant.Stop)
            {
                if (!last.FullBody.Binding.IsStop) return CharacterReleaseDecision.MissingStop;
                var actual = seen.Full.Destination;
                if (actual.fullPathHash != last.FullBody.StateHash)
                    return last.StopObserved ? CharacterReleaseDecision.Finished : CharacterReleaseDecision.WaitForEvaluation;
                if (actual.normalizedTime >= last.FullBody.Binding.StopExitPhase) return CharacterReleaseDecision.Finished;
            }
            return CharacterReleaseDecision.FollowStop;
        }

        // 只在有效落脚时允许策略提前收步，物理制动到期后必须推进收步阶段。
        private CharacterReleaseDecision DecideReleaseTiming(bool mainBrakeDue, bool lastLocomotion, bool earlyStance, float speed)
        {
            releaseDecisions.Begin(AnimationDecisionStage.ReleaseTiming);
            releaseDecisions.Set(AnimationDecisionInput.MainBrakeDue, mainBrakeDue);
            releaseDecisions.Set(AnimationDecisionInput.LastLocomotion, lastLocomotion);
            releaseDecisions.Set(AnimationDecisionInput.EarlyStance, earlyStance);
            releaseDecisions.Set(AnimationDecisionInput.Speed, speed);
            bool startStop = releaseDecisions.Flag(AnimationDecisionOutput.StartStop);
            if (mainBrakeDue) return CharacterReleaseDecision.EnterOnBraking;
            return startStop && lastLocomotion && earlyStance
                ? CharacterReleaseDecision.EnterOnContact : CharacterReleaseDecision.ContinueStride;
        }

#if UNITY_EDITOR
        // 将当前试选的收步时机表达式记录交给动画诊断。
        internal void AppendDecisionTrace(System.Text.StringBuilder trace) => releaseDecisions.AppendTrace(trace);
#endif

        // 生成各播放源的最终指令，并计算步频和收步调速参数。
        public CharacterAnimationPlaybackPlan Select(in CharacterPoseCandidate candidate, bool useMovingVariant,
            in CharacterPlaybackObservation seen, CharacterAnimationMotion motion, Vector3 entryVelocity, in CharacterAnimationPlaybackPlan last)
        {
            ref readonly var owners = ref candidate.Owners.Selection;
            var match = candidate.Match;
            bool waitingAtZero = match.Choice == CharacterMatchChoice.Continue &&
                owners.FullBody.Key == CharacterAnimationIntentConverter.IdleKey;
            if ((!owners.FullBody.IsLocomotion && !waitingAtZero) ||
                (candidate.Owners.LowerCoversFeet && (!candidate.Owners.MatchRecovery || match.Choice == CharacterMatchChoice.Stop)))
                match = default;
            var full = SelectLayer(owners.FullBody, seen.Full, last.FullBody, useMovingVariant, match, seen.LeftSupport);
            if (match.Choice == CharacterMatchChoice.Continue && full.Binding.IsStop)
                entryVelocity = last.StopPose.EntryVelocity;
            var upper = SelectLayer(owners.UpperBody, seen.Upper, last.UpperBody, useMovingVariant);
            var overlay = SelectLayer(owners.Overlay, seen.Overlay, last.Overlay, useMovingVariant);
            // 新入场片段维持原速时，尚在淡出的走跑仍需自己的实际步频。
            float? speed = motion.IsValid ? InputMath.NonNegative(motion.ActualSpeed, 0f) : motion.RetainedStrideSpeed ?? last.StrideSpeed;
            float? startSpeed = motion.IsValid ? GroundPlaybackPace.StartSpeed(motion, resources.WalkSpeed) : speed;
            float? clipTime = null;
            bool observed = false;
            float stopRate = 1f;
            var stop = default(CharacterStopPoseSample);
            float entryTime = 0f, entryTravelPhase = 0f;
            if (full.Binding.IsStop)
            {
                bool keep = full.Command == CharacterAnimationPlaybackCommand.Keep;
                var actual = seen.Full.Destination;
                bool matching = seen.Full.Ready && actual.fullPathHash == full.StateHash;
                observed = keep && (matching || last.StopObserved);
                entryTime = keep ? last.StopEntryTime : full.Start.OffsetSeconds.GetValueOrDefault();
                if (!keep) clipTime = entryTime;
                else if (matching && seen.Full.DestinationLength > 0f)
                    clipTime = actual.normalizedTime * seen.Full.DestinationLength;
                else if (keep && !observed) clipTime = last.StopClipTime;
                float marker = full.Binding.BrakingClipSeconds;
                var trajectory = motion.Trajectory;
                float distance = trajectory.MainBrakeTravel + trajectory.RemainingStopDistance;
                float travelPhase = trajectory.IsBraking && !trajectory.IsBrakeLeadIn && distance > 0f
                    ? 1f - Mathf.Sqrt(Mathf.Clamp01(trajectory.RemainingStopDistance / distance)) : 0f;
                entryTravelPhase = keep ? last.StopEntryTravelPhase : travelPhase;
                if (marker > 0f && entryVelocity.sqrMagnitude > 0f)
                {
                    float load = keep ? last.StopPose.Load : 0f;
                    if (trajectory.IsBrakeLeadIn && entryTime <= 0f) load = 0f;
                    else if (clipTime.HasValue)
                        load = CharacterStopPoseSample.EvaluateLoad(clipTime.Value, keep ? seen.Full.DestinationLength : match.Length,
                            marker, travelPhase, load);
                    stop = new CharacterStopPoseSample(full.Demand.InstanceId,
                        full.Binding.Variant == CharacterLocomotionVariant.StopLeft, entryVelocity, load);
                }
                if (clipTime.HasValue || trajectory.IsBrakeLeadIn)
                    stopRate = GroundPlaybackPace.BrakeRate(trajectory, marker, clipTime.GetValueOrDefault(), entryTime, entryTravelPhase);
            }
            return new CharacterAnimationPlaybackPlan(fullBody: full, lowerBody: candidate.Owners.Lower, upperBody: upper, overlay: overlay,
                speed, clipTime, observed, stopRate, stop, candidate.Release, entryTime, entryTravelPhase, startSpeed);
        }

        // 解析一个播放源的片段、入点、相位和淡入方式。
        private CharacterAnimationLayerPlan SelectLayer(CharacterAnimationDemand demand, in CharacterLayerObservation layer,
            in CharacterAnimationLayerPlan previous, bool useMovingVariant, CharacterMatchResult match = default, bool? leftSupport = null)
        {
            var selected = ResolveLayer(demand, layer, previous, useMovingVariant, match, leftSupport);
            return new CharacterAnimationLayerPlan(selected, previous, resources.Transitions);
        }

        // 按本次行为和选姿结果确定原始资源、入点与动作交接时钟。
        private CharacterAnimationLayerPlan ResolveLayer(CharacterAnimationDemand demand, in CharacterLayerObservation layer,
            in CharacterAnimationLayerPlan previous, bool useMovingVariant, CharacterMatchResult match, bool? leftSupport)
        {
            if (match.Choice == CharacterMatchChoice.Continue && previous.HasState)
            {
                var continued = demand.IsLocomotion && !previous.Binding.IsStop ? demand : previous.Demand;
                return new CharacterAnimationLayerPlan(continued, previous.Binding, CharacterAnimationPlaybackCommand.Keep,
                    CharacterAnimationPlaybackReason.ContinueMatch, previous.Weight,
                    previous.Start, previous.Blend, previous.LayerBlendSeconds, previous.ResumeLoop);
            }
            if (!demand.IsValid) return new CharacterAnimationLayerPlan(previous.LayerBlendSeconds);
            CharacterAnimationBinding binding;
            bool stopping = demand.LocomotionVariant == CharacterLocomotionVariant.Stop;
            bool matchedEntry = match.HasEntry && stopping == match.Binding.IsStop;
            if (matchedEntry)
                binding = match.Binding;
            else if (stopping && !match.RejectPreviousStop && previous.Demand.LocomotionVariant == CharacterLocomotionVariant.Stop &&
                previous.Demand.InstanceId == demand.InstanceId)
                binding = previous.Binding;
            else if (stopping)
                binding = resources.ResolveStop(demand.Key, leftSupport);
            else
                binding = resources.ResolveBinding(demand, useMovingVariant);

            float? requestedStart = demand.StartNormalizedTime ?? demand.Timing.StartClipPhase;
            float start = requestedStart ?? binding.Start;
            bool resumeLoop = !matchedEntry && previous.Demand.Playback != null && demand.IsLocomotion &&
                binding.Variant == CharacterLocomotionVariant.None && !requestedStart.HasValue;
            float normalizedBlend = previous.HasState ? Mathf.Min(binding.Transition, previous.Binding.ExitTransition) : binding.Transition;
            var blend = CharacterBlendDuration.SourceFraction(normalizedBlend);
            bool sameStart = binding.Variant == CharacterLocomotionVariant.Start &&
                previous.Binding.Variant == CharacterLocomotionVariant.Start && previous.Demand.InstanceId == demand.InstanceId;
            bool changing = previous.HasState && previous.StateHash != binding.StateHash;
            bool sync = false;
            if (!matchedEntry && changing && !requestedStart.HasValue &&
                (binding.Variant == CharacterLocomotionVariant.None || previous.Binding.IsStop || sameStart) &&
                TryMapEntry(previous.Binding, binding, layer, out float mapped))
            {
                start = mapped;
                sync = true;
            }
            bool walkStartToRun = changing && previous.Demand.LocomotionVariant == CharacterLocomotionVariant.Start &&
                previous.Demand.Key == CharacterAnimationIntentConverter.WalkKey && demand.Key == CharacterAnimationIntentConverter.MoveKey &&
                (sameStart || binding.Variant == CharacterLocomotionVariant.None);
            if (matchedEntry) start = match.Time / match.Length;
            var entry = CharacterClipStart.Normalized(start);
            if (walkStartToRun)
            {
                float? seconds = matchedEntry ? match.Length : resources.StateSeconds(binding.StateHash);
                if (sameStart) seconds ??= runStartSeconds;
                if (seconds.HasValue) entry = CharacterClipStart.Fixed(start * seconds.Value, start);
                blend = CharacterBlendDuration.Seconds(walkToRunBlend);
            }
            else if (stopping || demand.Key == CharacterAnimationIntentConverter.IdleKey)
            {
                entry = CharacterClipStart.Fixed(matchedEntry ? match.Time : 0f, start);
                blend = CharacterBlendDuration.Seconds(normalizedBlend * layer.SourceLength);
            }
            if (matchedEntry)
            {
                bool crossFade = blend.Value > 0f && previous.HasState;
                return new CharacterAnimationLayerPlan(demand, binding, crossFade ? CharacterAnimationPlaybackCommand.CrossFade :
                    CharacterAnimationPlaybackCommand.Play, CharacterAnimationPlaybackReason.MatchPose,
                    demand.Weight, entry, blend, binding.LayerBlend, resumeLoop);
            }
            demand = PrepareHandoff(demand, previous, binding.StateHash, start);
            var timing = demand.Timing;
            bool clockBlend = timing.Sample.Valid;
            if (clockBlend)
                blend = CharacterBlendDuration.Seconds(timing.Sample.RemainingEntry);
            var command = ChooseCommand(demand, binding, previous, blend.Value > 0f, out var reason);
            if (sync) reason = CharacterAnimationPlaybackReason.SynchronizeGait;
            if (command == CharacterAnimationPlaybackCommand.CrossFade && demand.HasHandoff)
            {
                var handoff = timing.Handoff;
                if (!clockBlend)
                {
                    float sourceSeconds = layer.SourceLength > 0f ? layer.SourceLength : previous.Demand.Timing.Handoff.Duration;
                    float untilFull = handoff.Duration * (handoff.FullFrom - (timing.WeightPhase ?? start));
                    blend = CharacterBlendDuration.Seconds(Mathf.Min(normalizedBlend * sourceSeconds, Mathf.Max(0f, untilFull)));
                }
                float? clipLength = resources.StateSeconds(binding.StateHash);
                entry = clipLength.HasValue ? CharacterClipStart.Fixed(start * clipLength.Value, start)
                    : CharacterClipStart.Normalized(start);
                if (blend.Value <= 0f) command = CharacterAnimationPlaybackCommand.Play;
            }
            return new CharacterAnimationLayerPlan(demand, binding, command, reason, demand.Weight,
                entry, blend, demand.HasHandoff ? 0f : binding.LayerBlend, resumeLoop);
        }

        // 依据上次提交的实例和片段判断续播、重播或淡入切换。
        private static CharacterAnimationPlaybackCommand ChooseCommand(CharacterAnimationDemand demand,
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
                reason = sameInstance ? CharacterAnimationPlaybackReason.KeepInstance :
                    CharacterAnimationPlaybackReason.KeepSharedResource;
                command = CharacterAnimationPlaybackCommand.Keep;
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

        // 以当前混合权重和动作时钟建立接管起点；已开始的动作沿用发布时的覆盖量。
        private CharacterAnimationDemand PrepareHandoff(CharacterAnimationDemand demand, in CharacterAnimationLayerPlan previous,
            int stateHash, float start)
        {
            bool continuing = demand.Playback != null && demand.Playback == previous.Demand.Playback;
            var settings = demand.Rule.Handoff;
            if (demand.Playback != null && !demand.Timing.HasWeight && !demand.Timing.WeightPhase.HasValue && settings != null)
            {
                var handoff = continuing ? previous.Demand.Timing.Handoff : default;
                if (!handoff.Valid) handoff = settings.Resolve(resources.StateSeconds(stateHash) ?? settings.Duration);
                float phase = continuing ? Mathf.Max(start, Mathf.Clamp01(demand.Playback.LastObservedPhase)) : start;
                demand = demand.WithTiming(new CharacterActionTiming(null, phase, handoff, settings.StartAtZero, false, followClip: true));
            }
            if (!demand.HasHandoff) return demand;
            if (demand.Timing.Sample.Valid && demand.Playback?.EntryCoverage is float captured)
                return demand.WithEntryCoverage(captured);
            if (continuing) return demand.WithEntryCoverage(previous.Demand.EntryCoverage);
            float applied = previous.HasState || previous.LayerBlendSeconds > 0f ? resources.LayerWeight(demand.Source) : 0f;
            float coverage = demand.Timing.StartAtZero || demand.MaximumWeight <= 0f ? 0f : applied / demand.MaximumWeight;
            return demand.WithEntryCoverage(coverage);
        }

        private static bool TryMapEntry(CharacterAnimationBinding source, CharacterAnimationBinding target,
            in CharacterLayerObservation layer, out float start)
        {
            start = 0f;
            if (!target.Gait.IsValid || !layer.Ready) return false;
            var state = layer.InTransition && layer.Next.fullPathHash == source.StateHash ? layer.Next : layer.Current;
            if (state.fullPathHash != source.StateHash) return false;
            if (source.IsStop)
            {
                bool left = source.Variant == CharacterLocomotionVariant.StopLeft;
                start = target.Gait.ContactTime(state.normalizedTime < CharacterStopPoseSample.SupportTransferPhase ? left : !left);
                return true;
            }
            return state.loop && source.Gait.TryMap(state.normalizedTime, target.Gait, out start);
        }
    }
}
