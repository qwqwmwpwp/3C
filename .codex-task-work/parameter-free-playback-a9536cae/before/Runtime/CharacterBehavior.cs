using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>固定步长角色行为的统一生命周期，由行为流程负责调度。</summary>
    public abstract class CharacterBehavior
    {
        /// <summary>行为已进入活动生命周期，由流程统一启动、执行和结束。</summary>
        public bool IsActive { get; protected set; }
        internal bool IsBound { get; set; }
        /// <summary>供有向打断规则读取的当前阶段。</summary>
        public virtual CharacterBehaviorPhase Phase => CharacterBehaviorPhase.Active;

        /// <summary>只读评估行为自身的条件，本步执行归属由流程裁决。</summary>
        public abstract bool Evaluate(CharacterBehaviorContext context);

        /// <summary>输入仍希望开启行为。默认只在未活动时评估请求。</summary>
        public virtual bool RequestsStart(CharacterBehaviorContext context) => !IsActive && Evaluate(context);

        /// <summary>请求已存在时的资源或行为条件检查，裁决流程批准前不得修改状态。</summary>
        public virtual CharacterBehaviorRejection GetStartRejection(CharacterBehaviorContext context) =>
            CharacterBehaviorRejection.None;

        /// <summary>成功开始时消耗的共享体力。此查询不更改行为或体力。</summary>
        public virtual float GetStaminaCost(CharacterBehaviorContext context) => 0f;

        /// <summary>本步开始时是否保留该行为，默认保留至执行完成或被打断。</summary>
        public virtual bool ShouldContinue(CharacterBehaviorContext context) => true;

        /// <summary>本步执行后是否结束行为。</summary>
        public virtual bool IsComplete => false;

        /// <summary>获准后初始化一次，首次执行更新的时机由行为流程决定。</summary>
        public virtual void Start(CharacterBehaviorContext context) => IsActive = true;

        /// <summary>行为完成或失去执行资格时释放活动状态。</summary>
        public virtual void End() => IsActive = false;

        /// <summary>推进活动行为一个固定步长。只有位移提供者的世界 X/Z 输出会用于移动。</summary>
        public virtual Vector2 Update(CharacterBehaviorContext context) => Vector2.zero;

        internal void Activate(CharacterBehaviorContext context)
        {
            Start(context);
            IsActive = true;
        }

        internal void Deactivate()
        {
            End();
            IsActive = false;
        }

        internal virtual void Reset()
        {
            if (IsActive)
                Deactivate();
        }
    }

    /// <summary>同一固定步内供行为判定和执行共享的输入，时间步长与冷却时间以秒计。</summary>
    public readonly struct CharacterBehaviorContext
    {
        public CharacterInputSnapshot Input { get; }
        public Vector2 Facing { get; }
        public float DeltaTime { get; }
        public CharacterMovementSettings Settings { get; }
        public float StaminaRemaining { get; }
        public float DashCooldownRemaining { get; }

        public CharacterBehaviorContext(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, float staminaRemaining, float dashCooldownRemaining)
        {
            Input = input;
            Facing = facing;
            DeltaTime = InputMath.NonNegative(deltaTime, 0f);
            Settings = settings ?? new CharacterMovementSettings();
            StaminaRemaining = staminaRemaining;
            DashCooldownRemaining = dashCooldownRemaining;
        }
    }
}
