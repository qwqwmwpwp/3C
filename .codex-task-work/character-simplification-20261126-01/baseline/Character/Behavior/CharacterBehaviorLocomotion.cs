using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>合成本步动作位移和交还给普通移动的时间。</summary>
    internal struct CharacterLocomotionStep
    {
        internal float OrdinaryMovementSeconds;
        internal CharacterBehaviorMotion Action;
        internal CharacterBehaviorId MotionBlocker;
    }

    /// <summary>维护基础移动许可，并将动作交还的时间和限速合成为普通位移。</summary>
    internal sealed class CharacterBehaviorLocomotion
    {
        private readonly CharacterBehaviorRule fallback;
        private readonly CharacterMoveBehavior movement;
        private bool previousApproval, requested;

        // 绑定基础移动及其准入规则。
        internal CharacterBehaviorLocomotion(CharacterBehaviorRule fallback, CharacterMoveBehavior movement)
        {
            this.fallback = fallback;
            this.movement = movement;
        }

        // 复制上一步的移动许可，以便独立回放。
        internal void CopyStateTo(CharacterBehaviorLocomotion target) => target.previousApproval = previousApproval;

        // 清除跨步保留的移动许可。
        internal void Reset() => previousApproval = false;

        // 无推进时保留已有状态，只初始化本帧限速。
        internal void ReadOutcome(ref CharacterBehaviorStep step) => step.OrdinarySpeedLimit = float.PositiveInfinity;

        // 初始化固定步的位移组合时间及移动许可边沿。
        internal CharacterLocomotionStep BeginStep(float seconds, ref CharacterBehaviorStep step)
        {
            ReadOutcome(ref step);
            step.PreviousOrdinaryMovementApproved = previousApproval;
            return new CharacterLocomotionStep { OrdinaryMovementSeconds = seconds };
        }

        // 在本步裁决开始时只求值一次基础移动请求。
        internal void ReadRequest(CharacterBehaviorContext context) => requested = movement.Evaluate(context);

        // 检查上步获得的基础移动许可在当前输入下是否仍有效。
        internal bool HasExistingPermit(CharacterBehaviorContext context, in CharacterBehaviorStep step) =>
            step.IsActive(fallback.Id) && previousApproval && requested;

        // 合并活动动作给出的时长、限制和位移归属。
        internal void Combine(CharacterBehaviorRule rule, CharacterBehaviorMotion motion,
            ref CharacterLocomotionStep locomotion, ref CharacterBehaviorStep step)
        {
            step.OrdinarySpeedLimit = Math.Min(step.OrdinarySpeedLimit, rule.OrdinarySpeedLimit);
            if (rule.HasExplicitConflictWith(fallback))
            {
                locomotion.OrdinaryMovementSeconds = 0f;
                if (locomotion.MotionBlocker == default) locomotion.MotionBlocker = rule.Id;
            }
            if (!rule.ProvidesDisplacement)
                return;

            if (motion.OrdinaryMovementSeconds == 0f && !rule.Behavior.AllowsOrdinaryMotionNow && locomotion.MotionBlocker == default)
                locomotion.MotionBlocker = rule.Id;
            locomotion.Action = motion; // 准入规则保证至多一个活动动作提供位移。
            locomotion.OrdinaryMovementSeconds = Math.Min(locomotion.OrdinaryMovementSeconds, motion.OrdinaryMovementSeconds);
            step.Motion = motion.Motion;
            step.ActionDisplacement = motion.Displacement;
            step.ActionMotionStarted = (step.Started & rule.Id) != default;
            if (motion.ControlsDisplacement || motion.Displacement != Vector2.zero)
                step.DisplacementProvider = rule.Id;
        }

        // 处理本步剩余时间的基础移动许可，再提交受限的合成位移。
        internal Vector2 Resolve(CharacterBehaviorContext context, CharacterBehaviorRules rules,
            in CharacterLocomotionStep locomotion, ref CharacterBehaviorStep step)
        {
            float seconds = locomotion.OrdinaryMovementSeconds;
            bool permittedTime = seconds > 0f;
            if (context.SeparateBoundaries && context.DeltaTime == 0f)
            {
                permittedTime = true;
                foreach (var rule in rules.Actions)
                {
                    if (!step.IsActive(rule.Id)) continue;
                    if (rule.HasExplicitConflictWith(fallback) || rule.ProvidesDisplacement &&
                        !rule.Behavior.AllowsOrdinaryMotionNow)
                        permittedTime = false;
                }
            }
            if (movement.Journal.DispatchCancelled) return locomotion.Action.Displacement;
            step.OrdinaryMovementApproved = ResolvePermit(context, rules, permittedTime, locomotion.MotionBlocker, ref step);
            if (step.OrdinaryMovementApproved && !previousApproval)
                step.RecordGrant(fallback.Id);
            previousApproval = step.OrdinaryMovementApproved;
            if (!step.OrdinaryMovementApproved) return locomotion.Action.Displacement;

            step.OrdinaryMovementSeconds = seconds;
            if (!locomotion.Action.ControlsDisplacement)
                step.DisplacementProvider = fallback.Id;
            return locomotion.Action.Displacement + movement.Update(context, seconds, step.OrdinarySpeedLimit);
        }

        // 在动作留出时间时保持基础移动生命周期，新许可需要经过门控。
        private bool ResolvePermit(CharacterBehaviorContext context, CharacterBehaviorRules rules,
            bool permittedTime, CharacterBehaviorId motionBlocker, ref CharacterBehaviorStep step)
        {
            var journal = movement.Journal;
            if (!permittedTime)
            {
                if (step.IsActive(fallback.Id))
                    step.End(fallback, CharacterBehaviorEndReason.Interrupted);
                if (context.Input.Direction != default)
                    journal.Record(fallback.Id, journal.NewRequest(), 0, CharacterBehaviorRequestStatus.Rejected,
                        CharacterBehaviorRejectReason.Conflict, motionBlocker);
                return false;
            }

            if (!step.IsActive(fallback.Id))
                step.Start(fallback, new CharacterBehaviorStart(context, 0f), recordGrant: false);
            if (context.Input.Direction == default)
            {
                if (previousApproval) journal.End(movement, CharacterBehaviorEndReason.Withdrawn);
                return false;
            }
            var blocker = requested && !previousApproval ? rules.GateBlocker(fallback.Id, step.Active) : default;
            if (!requested || blocker != default)
            {
                if (previousApproval) journal.End(movement, CharacterBehaviorEndReason.Withdrawn);
                journal.Record(fallback.Id, journal.NewRequest(), 0, CharacterBehaviorRequestStatus.Rejected,
                    !requested ? CharacterBehaviorRejectReason.InvalidConfiguration : CharacterBehaviorRejectReason.Gate, blocker);
                return false;
            }
            if (previousApproval && movement.Execution.EndReason == default && movement.Execution.Instance != 0) step.HeldRequests |= fallback.Id;
            else
            {
                long request = journal.NewRequest();
                journal.Start(movement, request);
                journal.Record(fallback.Id, request, movement.Execution.Instance, CharacterBehaviorRequestStatus.Started);
            }
            return true;
        }
    }
}
