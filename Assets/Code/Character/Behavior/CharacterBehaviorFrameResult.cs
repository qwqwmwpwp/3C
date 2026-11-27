using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>兼容调用方原有的位移来源分类。附加行为的具体标识见 DisplacementProvider。</summary>
    public enum CharacterDisplacementSource
    {
        None,
        BasicMovement,
        Dash,
        AdditionalBehavior
    }

    /// <summary>单步的执行结果和步末状态。最后一步执行后结束的行为同时出现在执行与结束集合。</summary>
    public readonly struct CharacterBehaviorFrameResult
    {
        private readonly CharacterBehaviorStep step;

        public long FrameNumber { get; }
        internal double StepStartTime => step.StartSeconds;
        internal bool DurationCommitted => step.DurationCommitted;
        public Vector2 Displacement { get; }
        public CharacterDisplacementSource DisplacementSource { get; }
        public CharacterBehaviorId DisplacementProvider => step.DisplacementProvider;
        public CharacterBehaviorId ExecutedBehaviors => step.Executed;
        public CharacterBehaviorId ActiveBehaviors => step.Active;
        public CharacterBehaviorId StartedBehaviors => step.Started;
        public CharacterBehaviorId EndedBehaviors => step.Ended;
        public CharacterBehaviorId InterruptedBehaviors => step.Interrupted;
        public CharacterBehaviorId WithdrawnBehaviors => step.Withdrawn;
        public CharacterAttackState Attack => step.Attack;
        public bool AttackMoveStarted => step.AttackMoveStarted;
        public IReadOnlyList<CharacterBehaviorExecution> ExecutionChanges => step.ExecutionChanges ?? Array.Empty<CharacterBehaviorExecution>();
        public IReadOnlyList<CharacterBehaviorDecision> Decisions => step.Decisions ?? Array.Empty<CharacterBehaviorDecision>();

        // 查询本步活动或刚结束的执行，保留提交时身份、阶段和结束原因。
        public bool TryGetExecution(CharacterBehaviorId id, out CharacterBehaviorExecution execution)
        {
            if (((step.Active | step.Ended | step.Executed) & id) != 0 && step.Executions != null)
                for (int i = 0; i < step.Executions.Count; i++)
                {
                    var value = step.Executions[i];
                    if (value.Id != id) continue;
                    if (value.EndReason == default || EndedInThisStep(value))
                    {
                        execution = value;
                        return true;
                    }
                }
            execution = default;
            return false;
        }

        // 基础移动许可可在待机生命周期内撤回，用本步边沿排除更早的陈旧终态。
        private bool EndedInThisStep(CharacterBehaviorExecution value)
        {
            var changes = ExecutionChanges;
            for (int i = changes.Count - 1; i >= 0; i--)
                if (changes[i].Instance == value.Instance && changes[i].EndReason != default) return true;
            return false;
        }

        // 无独立请求时返回 NoRequest；持续意图沿用原有身份并标为 AlreadyActive。
        public CharacterBehaviorRequestStatus RequestStatus(CharacterBehaviorId id)
        {
            for (int i = Decisions.Count - 1; i >= 0; i--)
                if (Decisions[i].Id == id) return Decisions[i].Status;
            return (step.HeldRequests & id) != 0 ? CharacterBehaviorRequestStatus.AlreadyActive : CharacterBehaviorRequestStatus.NoRequest;
        }

        internal CharacterActionMotion ActionMotion => step.Motion;
        internal Vector2 ActionDisplacement => step.ActionDisplacement;
        internal bool ActionMotionStarted => step.ActionMotionStarted;
        internal float OrdinaryMovementSeconds => step.OrdinaryMovementSeconds;
        internal float GrantAgeAtStepStart(CharacterBehaviorId id) =>
            step.GrantTimes != null && step.GrantTimes.TryGetValue(id, out double start) ? (float)(step.StartSeconds - start) : 0f;
        /// <summary>本步所有执行行为给出的最紧普通移动目标速限制。无限值表示未限速。</summary>
        public float OrdinarySpeedLimit => step.OrdinarySpeedLimit;
        public bool DashApproved => DashStarted;
        /// <summary>本步普通移动请求获准。以输入意图为准，碰撞阻挡时仍可获准。</summary>
        public bool MoveApproved { get; }
        public bool DashStarted => (StartedBehaviors & CharacterBehaviorId.Dash) != CharacterBehaviorId.None;
        public bool DashEnded => (EndedBehaviors & CharacterBehaviorId.Dash) != CharacterBehaviorId.None;
        /// <summary>本步提交后冲刺是否仍在活动，最后一步输出冲刺位移时此值可为 false。</summary>
        public bool DashActive => (ActiveBehaviors & CharacterBehaviorId.Dash) != CharacterBehaviorId.None;
        public bool MoveStarted { get; }
        public bool MoveEnded { get; }
        /// <summary>普通移动沿用请求的事件边沿，单纯进入基础待机不向表现层发送事件。</summary>
        public bool HasTransitions => MoveStarted || MoveEnded || AttackMoveStarted
            || ((StartedBehaviors | EndedBehaviors) & ~CharacterBehaviorId.Move) != CharacterBehaviorId.None;
        public Vector2 DashDirection { get; }
        public float StaminaRemaining { get; }
        public float DashCooldownRemaining { get; }

        // 将获准的时间片投影为该物理区间实际请求的运动，保留对应行为帧和实例。
        internal CharacterBehaviorFrameResult WithMotion(Vector2 total, Vector2 action, CharacterActionMotion motion,
            CharacterBehaviorId provider, float ordinarySeconds, bool moveApproved, bool snap)
        {
            var result = step;
            result.Motion = motion;
            result.ActionDisplacement = action;
            result.ActionMotionStarted = snap;
            result.DisplacementProvider = provider;
            result.OrdinaryMovementSeconds = ordinarySeconds;
            result.OrdinaryMovementApproved = moveApproved;
            return new CharacterBehaviorFrameResult(FrameNumber, total, result);
        }

        // 用各参与者填好的固定步记录生成运行时帧结果。
        internal CharacterBehaviorFrameResult(long frameNumber, Vector2 displacement, CharacterBehaviorStep step)
            : this(frameNumber, displacement, step.OrdinaryMovementApproved, step.PreviousOrdinaryMovementApproved, step.DashDirection,
                step.StaminaRemaining, step.DashCooldownRemaining, step)
        {
        }

        internal CharacterBehaviorFrameResult(long frameNumber, Vector2 displacement, bool moveApproved,
            bool previousMoveApproved, Vector2 dashDirection, float staminaRemaining, float dashCooldownRemaining,
            CharacterBehaviorStep step)
        {
            this.step = step;
            FrameNumber = frameNumber;
            Displacement = displacement;
            switch (step.DisplacementProvider)
            {
                case CharacterBehaviorId.None: DisplacementSource = CharacterDisplacementSource.None; break;
                case CharacterBehaviorId.Move: DisplacementSource = CharacterDisplacementSource.BasicMovement; break;
                case CharacterBehaviorId.Dash: DisplacementSource = CharacterDisplacementSource.Dash; break;
                default: DisplacementSource = CharacterDisplacementSource.AdditionalBehavior; break;
            }
            MoveApproved = moveApproved;
            MoveStarted = moveApproved && !previousMoveApproved;
            MoveEnded = !moveApproved && previousMoveApproved;
            DashDirection = dashDirection;
            StaminaRemaining = staminaRemaining;
            DashCooldownRemaining = dashCooldownRemaining;
        }
    }

    /// <summary>位移动作推进一次后的运动结果。普通移动使用本步交还的等效秒数。</summary>
    public readonly struct CharacterBehaviorMotion
    {
        public readonly Vector2 Displacement; // 世界 X/Z 位移。
        public readonly float OrdinaryMovementSeconds;
        public readonly bool ControlsDisplacement; // 与普通移动共存时，是否仍以本动作为位移来源。
        public readonly CharacterActionMotion Motion;

        public CharacterBehaviorMotion(Vector2 displacement, float ordinaryMovementSeconds = 0f,
            bool controlsDisplacement = true, CharacterActionMotion motion = default)
        {
            Displacement = displacement;
            OrdinaryMovementSeconds = InputMath.NonNegative(ordinaryMovementSeconds, 0f);
            ControlsDisplacement = controlsDisplacement;
            Motion = motion;
        }
    }

    /// <summary>位移提供者本步的固定时钟、转向和制动指令，非位移行为使用默认值。</summary>
    public struct CharacterActionMotion
    {
        /// <summary>立即施加本步动作速度，动作结束时只清理动作贡献的余速。</summary>
        public bool UsesBurstVelocity;
        /// <summary>动作开始时立即转向 Facing，优先于普通转向。</summary>
        public bool SnapsFacingOnStart;
        public float? StartPhase;
        public float EndPhase, Duration;
        public float MaxTurn, Braking;
        public Vector2 Facing;
        public bool ControlsFacing, RecoversMovement;
    }

    /// <summary>收集单步生命周期边沿、执行结果和表现层许可时刻。</summary>
    internal struct CharacterBehaviorStep
    {
        public CharacterBehaviorId DisplacementProvider;
        public CharacterBehaviorId Executed;
        public CharacterBehaviorId Active;
        public CharacterBehaviorId Started;
        public CharacterBehaviorId Ended;
        public CharacterBehaviorId Interrupted;
        public CharacterBehaviorId Withdrawn;
        public CharacterActionMotion Motion;
        public Vector2 ActionDisplacement;
        public bool ActionMotionStarted;
        public float OrdinaryMovementSeconds;
        public CharacterAttackState Attack;
        public bool AttackMoveStarted;
        public bool OrdinaryMovementApproved, PreviousOrdinaryMovementApproved;
        public Vector2 DashDirection;
        public float StaminaRemaining, DashCooldownRemaining;
        public float OrdinarySpeedLimit;
        public double StartSeconds;
        public Dictionary<CharacterBehaviorId, double> GrantTimes;
        public bool SkipGrantHistory;
        internal bool DurationCommitted;
        internal IReadOnlyList<CharacterBehaviorDecision> Decisions;
        internal IReadOnlyList<CharacterBehaviorExecution> Executions, ExecutionChanges;
        internal CharacterBehaviorId HeldRequests;
        private bool copiedGrants;

        // 判断该行为的生命周期在当前阶段是否处于活动状态。
        internal bool IsActive(CharacterBehaviorId id) => (Active & id) != CharacterBehaviorId.None;

        // 激活已获准参与者并记录生命周期边沿。
        internal void Start(CharacterBehaviorRule rule, CharacterBehaviorStart request, bool recordGrant = true)
        {
            rule.Behavior.Activate(request);
            Active |= rule.Id;
            Started |= rule.Id;
            if (recordGrant) RecordGrant(rule.Id);
        }

        // 释放参与者并同步许可，即使参与者的结束回调报错也撤销许可。
        internal void End(CharacterBehaviorRule rule, CharacterBehaviorEndReason reason = CharacterBehaviorEndReason.Completed)
        {
            // 阶段读取失败时尚未进入结束回调，保留原有活动位。
            rule.Behavior.Journal?.End(rule.Behavior, reason);
            try
            {
                rule.Behavior.Deactivate();
            }
            finally
            {
                Active &= ~rule.Id;
                Ended |= rule.Id;
            }
        }

        // 记录新获得执行许可的行为及其固定步起点。
        public void RecordGrant(CharacterBehaviorId id)
        {
            if (SkipGrantHistory) return; // 预测只消费移动许可，不生成给表现层的历史字典。
            if (!copiedGrants) // 已发布固定步保留自己的时间快照，仅在新许可时复制。
            {
                GrantTimes = GrantTimes == null ? new Dictionary<CharacterBehaviorId, double>() :
                    new Dictionary<CharacterBehaviorId, double>(GrantTimes);
                copiedGrants = true;
            }
            GrantTimes[id] = StartSeconds;
        }
    }

}
