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

    public enum CharacterBehaviorRejection
    {
        None,
        AlreadyActive,
        GateDenied,
        Conflict,
        InterruptPhaseDenied,
        Cooldown,
        InsufficientStamina,
        InvalidSettings
    }

    /// <summary>单步的执行结果和步末状态。最后一步执行后结束的行为同时出现在执行与结束集合。</summary>
    public readonly struct CharacterBehaviorFrameResult
    {
        private readonly CharacterBehaviorStep step;

        public long FrameNumber { get; }
        public Vector2 Displacement { get; }
        public CharacterDisplacementSource DisplacementSource { get; }
        public CharacterBehaviorId DisplacementProvider => step.DisplacementProvider;
        public CharacterBehaviorId ExecutedBehaviors => step.Executed;
        public CharacterBehaviorId ActiveBehaviors => step.Active;
        public CharacterBehaviorId StartedBehaviors => step.Started;
        public CharacterBehaviorId EndedBehaviors => step.Ended;
        public CharacterBehaviorId InterruptedBehaviors => step.Interrupted;
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
        public bool HasTransitions => MoveStarted || MoveEnded
            || ((StartedBehaviors | EndedBehaviors) & ~CharacterBehaviorId.Move) != CharacterBehaviorId.None;
        public Vector2 DashDirection { get; }
        public float StaminaRemaining { get; }
        public float DashCooldownRemaining { get; }

        public CharacterBehaviorRejection GetRejection(CharacterBehaviorId id) => step.Rejections.Get(id);

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

    internal struct CharacterBehaviorStep
    {
        public CharacterBehaviorId DisplacementProvider;
        public CharacterBehaviorId Executed;
        public CharacterBehaviorId Active;
        public CharacterBehaviorId Started;
        public CharacterBehaviorId Ended;
        public CharacterBehaviorId Interrupted;
        public float OrdinarySpeedLimit;
        public CharacterBehaviorRejections Rejections;
    }

    /// <summary>按原因记录行为位集，使先前提交的结果能跨后续固定步保留。</summary>
    internal struct CharacterBehaviorRejections
    {
        private CharacterBehaviorId alreadyActive;
        private CharacterBehaviorId gateDenied;
        private CharacterBehaviorId conflict;
        private CharacterBehaviorId interruptPhaseDenied;
        private CharacterBehaviorId cooldown;
        private CharacterBehaviorId insufficientStamina;
        private CharacterBehaviorId invalidSettings;

        public void Set(CharacterBehaviorId id, CharacterBehaviorRejection reason)
        {
            switch (reason)
            {
                case CharacterBehaviorRejection.AlreadyActive: alreadyActive |= id; break;
                case CharacterBehaviorRejection.GateDenied: gateDenied |= id; break;
                case CharacterBehaviorRejection.Conflict: conflict |= id; break;
                case CharacterBehaviorRejection.InterruptPhaseDenied: interruptPhaseDenied |= id; break;
                case CharacterBehaviorRejection.Cooldown: cooldown |= id; break;
                case CharacterBehaviorRejection.InsufficientStamina: insufficientStamina |= id; break;
                case CharacterBehaviorRejection.InvalidSettings: invalidSettings |= id; break;
            }
        }

        public CharacterBehaviorRejection Get(CharacterBehaviorId id)
        {
            if ((alreadyActive & id) != CharacterBehaviorId.None) return CharacterBehaviorRejection.AlreadyActive;
            if ((gateDenied & id) != CharacterBehaviorId.None) return CharacterBehaviorRejection.GateDenied;
            if ((conflict & id) != CharacterBehaviorId.None) return CharacterBehaviorRejection.Conflict;
            if ((interruptPhaseDenied & id) != CharacterBehaviorId.None) return CharacterBehaviorRejection.InterruptPhaseDenied;
            if ((cooldown & id) != CharacterBehaviorId.None) return CharacterBehaviorRejection.Cooldown;
            if ((insufficientStamina & id) != CharacterBehaviorId.None) return CharacterBehaviorRejection.InsufficientStamina;
            if ((invalidSettings & id) != CharacterBehaviorId.None) return CharacterBehaviorRejection.InvalidSettings;
            return CharacterBehaviorRejection.None;
        }
    }
}
