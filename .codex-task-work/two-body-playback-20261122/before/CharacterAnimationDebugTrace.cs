#if UNITY_EDITOR
using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterAnimationDebugStatus
    {
        WaitingForUpdate,
        Running,
        Inactive,
        Paused,
        AnimatorUnavailable,
        ResourceUnavailable
    }

    internal readonly struct CharacterDebugClipMix
    {
        public readonly string Target, Outgoing;
        public readonly float TargetWeight, OutgoingWeight;
        public readonly int OtherCount;

        public CharacterDebugClipMix(string target, float targetWeight, string outgoing, float outgoingWeight, int otherCount)
        {
            Target = target;
            TargetWeight = targetWeight;
            Outgoing = outgoing;
            OutgoingWeight = outgoingWeight;
            OtherCount = otherCount;
        }
    }

    internal readonly struct CharacterDebugHandoff
    {
        public readonly int RenderFrame;
        public readonly bool MoveApproved;
        public readonly CharacterAttackState Attack;
        public readonly CharacterDebugClipMix Base, Action;
        public readonly float Coverage;
        public readonly bool Direct;

        public CharacterDebugHandoff(CharacterBehaviorFrameResult step, CharacterAnimationPlayer player, int overlayLayer)
        {
            RenderFrame = UnityEngine.Time.frameCount;
            MoveApproved = step.MoveApproved;
            Attack = step.Attack;
            var clips = player.Direct;
            Direct = clips != null;
            Base = clips?.DebugBaseMix ?? default;
            Action = clips?.DebugOverlayMix ?? default;
            Coverage = player.Active.LayerWeight(overlayLayer);
        }

        public bool SameStage(in CharacterDebugHandoff other) =>
            MoveApproved == other.MoveApproved && Attack.InstanceId == other.Attack.InstanceId &&
            Attack.Move == other.Attack.Move && Attack.Phase == other.Attack.Phase &&
            (Coverage >= 0.9999f) == (other.Coverage >= 0.9999f) &&
            (Action.TargetWeight >= 0.9999f) == (other.Action.TargetWeight >= 0.9999f);
    }

    /// <summary>单层获选语义、已解析素材与本次实际下发的 Animator 起点。</summary>
    internal readonly struct CharacterAnimationDebugLayer
    {
        public string Key { get; }
        public long InstanceId { get; }
        public string StateName { get; }
        public float Weight { get; }
        public CharacterAnimationPlaybackReason Reason { get; }
        public CharacterAnimationPlaybackCommand Command { get; }
        public float? SentStart { get; }
        public float? SamplePhase { get; }
        public CharacterBehaviorPhase CoveragePhase { get; }
        public bool UsedResourceFallback { get; }
        public bool IsBasePose { get; }

        public CharacterAnimationDebugLayer(in CharacterAnimationDemand demand, in CharacterAnimationLayerPlan plan,
            bool emitted)
        {
            Key = demand.Key;
            IsBasePose = demand.IsLocomotion || demand.Key == CharacterAnimationIntentConverter.IdleKey;
            InstanceId = demand.InstanceId;
            StateName = plan.StateName;
            Weight = plan.Weight;
            var timing = plan.Demand.Timing;
            SamplePhase = timing.Handoff.Valid ? timing.SamplePhase : null;
            CoveragePhase = SamplePhase.HasValue ? timing.Handoff.PhaseAt(SamplePhase.Value) : default;
            Reason = plan.Reason;
            Command = emitted ? plan.Command : CharacterAnimationPlaybackCommand.Keep;
            SentStart = emitted ? plan.Start.Phase : (float?)null;
            UsedResourceFallback = plan.UsedResourceFallback;
        }

        public bool MatchesSelection(in CharacterAnimationDebugLayer other) =>
            Key == other.Key && InstanceId == other.InstanceId && StateName == other.StateName &&
            CoveragePhase == other.CoveragePhase && UsedResourceFallback == other.UsedResourceFallback;
    }

    /// <summary>同一次动画更新发布的只读快照；无有效运动观测时不显示固定步、速度与阶段。</summary>
    internal readonly struct CharacterAnimationDebugSnapshot
    {
        public CharacterAnimationDebugStatus Status { get; }
        public string Error { get; }
        public CharacterDebugHandoff Handoff { get; }
        public CharacterAnimationMotion Motion { get; }
        public CharacterLocomotionPhase Phase { get; }
        public CharacterLocomotionPhaseReason PhaseReason { get; }
        public CharacterGroundTrajectory Braking { get; }
        public float? StopClipTime { get; }
        public float StopBrakeMarker { get; }
        public CharacterStopPoseSample StopPose { get; }
        public CharacterReleaseDecision ReleaseDecision { get; }
        public bool? ObservedLeftSupport { get; }
        public float ReleaseSpeed { get; }
        public CharacterAnimationDebugLayer FullBody { get; }
        public CharacterAnimationDebugLayer Overlay { get; }
        public CharacterAnimationDebugLayer LowerBody { get; }
        public CharacterAnimationDebugLayer UpperBody { get; }
        public CharacterBodyConflict Conflict { get; }

        public CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus status, string error = null)
        {
            this = default;
            Status = status;
            Error = error;
        }

        public CharacterAnimationDebugSnapshot(CharacterAnimationMotion motion, CharacterLocomotionPhase phase,
            CharacterLocomotionPhaseReason phaseReason, CharacterAnimationDebugLayer fullBody,
            CharacterAnimationDebugLayer lowerBody, CharacterAnimationDebugLayer upperBody, CharacterAnimationDebugLayer overlay,
            CharacterGroundTrajectory braking, in CharacterAnimationPlaybackPlan plan,
            bool? observedLeft, float releaseSpeed, in CharacterDebugHandoff handoff, CharacterBodyConflict conflict)
            : this(CharacterAnimationDebugStatus.Running)
        {
            Handoff = handoff;
            Motion = motion;
            Phase = phase;
            PhaseReason = phaseReason;
            Braking = braking;
            StopClipTime = plan.StopClipTime;
            StopBrakeMarker = plan.FullBody.Binding.BrakingClipSeconds;
            StopPose = plan.StopPose;
            ReleaseDecision = plan.ReleaseDecision;
            ObservedLeftSupport = observedLeft;
            ReleaseSpeed = releaseSpeed;
            FullBody = fullBody;
            LowerBody = lowerBody;
            UpperBody = upperBody;
            Overlay = overlay;
            Conflict = conflict;
        }

        public bool StartsPlayback => FullBody.SentStart.HasValue || LowerBody.SentStart.HasValue ||
            UpperBody.SentStart.HasValue || Overlay.SentStart.HasValue;

        public bool MatchesSelection(in CharacterAnimationDebugSnapshot other) =>
            (!Motion.IsValid || (other.Motion.IsValid && Phase == other.Phase)) &&
            Conflict.Rejected == other.Conflict.Rejected && Conflict.Holder == other.Conflict.Holder && Conflict.Reason == other.Conflict.Reason &&
            Handoff.SameStage(other.Handoff) && ReleaseDecision == other.ReleaseDecision &&
            Braking.IsBraking == other.Braking.IsBraking &&
            Braking.IsBrakeLeadIn == other.Braking.IsBrakeLeadIn &&
            (Braking.RemainingStopSeconds > 0f) == (other.Braking.RemainingStopSeconds > 0f) &&
            (StopClipTime >= StopBrakeMarker) == (other.StopClipTime >= other.StopBrakeMarker) &&
            FullBody.MatchesSelection(other.FullBody) && LowerBody.MatchesSelection(other.LowerBody) &&
            UpperBody.MatchesSelection(other.UpperBody) && Overlay.MatchesSelection(other.Overlay);
    }

    internal interface ICharacterAnimationDebugHistory
    {
        long FirstTransition { get; }
        long LatestTransition { get; }
        bool TryReadTransition(long sequence, out CharacterAnimationDebugSnapshot snapshot);
    }

    /// <summary>只在编辑器中保留当前快照和最近的阶段、获选或实际播放变化；正常帧不追加历史。</summary>
    internal sealed class CharacterAnimationDebugTrace : ICharacterAnimationDebugHistory
    {
        private const int Capacity = 24;
        private readonly CharacterAnimationDebugSnapshot[] entries = new CharacterAnimationDebugSnapshot[Capacity];
        private long lastSequence;
        private int count;
        private CharacterGroundTrajectory lastBraking;
        private int lastVersion;

        public void ObserveGroundStep(CharacterControllerFrameResult frame)
        {
            if (frame.SimulationVersion != lastVersion || frame.Behavior.DisplacementProvider != CharacterBehaviorId.None)
                lastBraking = default;
            if (frame.Trajectory.IsBraking)
                lastBraking = frame.Trajectory;
            lastVersion = frame.SimulationVersion;
        }

        public CharacterAnimationDebugSnapshot Current { get; private set; }
        public long FirstTransition => count > 0 ? lastSequence - count + 1 : 0;
        public long LatestTransition => count > 0 ? lastSequence : 0;

        public void Record(CharacterAnimationMotion motion, CharacterLocomotionPhase phase,
            CharacterLocomotionPhaseReason phaseReason, in CharacterAnimationComposition selection,
            in CharacterAnimationPlaybackPlan plan, in CharacterAnimationPlaybackEmission emission,
            bool? observedLeft, UnityEngine.Vector3 entryVelocity, in CharacterDebugHandoff handoff)
        {
            float speed = plan.ReleaseDecision == CharacterReleaseDecision.Interrupted ? Current.ReleaseSpeed : entryVelocity.magnitude;
            var snapshot = new CharacterAnimationDebugSnapshot(motion, phase, phaseReason,
                new CharacterAnimationDebugLayer(selection.FullBody, plan.FullBody, emission.FullBody),
                new CharacterAnimationDebugLayer(selection.LowerBody, plan.LowerBody, emission.LowerBody),
                new CharacterAnimationDebugLayer(selection.UpperBody, plan.UpperBody, emission.UpperBody),
                new CharacterAnimationDebugLayer(selection.Overlay, plan.Overlay, emission.Overlay),
                lastBraking, plan, observedLeft, speed, handoff, selection.Conflict);
            Current = snapshot;
            if (!snapshot.StartsPlayback && count > 0 && snapshot.MatchesSelection(entries[Slot(lastSequence)]))
                return;

            entries[Slot(++lastSequence)] = snapshot;
            if (count < Capacity)
                count++;
        }

        public void SetStatus(CharacterAnimationDebugStatus status, string error = null)
        {
            Current = new CharacterAnimationDebugSnapshot(status, error);
        }

        public void Clear()
        {
            Current = default;
            lastBraking = default;
            Array.Clear(entries, 0, entries.Length);
            count = 0;
        }

        public bool TryReadTransition(long sequence, out CharacterAnimationDebugSnapshot snapshot)
        {
            if (count == 0 || sequence < FirstTransition || sequence > LatestTransition)
            {
                snapshot = default;
                return false;
            }
            snapshot = entries[Slot(sequence)];
            return true;
        }

        private static int Slot(long sequence) => (int)((sequence - 1) % Capacity);
    }
}
#endif
