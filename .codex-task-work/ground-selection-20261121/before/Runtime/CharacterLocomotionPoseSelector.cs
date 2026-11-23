using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterReleaseDecision
    {
        None, ContinueStride, WaitForEvaluation, EnterOnContact, EnterOnBraking, FollowStop,
        FeetOverridden, Finished, MissingStop, Interrupted, EnterOnMatch
    }

    /// <summary>规则选姿。只读本帧观测、候选归属和上一次已经提交的播放计划。</summary>
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

        public CharacterReleaseDecision DecideRelease(CharacterAnimationIntentConverter intent,
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
                if (actual.normalizedTime >= 0.98f) return CharacterReleaseDecision.Finished;
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
            bool idle = owners.FullBody.Key == CharacterAnimationIntentConverter.IdleKey;
            float? speed = null;
            if (full.Binding.NominalSpeed > 0f || idle || full.Binding.IsStop)
            {
                if (motion.IsValid) speed = InputMath.NonNegative(motion.ActualSpeed, 0f);
                else if (full.StateHash == last.FullBody.StateHash) speed = last.Rates.StrideSpeed;
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
                entryTime = keep ? last.StopEntryTime : full.FixedStartSeconds.GetValueOrDefault();
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
            return new CharacterAnimationPlaybackPlan(full, lower, upper, motion, rates, clipTime, observed, stopRate, stop, release, entryTime, entryTravelPhase);
        }

        private CharacterAnimationLayerPlan SelectLayer(CharacterAnimationDemand demand, in CharacterLayerObservation layer,
            in CharacterPlaybackObservation seen, in CharacterAnimationLayerPlan previous,
            CharacterMatchResult match = default)
        {
            if (match.Choice == CharacterMatchChoice.Continue && previous.HasState)
                return new CharacterAnimationLayerPlan(previous.Demand, previous.Binding, CharacterAnimationPlaybackCommand.Keep,
                    CharacterAnimationPlaybackReason.KeepInstance, previous.UsedResourceFallback, previous.Weight,
                    previous.StartNormalizedTime, previous.TransitionDuration, previous.LayerBlendSeconds, previous.FixedStartSeconds);
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

            float start = demand.StartNormalizedTime ?? binding.Start;
            float duration = previous.HasState ? Mathf.Min(binding.Transition, previous.Binding.ExitTransition) : binding.Transition;
            bool sameStart = binding.Variant == CharacterLocomotionVariant.Start &&
                previous.Binding.Variant == CharacterLocomotionVariant.Start && previous.Demand.InstanceId == demand.InstanceId;
            bool changing = previous.HasState && previous.StateHash != binding.StateHash;
            bool sync = false;
            if (changing && !demand.StartNormalizedTime.HasValue &&
                (binding.Variant == CharacterLocomotionVariant.None || previous.Binding.IsStop || sameStart) &&
                TryMapEntry(previous.Binding, binding, layer, out float mapped))
            {
                start = mapped;
                sync = true;
            }
            bool fixedGaitBlend = changing && sameStart && previous.Demand.Key == CharacterAnimationIntentConverter.WalkKey &&
                demand.Key == CharacterAnimationIntentConverter.MoveKey && runStartClip != null;
            float? fixedStart = null;
            if (fixedGaitBlend)
            {
                fixedStart = start * resources.ClipSeconds(runStartClip);
                duration = walkToRunBlend;
            }
            else if (stopping || demand.Key == CharacterAnimationIntentConverter.IdleKey)
                fixedStart = 0f;
            if (match.HasEntry && binding.StateHash == match.Binding.StateHash)
            {
                start = match.Time / match.Length;
                fixedStart = stopping ? match.Time : (float?)null;
                bool blend = duration > 0f && previous.HasState;
                if (blend && stopping) duration *= seen.SourceLength;
                return new CharacterAnimationLayerPlan(demand, binding, blend ? CharacterAnimationPlaybackCommand.CrossFade :
                    CharacterAnimationPlaybackCommand.Play, CharacterAnimationPlaybackReason.MatchPose, fallback,
                    demand.Weight, start, duration, binding.LayerBlend, fixedStart);
            }
            bool fade = duration > 0f && (fixedGaitBlend || layer.Current.fullPathHash != binding.StateHash);
            var command = CharacterAnimationResourceResolver.ChooseCommand(demand, binding, previous, fade, out var reason);
            if (sync) reason = CharacterAnimationPlaybackReason.SynchronizeGait;
            if (command == CharacterAnimationPlaybackCommand.CrossFade && fixedStart.HasValue && !fixedGaitBlend)
                duration *= seen.SourceLength;
            return new CharacterAnimationLayerPlan(demand, binding, command, reason, fallback, demand.Weight,
                start, duration, binding.LayerBlend, fixedStart);
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
