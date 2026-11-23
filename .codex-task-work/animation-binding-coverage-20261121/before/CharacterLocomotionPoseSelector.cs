using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterReleaseDecision
    {
        None, ContinueStride, WaitForEvaluation, EnterOnContact, EnterOnBraking, FollowStop,
        FeetOverridden, Finished, MissingStop, Interrupted, EnterOnMatch
    }

    /// <summary>基于已选部位和播放观测决定地面入点及各层播放计划。</summary>
    internal sealed class CharacterLocomotionPoseSelector
    {
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly AnimationClip runStartClip;
        private readonly float walkToRunBlend;

        public CharacterLocomotionPoseSelector(CharacterAnimationResourceCatalog resources, CharacterAnimationConfiguration config)
        {
            this.resources = resources;
            runStartClip = config.RunStartClip;
            walkToRunBlend = InputMath.NonNegative(config.WalkToRunBlendSeconds, 0.14f);
        }

        public CharacterReleaseDecision EvaluateRelease(CharacterAnimationIntentConverter intent, CharacterMotionMatcher matcher,
            in CharacterAnimationComposition selection, in CharacterPlaybackObservation seen, in CharacterAnimationPlaybackPlan committedPlan,
            CharacterAnimationMotion motion, CharacterFootObservation feet, float deltaTime, out CharacterMatchResult match)
        {
            var release = DecideRelease(intent, selection, seen, committedPlan);
            match = matcher.Evaluate(intent, selection, seen, committedPlan, release, motion, feet, deltaTime);
            if (intent.Phase == CharacterLocomotionPhase.BrakeLeadIn)
            {
                if (match.Choice == CharacterMatchChoice.Stop && !intent.MainBrakeDue)
                    release = CharacterReleaseDecision.EnterOnMatch;
                else if (match.Choice == CharacterMatchChoice.Continue)
                    release = CharacterReleaseDecision.ContinueStride;
            }
            return release;
        }

        private CharacterReleaseDecision DecideRelease(CharacterAnimationIntentConverter intent,
            in CharacterAnimationComposition ownership, in CharacterPlaybackObservation seen,
            in CharacterAnimationPlaybackPlan last)
        {
            bool pending = intent.Phase == CharacterLocomotionPhase.BrakeLeadIn;
            if (!pending && intent.Phase != CharacterLocomotionPhase.Stopping)
            {
                bool wasReleasing = last.FullBody.Demand.LocomotionVariant == CharacterLocomotionVariant.Stop ||
                    last.ReleaseDecision == CharacterReleaseDecision.ContinueStride ||
                    last.ReleaseDecision == CharacterReleaseDecision.WaitForEvaluation;
                return wasReleasing ? CharacterReleaseDecision.Interrupted : CharacterReleaseDecision.None;
            }
            if (ownership.LowerBody.IsValid || !ownership.FullBody.IsLocomotion)
                return CharacterReleaseDecision.FeetOverridden;
            if (!seen.Full.Ready) return CharacterReleaseDecision.WaitForEvaluation;
            if (pending)
            {
                if (intent.MainBrakeDue) return CharacterReleaseDecision.EnterOnBraking;
                return last.FullBody.Demand.IsLocomotion && seen.EarlyStance
                    ? CharacterReleaseDecision.EnterOnContact : CharacterReleaseDecision.ContinueStride;
            }
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

        public CharacterAnimationPlaybackPlan Select(in CharacterAnimationComposition owners,
            in CharacterPlaybackObservation seen, CharacterAnimationMotion motion, Vector3 entryVelocity,
            in CharacterAnimationPlaybackPlan last, CharacterReleaseDecision release, CharacterMatchResult match = default)
        {
            bool waitingAtZero = match.Choice == CharacterMatchChoice.Continue &&
                owners.FullBody.Key == CharacterAnimationIntentConverter.IdleKey;
            if ((!owners.FullBody.IsLocomotion && !waitingAtZero) || owners.LowerBody.IsValid) match = default;
            var full = SelectLayer(owners.FullBody, seen.Full, seen, last.FullBody, match);
            if (match.Choice == CharacterMatchChoice.Continue && full.Binding.IsStop)
                entryVelocity = last.StopPose.EntryVelocity;
            var lower = SelectLayer(owners.LowerBody, seen.Lower, seen, last.LowerBody);
            var upper = SelectLayer(owners.UpperBody, seen.Upper, seen, last.UpperBody);
            var overlay = SelectLayer(owners.Overlay, seen.Overlay, seen, last.Overlay);
            // 新入场片段维持原速时，尚在淡出的走跑仍需自己的实际步频。
            float? speed = motion.IsValid ? InputMath.NonNegative(motion.ActualSpeed, 0f) : motion.RetainedStrideSpeed ?? last.Rates.StrideSpeed;
            if (full.Binding.Variant == CharacterLocomotionVariant.Start && motion.IsValid && !motion.IsBlocked && motion.Trajectory.IsValid)
            {
                float forward = Vector3.Dot(motion.Trajectory.FutureVelocity, motion.ExpectedVelocity.normalized);
                float startSpeed = Mathf.Clamp(forward, 0f, Mathf.Min(resources.WalkSpeed, motion.ExpectedSpeed));
                speed = Mathf.Max(motion.ActualSpeed, startSpeed); // 加速首步提前推进起步片段，循环步频仍跟随实速。
            }
            var rates = new CharacterAnimationPlaybackRates(speed, full.Binding.NominalSpeed, resources.WalkSpeed,
                resources.RunSpeed, resources.SprintSpeed, resources.DashSpeed);
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
                else if (matching && seen.DestinationClipLength > 0f)
                    clipTime = actual.normalizedTime * seen.DestinationClipLength;
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
                        load = CharacterStopPoseSample.EvaluateLoad(clipTime.Value, keep ? seen.DestinationClipLength : match.Length,
                            marker, travelPhase, load);
                    stop = new CharacterStopPoseSample(full.Demand.InstanceId,
                        full.Binding.Variant == CharacterLocomotionVariant.StopLeft, entryVelocity, load);
                }
                if (trajectory.IsBrakeLeadIn && marker > 0f) stopRate = 0f;
                else if (trajectory.IsBraking && distance > 0f && trajectory.RemainingStopSeconds > 0f &&
                    clipTime.HasValue && clipTime.Value < marker)
                {
                    float progress = entryTime > 0f ? Mathf.InverseLerp(entryTravelPhase, 1f, travelPhase) : travelPhase;
                    float desired = Mathf.Lerp(entryTime, marker, progress);
                    stopRate = Mathf.Clamp((marker - desired) / trajectory.RemainingStopSeconds +
                        (desired - clipTime.Value) / 0.06f, 0.25f, 2.5f);
                }
            }
            return new CharacterAnimationPlaybackPlan(fullBody: full, lowerBody: lower, upperBody: upper, overlay: overlay,
                rates, clipTime, observed, stopRate, stop, release, entryTime, entryTravelPhase);
        }

        private CharacterAnimationLayerPlan SelectLayer(CharacterAnimationDemand demand, in CharacterLayerObservation layer,
            in CharacterPlaybackObservation seen, in CharacterAnimationLayerPlan previous,
            CharacterMatchResult match = default)
        {
            if (match.Choice == CharacterMatchChoice.Continue && previous.HasState)
                return new CharacterAnimationLayerPlan(previous.Demand, previous.Binding, CharacterAnimationPlaybackCommand.Keep,
                    CharacterAnimationPlaybackReason.KeepInstance, previous.UsedResourceFallback, previous.Weight,
                    previous.Start, previous.Blend, previous.LayerBlendSeconds, previous.ResumeLoop);
            if (!demand.IsValid) return new CharacterAnimationLayerPlan(previous.LayerBlendSeconds);
            CharacterAnimationBinding binding;
            bool fallback;
            bool stopping = demand.LocomotionVariant == CharacterLocomotionVariant.Stop;
            if (match.HasEntry && stopping == match.Binding.IsStop)
            {
                binding = match.Binding;
                fallback = false;
            }
            else if (stopping && previous.Demand.LocomotionVariant == CharacterLocomotionVariant.Stop &&
                previous.Demand.InstanceId == demand.InstanceId)
            {
                binding = previous.Binding;
                fallback = previous.UsedResourceFallback;
            }
            else if (stopping)
                binding = resources.ResolveStop(demand.Key, seen.LeftSupport, out fallback);
            else
                binding = resources.ResolveBinding(demand, out fallback);

            float? requestedStart = demand.StartNormalizedTime ?? demand.Timing.StartPhase;
            float start = requestedStart ?? binding.Start;
            bool resumeLoop = previous.Demand.Playback != null && demand.IsLocomotion &&
                binding.Variant == CharacterLocomotionVariant.None && !requestedStart.HasValue;
            float normalizedBlend = previous.HasState ? Mathf.Min(binding.Transition, previous.Binding.ExitTransition) : binding.Transition;
            var blend = CharacterBlendDuration.SourceFraction(normalizedBlend);
            bool sameStart = binding.Variant == CharacterLocomotionVariant.Start &&
                previous.Binding.Variant == CharacterLocomotionVariant.Start && previous.Demand.InstanceId == demand.InstanceId;
            bool changing = previous.HasState && previous.StateHash != binding.StateHash;
            bool sync = false;
            if (changing && !requestedStart.HasValue &&
                (binding.Variant == CharacterLocomotionVariant.None || previous.Binding.IsStop || sameStart) &&
                TryMapEntry(previous.Binding, binding, layer, out float mapped))
            {
                start = mapped;
                sync = true;
            }
            bool fixedGaitBlend = changing && sameStart && previous.Demand.Key == CharacterAnimationIntentConverter.WalkKey &&
                demand.Key == CharacterAnimationIntentConverter.MoveKey && runStartClip != null;
            var entry = CharacterClipStart.Normalized(start);
            if (fixedGaitBlend)
            {
                entry = CharacterClipStart.Fixed(start * resources.ClipSeconds(runStartClip), start);
                blend = CharacterBlendDuration.Seconds(walkToRunBlend);
            }
            else if (stopping || demand.Key == CharacterAnimationIntentConverter.IdleKey)
            {
                entry = CharacterClipStart.Fixed(0f, start);
                blend = CharacterBlendDuration.Seconds(normalizedBlend * seen.SourceLength);
            }
            if (match.HasEntry && binding.StateHash == match.Binding.StateHash)
            {
                start = match.Time / match.Length;
                entry = stopping ? CharacterClipStart.Fixed(match.Time, start) : CharacterClipStart.Normalized(start);
                bool crossFade = blend.Value > 0f && previous.HasState;
                return new CharacterAnimationLayerPlan(demand, binding, crossFade ? CharacterAnimationPlaybackCommand.CrossFade :
                    CharacterAnimationPlaybackCommand.Play, CharacterAnimationPlaybackReason.MatchPose, fallback,
                    demand.Weight, entry, blend, binding.LayerBlend, resumeLoop);
            }
            var timing = demand.Timing;
            bool phased = demand.Region != CharacterAnimationBodyRegion.FullBody && timing.Handoff.Valid;
            if (phased)
            {
                float entryCoverage = previous.Demand.EntryCoverage;
                if (demand.Playback != previous.Demand.Playback)
                {
                    float applied = previous.HasState || previous.LayerBlendSeconds > 0f ? resources.LayerWeight(demand.Region) : 0f;
                    entryCoverage = timing.StartAtZero || demand.MaximumWeight <= 0f ? 0f : applied / demand.MaximumWeight;
                }
                demand = demand.WithEntryCoverage(entryCoverage);
            }
            var command = CharacterAnimationResourceResolver.ChooseCommand(demand, binding, previous, blend.Value > 0f, out var reason);
            if (sync) reason = CharacterAnimationPlaybackReason.SynchronizeGait;
            if (command == CharacterAnimationPlaybackCommand.CrossFade && phased)
            {
                var handoff = timing.Handoff;
                float sourceSeconds = layer.SourceLength > 0f ? layer.SourceLength : previous.Demand.Timing.Handoff.Duration;
                float untilFull = handoff.Duration * (handoff.FullFrom - (timing.SamplePhase ?? start));
                blend = CharacterBlendDuration.Seconds(Mathf.Min(normalizedBlend * sourceSeconds, Mathf.Max(0f, untilFull)));
                float? clipLength = resources.StateSeconds(binding.StateHash);
                entry = clipLength.HasValue ? CharacterClipStart.Fixed(start * clipLength.Value, start)
                    : CharacterClipStart.Normalized(start);
                if (blend.Value <= 0f) command = CharacterAnimationPlaybackCommand.Play;
            }
            return new CharacterAnimationLayerPlan(demand, binding, command, reason, fallback, demand.Weight,
                entry, blend, phased ? 0f : binding.LayerBlend, resumeLoop);
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
