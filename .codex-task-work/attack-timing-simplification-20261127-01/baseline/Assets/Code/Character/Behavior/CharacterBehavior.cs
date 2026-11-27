using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>固定步长角色行为的统一生命周期，由行为流程负责调度。</summary>
    public abstract class CharacterBehavior
    {
        /// <summary>行为已进入活动生命周期，由流程统一启动、执行和结束。</summary>
        public bool IsActive { get; protected set; }
        internal CharacterBehaviorJournal Journal;
        internal CharacterBehaviorId RegisteredId;
        internal CharacterBehaviorExecution Execution;
        internal bool HeldRequest;
        /// <summary>生命周期回调也可区分独立预测，避免在预演或工作线程发出真实副作用。</summary>
        protected bool IsPrediction => Journal?.Forecasting == true;
        /// <summary>启动获准后可在运动冻结或后续执行时读取当前通用执行号。</summary>
        protected long ExecutionInstance => Execution.Instance;
        internal bool IsBound { get; set; }
        /// <summary>位移动作起手时允许保留普通移动许可。显式互斥仍然有效。</summary>
        public virtual bool SharesMovementOnStart => false;
        /// <summary>供有向打断规则读取的当前阶段。</summary>
        public virtual CharacterBehaviorPhase Phase => CharacterBehaviorPhase.Active;
        /// <summary>返回下一阶段、接续或结束边界的余时（秒），正无穷表示没有边界。</summary>
        public virtual double NextBoundaryDelay => double.PositiveInfinity;
        /// <summary>允许下一边界吸附到紧邻的离散输入时刻的单向误差（秒）。</summary>
        public virtual float InputEndpointTolerance => 0f;
        /// <summary>完成正时长时间段后，保留行为至端点的零时长裁决。</summary>
        public virtual bool DefersCompletionAtEndpoint => false;
        /// <summary>零时长裁决时允许普通移动取得本位移动作交还的许可。</summary>
        public virtual bool AllowsOrdinaryMotionNow => false;

        /// <summary>在正时长步骤的完成处理后推进跨活动期的计时。</summary>
        public virtual void AfterStep(float seconds) { }

        /// <summary>只读判断行为自身是否允许开始，互斥和打断由规则裁决。</summary>
        public abstract bool Evaluate(CharacterBehaviorContext context);

        /// <summary>一次性检查请求条件。旧 Evaluate 仍可由状态主动触发，显式请求未通过时使用兼容原因。</summary>
        protected virtual CharacterBehaviorRejectReason CheckRequest(CharacterBehaviorContext context) =>
            Evaluate(context) ? CharacterBehaviorRejectReason.None : CharacterBehaviorRejectReason.LegacyEvaluation;

        // 用同一次检查及报价准备启动；失败原因由实际拒绝的检查直接给出。
        internal virtual CharacterBehaviorStart PrepareStart(CharacterBehaviorContext context, out CharacterBehaviorRejectReason reason)
        {
            reason = CheckRequest(context);
            if (reason != default || Journal?.DispatchCancelled == true) return null;
            return new CharacterBehaviorStart(context, GetStaminaCost(context));
        }

        // 将通用请求交给已有行为的公开启动入口。
        internal virtual void StartPrepared(CharacterBehaviorStart request) => Start(request.Context);

        // 检查已准备请求的报价，资源只在获准启动后消费。
        internal CharacterBehaviorStart Quote(CharacterBehaviorContext context, out CharacterBehaviorRejectReason reason)
        {
            var prepared = PrepareStart(context, out reason);
            if (prepared == null) return null;
            if (CharacterStaminaBlackboard.CanAfford(context.StaminaRemaining, prepared.Cost)) return prepared;
            reason = prepared.Cost >= 0f && !float.IsInfinity(prepared.Cost)
                ? CharacterBehaviorRejectReason.InsufficientResource : CharacterBehaviorRejectReason.InvalidConfiguration;
            return null;
        }

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

        /// <summary>每个获准动作每步调用一次。旧位移行为默认独占本步，其他行为的运动输出由规则忽略。</summary>
        public virtual CharacterBehaviorMotion AdvanceStep(CharacterBehaviorContext context) =>
            new CharacterBehaviorMotion(Update(context));

        // 将行为在本步实际执行后的结果写入行为帧。
        internal virtual void RecordOutcome(bool ordinaryMovementApproved, ref CharacterBehaviorStep step) { }

        // 将未推进固定步时可观测的当前状态写入行为帧。
        internal virtual void ReadOutcome(ref CharacterBehaviorStep step) { }

        /// <summary>输入连续性结束时释放本行为保留的输入，活动状态由正常调度维护。</summary>
        protected virtual void OnInputContinuityEnded() { }

        // 把输入连续性结束事件交给具体行为处理。
        internal void EndInputContinuity() => OnInputContinuityEnded();

        /// <summary>返回需在复制前检查引用隔离的独占可变容器。</summary>
        protected virtual object ExclusiveRuntimeStorage => null;

        /// <summary>将运行状态复制到已校验且独立的同类型目标。</summary>
        protected virtual void CopyRuntimeStateTo(CharacterBehavior target) { }

        /// <summary>用目标持有的定义刷新需要重新绑定的预测运行参数。</summary>
        protected virtual void BindPredictionDefinition() { }

        internal object RuntimeStorageForCopy => ExclusiveRuntimeStorage;

        // 流程完成整组预检后，复制框架活动位，再交给动作复制其私有状态。
        internal void ApplyRuntimeCopyTo(CharacterBehavior target)
        {
            target.IsActive = IsActive;
            target.Execution = Execution;
            target.HeldRequest = HeldRequest;
            CopyRuntimeStateTo(target);
        }

        // 完成整组运行状态复制后，将当前动作与目标的预测定义绑定。
        internal void RebindPredictionDefinition() => BindPredictionDefinition();

        // 仅在启动回调成功后提交活动状态，异常时释放未完成的启动。
        internal void Activate(CharacterBehaviorStart request)
        {
            try
            {
                StartPrepared(request);
            }
            catch
            {
                IsActive = false;
                throw;
            }
            IsActive = true;
        }

        // 结束回调报错时仍释放框架拥有的活动状态。
        internal void Deactivate()
        {
            try
            {
                End();
            }
            finally
            {
                IsActive = false;
            }
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
        /// <summary>预测与预演只计算独占状态，不能发送真实事件或修改场景对象。</summary>
        public bool IsPrediction { get; }
        public double SimulationTime { get; }
        public Vector2 GroundVelocity { get; }
        public bool SeparateBoundaries { get; }

        // 保存外部调用者提供的共享输入、体力与兼容冷却快照。
        public CharacterBehaviorContext(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, float staminaRemaining, float dashCooldownRemaining)
        {
            Input = input;
            Facing = facing;
            DeltaTime = InputMath.NonNegative(deltaTime, 0f);
            Settings = settings ?? new CharacterMovementSettings();
            StaminaRemaining = staminaRemaining;
            DashCooldownRemaining = dashCooldownRemaining;
            IsPrediction = false;
            SeparateBoundaries = false;
            SimulationTime = 0d;
            GroundVelocity = default;
        }

        // 捕获该步的角色时钟、真实地速、端点规则及预测语境。
        internal CharacterBehaviorContext(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, float stamina, float dashCooldown, double simulationTime, Vector2 groundVelocity, bool separateBoundaries = false, bool isPrediction = false)
            : this(input, facing, deltaTime, settings, stamina, dashCooldown)
        {
            IsPrediction = isPrediction;
            SeparateBoundaries = separateBoundaries;
            SimulationTime = simulationTime;
            GroundVelocity = groundVelocity;
        }
    }
}
