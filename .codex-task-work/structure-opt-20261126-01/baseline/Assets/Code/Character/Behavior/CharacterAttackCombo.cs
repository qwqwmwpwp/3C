using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>标识攻击招式，供参数配置、接续和表现映射查找。</summary>
    public enum CharacterAttackMove
    {
        [InspectorName("无（起手）")] None = 0,
        [InspectorName("轻击一")] LightOne = 1,
        [InspectorName("轻击二")] LightTwo = 2,
        [InspectorName("重击")] Heavy = 3,
        [InspectorName("技能 2")] SkillTwo = 4
    }

    /// <summary>本步执行的招式。自然结束的最后一步仍保留进度，空闲及被打断的步返回默认值。</summary>
    public readonly struct CharacterAttackState
    {
        public CharacterAttackMove Move { get; }
        public long InstanceId { get; }
        public CharacterBehaviorPhase Phase { get; }
        public float StartProgress { get; }
        public float Progress { get; }
        public float Duration { get; } // 当前招式的逻辑时长，单位为秒。
        internal CharacterActionHandoff Handoff { get; }
        public CharacterAttackMove BufferedMove { get; }
        public float LinkAtSeconds { get; }
        public string BufferResult { get; }

        // 封装当前招式时钟、表现交接及预输入观测。
        internal CharacterAttackState(CharacterAttackMove move, long instanceId, CharacterBehaviorPhase phase,
            float startProgress, float progress, float duration, Vector2 blendRange, float? recoveryMoveStart,
            CharacterAttackMove bufferedMove = default, float linkAt = 0f, string result = null)
        {
            BufferedMove = bufferedMove;
            LinkAtSeconds = linkAt;
            BufferResult = result;
            Move = move;
            InstanceId = instanceId;
            Phase = phase;
            StartProgress = startProgress;
            Progress = progress;
            Duration = duration;
            Handoff = new CharacterActionHandoff(duration, blendRange, recoveryMoveStart);
        }
    }

    /// <summary>本次请求已解析的招式及其原速时长，只在当前固定步内传递。</summary>
    internal readonly struct CharacterAttackOpening
    {
        internal readonly CharacterAttackLink Link;
        internal readonly CharacterAttackMoveSettings Definition;
        internal readonly float Duration;

        // 保存本次准入请求已经解析的路线和有效时长。
        internal CharacterAttackOpening(CharacterAttackLink link, CharacterAttackMoveSettings definition, float duration)
        {
            Link = link;
            Definition = definition;
            Duration = duration;
        }
    }

    // 让已解析的起手招式随本次申请交给获准的攻击实例。
    internal sealed class CharacterAttackStart : CharacterBehaviorStart
    {
        internal readonly CharacterAttackOpening Opening;

        // 保存本次攻击的公共报价与选定起手招式。
        internal CharacterAttackStart(CharacterBehaviorContext context, float cost, CharacterAttackOpening opening)
            : base(context, cost) => Opening = opening;
    }

    /// <summary>同一许可内按接续表切招。每招的播放、转向、位移和打断窗口共用固定步时钟。</summary>
    internal sealed class CharacterAttackBehavior : CharacterBehavior, ICharacterIntervalMotion
    {
        private readonly CharacterLightAttackSettings settings;
        private readonly CharacterComboRoutes routes;
        private const float TimingTolerance = 0.000001f;
        private readonly Dictionary<CharacterAttackMove, double> readyAt = new Dictionary<CharacterAttackMove, double>();
        private CharacterAttackRuntimeState state;
        public override bool SharesMovementOnStart => true;
        public override bool DefersCompletionAtEndpoint => true;
        public override float InputEndpointTolerance => TimingTolerance;
        protected override object ExclusiveRuntimeStorage => readyAt;
        internal CharacterAttackState State => state.Move == CharacterAttackMove.None ? default :
            new CharacterAttackState(state.Move, state.InstanceId, Phase, state.StartProgress, (float)(state.Elapsed / state.Duration),
                state.Duration, state.BlendRange, state.RecoveryMoveStart, state.BufferedLink.To, (float)state.BufferedAt, state.BufferResult);
        public override bool AllowsOrdinaryMotionNow =>
            !state.HasAttackMotion || state.Elapsed >= state.Recovery || state.Elapsed < state.Strike && !state.AttackMotion.HasOpening;
        public override double NextBoundaryDelay
        {
            get
            {
                if (!IsActive) return double.PositiveInfinity;
                double next = state.NextPhaseEnd;
                if (state.BufferedLink.To != CharacterAttackMove.None && state.BufferedAt > state.Elapsed)
                    next = Math.Min(next, state.BufferedAt);
                return next - state.Elapsed;
            }
        }
        public override CharacterBehaviorPhase Phase =>
            state.Elapsed < state.Strike ? CharacterBehaviorPhase.Startup :
            state.Elapsed < state.Recovery ? CharacterBehaviorPhase.Active : CharacterBehaviorPhase.Recovery;
        public override bool IsComplete => state.Elapsed >= state.Duration;

        // 按值冻结招式的运动和阶段时钟，无片段的招式也通过同一协议交还移动。
        public CharacterFrozenMotion FreezeMotion(in CharacterMotionCapture capture) =>
            new CharacterFrozenAttackMotion(in state, in capture);

        // 绑定此行为实际查阅的招式和接续定义。
        internal CharacterAttackBehavior(CharacterLightAttackSettings settings, CharacterComboRoutes routes)
        {
            this.settings = settings;
            this.routes = routes;
        }

        // 为有效按键解析一次起手招式，保留至该申请得到准入。
        internal override CharacterBehaviorStart PrepareStart(CharacterBehaviorContext context)
        {
            if (IsActive) return null;
            var opening = SelectOpening(context);
            return opening.Link.To == CharacterAttackMove.None ? null
                : new CharacterAttackStart(context, GetStaminaCost(context), opening);
        }

        // 使用准入时选定的招式启动攻击。
        internal override void StartPrepared(CharacterBehaviorStart request) =>
            StartAttack(request.Context, ((CharacterAttackStart)request).Opening);

        // 按按键顺序选取符合起手条件且冷却已好的第一招。
        private CharacterAttackOpening SelectOpening(CharacterBehaviorContext context)
        {
            var presses = context.Input.AttackPresses;
            bool moving = IsMoving(context);
            for (int i = 0; i < presses.Count; i++)
            {
                var link = CharacterComboRoutes.Find(routes.Openers, presses[i], moving);
                if (link.To == CharacterAttackMove.None) continue;
                var opening = ResolveMove(link);
                if (opening.Duration > 0f && !float.IsInfinity(opening.Duration) && IsReady(link.To, context.SimulationTime))
                    return opening;
            }
            return default;
        }

        // 从当前实际地速判定移动起手。
        private static bool IsMoving(CharacterBehaviorContext context) => context.GroundVelocity.sqrMagnitude > 0.0025f;

        // 按角色时刻和端点容差检查目标招式的冷却。
        private bool IsReady(CharacterAttackMove target, double at) =>
            !readyAt.TryGetValue(target, out double ready) || ready <= at + TimingTolerance;

        // 只读检查是否收到可执行的起手请求。
        public override bool Evaluate(CharacterBehaviorContext context) =>
            !IsActive && SelectOpening(context).Link.To != CharacterAttackMove.None;

        // 沿用当前定义按招式编号查找参数。
        private CharacterAttackMoveSettings SettingsFor(CharacterAttackMove attack) => settings.Moves?.Move(attack);

        // 解析目标路线的参数和原速时长。
        private CharacterAttackOpening ResolveMove(CharacterAttackLink link)
        {
            var next = link.To;
            var definition = SettingsFor(next);
            float seconds = settings.DurationFor(next, definition);
            return new CharacterAttackOpening(link, definition, seconds);
        }

        // 开始攻击许可并记录首招和移动起手条件。
        public override void Start(CharacterBehaviorContext context) => StartAttack(context, SelectOpening(context));

        // 初始化攻击许可与首招的运行状态。
        private void StartAttack(CharacterBehaviorContext context, CharacterAttackOpening opening)
        {
            base.Start(context);
            state.MovingOpening = IsMoving(context);
            state.BufferResult = null;
            Enter(opening, context, 0f, true);
            state.OpeningStep = true;
        }

        // 进入指定招式，按当前配置锁定时钟、位移参数和冷却。
        private void Enter(CharacterAttackOpening opening, CharacterBehaviorContext context, float stepOffset, bool first)
        {
            state.Move = opening.Link.To;
            state.BlendRange = opening.Link.BlendRange;
            state.InstanceId++;
            state.Duration = opening.Duration;
            state.Elapsed = state.StartProgress = 0f;
            var definition = opening.Definition;
            float cooldown = InputMath.NonNegative(definition.CooldownSeconds, 0f);
            if (cooldown > 0f) readyAt[state.Move] = context.SimulationTime + stepOffset + cooldown;
            state.Strike = definition.StrikeStart * state.Duration;
            state.Recovery = definition.RecoveryStart * state.Duration;
            state.RecoveryMoveStart = null;
            state.HasAttackMotion = definition.HasMotionClip;
            state.Alignment = definition.Alignment;
            if (state.HasAttackMotion)
                state.AttackMotion = new CharacterAttackMotion(definition, context, state.Duration, state.Strike, state.Recovery,
                    first ? InputMath.NonNegative(settings.OpeningStepDistance, 0f) : (float?)null, state.MovingOpening);
            ClearBuffer();
        }

        // 保留当前招式在起手时锁定的运动数值，后续招式由目标自己的定义解析。
        protected override void CopyRuntimeStateTo(CharacterBehavior target)
        {
            var copy = (CharacterAttackBehavior)target;
            copy.state = state;
            copy.readyAt.Clear();
            foreach (var pair in readyAt) copy.readyAt.Add(pair.Key, pair.Value);
        }

        // 清除已缓存的接续路线和执行时点。
        internal void ClearBuffer()
        {
            state.BufferedLink = default;
            state.BufferedAt = 0f;
        }

        // 清除跨活动期的招式冷却和预输入观测。
        internal override void Reset()
        {
            base.Reset();
            readyAt.Clear();
            ClearBuffer();
            state.BufferResult = null;
        }

        // 按原输入顺序处理仍在窗口内的有效接续，后一次有效请求可覆盖先前请求。
        private void ReadLinks(CharacterBehaviorContext context)
        {
            var presses = context.Input.AttackPresses;
            if (presses.Count == 0) return;
            var source = routes.ForMove(state.Move);
            for (int i = 0; i < presses.Count; i++)
            {
                var link = CharacterComboRoutes.Find(source?.Links, presses[i], state.MovingOpening);
                if (link.To == CharacterAttackMove.None)
                {
                    state.BufferResult = CharacterComboRoutes.Find(source?.Links, presses[i]).To == CharacterAttackMove.None ?
                        "未覆盖：当前招式无此路线" : "未覆盖：起手条件不满足";
                    continue;
                }
                if (!source.HasValidWindow)
                {
                    state.BufferResult = "未覆盖：当前招式的接续时间无效";
                    continue;
                }
                float open = source.LinkStart * state.Duration;
                float early = Mathf.Max(0f, open - source.EarlySeconds);
                float end = source.LinkEnd * state.Duration;
                bool inWindow = context.SeparateBoundaries
                    ? state.Elapsed + TimingTolerance >= early && state.Elapsed <= (double)end + TimingTolerance
                    : (float)state.Elapsed + TimingTolerance >= early && (float)state.Elapsed <= end + TimingTolerance;
                if (!inWindow)
                {
                    state.BufferResult = "未覆盖：窗口外";
                    continue;
                }
                double linkAt = Math.Max(open, state.Elapsed);
                if (!IsReady(link.To, context.SimulationTime + linkAt - state.Elapsed))
                {
                    state.BufferResult = "未覆盖：接续时仍在冷却";
                    continue;
                }
                if (ResolveMove(link).Duration <= 0f)
                {
                    state.BufferResult = "未覆盖：条件或招式配置不满足";
                    continue;
                }
                state.BufferResult = state.BufferedLink.To == CharacterAttackMove.None ? "已预输入" : "后一次输入已覆盖";
                state.BufferedLink = link;
                state.BufferedAt = linkAt;
            }
        }

        // 记录收招交还移动的时刻，并发布本步实际执行的招式。
        internal override void RecordOutcome(bool moveApproved, ref CharacterBehaviorStep step)
        {
            if (!IsActive) return;
            if (moveApproved && Phase == CharacterBehaviorPhase.Recovery && !state.RecoveryMoveStart.HasValue)
                state.RecoveryMoveStart = Mathf.Max(state.StartProgress, state.Recovery / state.Duration);
            ReadOutcome(ref step);
            step.AttackMoveStarted = state.MoveStarted;
        }

        // 将当前招式与接续状态写入行为帧。
        internal override void ReadOutcome(ref CharacterBehaviorStep step) => step.Attack = State;

        // 暂停或失去输入连续性时清除尚未执行的接续。
        protected override void OnInputContinuityEnded() => ClearBuffer();

        // 结束本次攻击并释放活动招式、运动和预输入。
        public override void End()
        {
            base.End();
            state.Move = CharacterAttackMove.None;
            state.HasAttackMotion = false;
            ClearBuffer();
        }

        // 通过原位移入口推进并返回本步攻击位移。
        public override Vector2 Update(CharacterBehaviorContext context) => AdvanceStep(context).Displacement;

        // 推进当前招式，在时间窗口允许时执行缓冲接续。
        public override CharacterBehaviorMotion AdvanceStep(CharacterBehaviorContext context)
        {
            state.MoveStarted = state.OpeningStep;
            if (!state.OpeningStep) ReadLinks(context);
            state.OpeningStep = false;

            float seconds = context.DeltaTime;
            if (!CanExecuteBufferedLink(context, out float untilLink))
                return Advance(context, seconds);

            var next = ResolveMove(state.BufferedLink);
            untilLink = Mathf.Min(seconds, untilLink);
            if (next.Duration <= 0f || !IsReady(next.Link.To, context.SimulationTime + untilLink))
            {
                ClearBuffer();
                return Advance(context, seconds);
            }
            var previous = Advance(context, untilLink);
            Enter(next, context, untilLink, false);
            state.MoveStarted = true;
            var current = Advance(context, seconds - untilLink);
            // 两段均消耗本步时间，帧末的阶段、转向与表现时钟属于接入的新招。
            return new CharacterBehaviorMotion(previous.Displacement + current.Displacement,
                previous.OrdinaryMovementSeconds + current.OrdinaryMovementSeconds, current.ControlsDisplacement, current.Motion);
        }

        // 分段时间线仅在已到达接续点的零裁决中切招，公开 Tick 仍允许在单步中切招。
        private bool CanExecuteBufferedLink(CharacterBehaviorContext context, out float untilLink)
        {
            untilLink = 0f;
            if (state.BufferedLink.To == CharacterAttackMove.None) return false;
            if (context.SeparateBoundaries)
                return context.DeltaTime == 0f && state.BufferedAt <= state.Elapsed;

            untilLink = Mathf.Max(0f, (float)state.BufferedAt - (float)state.Elapsed);
            return untilLink <= context.DeltaTime + TimingTolerance;
        }

        // 推进单招时钟并计算位移和交还时间。
        private CharacterBehaviorMotion Advance(CharacterBehaviorContext context, float seconds)
        {
            double before = state.Elapsed;
            state.StartProgress = (float)(before / state.Duration);
            AdvanceElapsed(seconds, context.SeparateBoundaries);
            if (!state.HasAttackMotion)
                return new CharacterBehaviorMotion(Vector2.zero, seconds, controlsDisplacement: false);

            Vector2 displacement = state.AttackMotion.Advance(context, before, state.Elapsed, out var motion, out float movementSeconds);
            // 自然结束后的步内余量交还给移动。
            movementSeconds += context.SeparateBoundaries ? (float)Math.Max(0d, before + seconds - state.Duration)
                : Mathf.Max(0f, (float)before + seconds - state.Duration);
            return new CharacterBehaviorMotion(displacement, movementSeconds, Phase == CharacterBehaviorPhase.Active || motion.OpeningStep, motion);
        }

        // 仅校正已到达的边界，以步长的 float 精度消除舍入余量。
        private void AdvanceElapsed(float seconds, bool separateBoundaries)
        {
            double before = state.Elapsed;
            if (!separateBoundaries)
            {
                state.Elapsed = Mathf.Min(state.Duration, (float)before + seconds);
                return;
            }

            if (seconds == 0f) return;
            float boundary = state.NextPhaseEnd;
            state.Elapsed = Math.Min(state.Duration, before + seconds);
            if (seconds >= (float)(boundary - before))
                state.Elapsed = Math.Max(state.Elapsed, boundary);
            if (state.BufferedLink.To != CharacterAttackMove.None && state.BufferedAt > before &&
                seconds >= (float)(state.BufferedAt - before))
                state.Elapsed = Math.Max(state.Elapsed, state.BufferedAt);
        }
    }

    /// <summary>保存单个攻击实例的招式时钟、接续、运动和表现交接值，独占冷却表由行为持有。</summary>
    internal struct CharacterAttackRuntimeState
    {
        internal CharacterAttackMove Move;
        internal long InstanceId;
        internal double Elapsed, BufferedAt; // 动作运行与接续时刻，单位为秒。
        internal float Duration, Strike, Recovery; // 定义中的原速时钟，单位为秒。
        internal float StartProgress;
        internal CharacterAttackLink BufferedLink;
        internal string BufferResult;
        internal Vector2 BlendRange;
        internal float? RecoveryMoveStart;
        internal bool MovingOpening, OpeningStep, MoveStarted;
        internal bool HasAttackMotion;
        internal CharacterAttackMotion AttackMotion;
        internal CharacterActionAlignmentSettings Alignment;
        internal readonly float NextPhaseEnd => Elapsed < Strike ? Strike : Elapsed < Recovery ? Recovery : Duration;
    }
}
