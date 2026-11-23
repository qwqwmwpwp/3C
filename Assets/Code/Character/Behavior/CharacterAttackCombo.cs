using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
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

        internal CharacterAttackOpening(CharacterAttackLink link, CharacterAttackMoveSettings definition, float duration)
        {
            Link = link;
            Definition = definition;
            Duration = duration;
        }
    }

    /// <summary>同一许可内按接续表切招。每招的播放、转向、位移和打断窗口共用固定步时钟。</summary>
    internal sealed class CharacterAttackBehavior : CharacterBehavior
    {
        private readonly CharacterLightAttackSettings settings;
        private readonly CharacterComboRoutes routes;
        private CharacterAttackMove move;
        private CharacterAttackLink bufferedLink;
        private Vector2 blendRange;
        private const float TimingTolerance = 0.000001f;
        private readonly Dictionary<CharacterAttackMove, double> readyAt = new Dictionary<CharacterAttackMove, double>();
        private bool movingOpening;
        private float bufferedAt;
        private string bufferResult;
        private long instanceId;
        private bool openingStep;
        private float duration, elapsed, startProgress, strike, recovery;
        private float? recoveryMoveStart;
        private CharacterAttackMotion attackMotion;
        private bool hasAttackMotion;
        public override bool SharesMovementOnStart => true;
        internal CharacterAttackState State => move == CharacterAttackMove.None ? default :
            new CharacterAttackState(move, instanceId, Phase, startProgress, elapsed / duration, duration,
                blendRange, recoveryMoveStart, bufferedLink.To, bufferedAt, bufferResult);
        private bool moveStarted;
        public override CharacterBehaviorPhase Phase => elapsed < strike ? CharacterBehaviorPhase.Startup :
            elapsed < recovery ? CharacterBehaviorPhase.Active : CharacterBehaviorPhase.Recovery;
        public override bool IsComplete => elapsed >= duration;

        internal CharacterAttackBehavior(CharacterLightAttackSettings settings, CharacterComboRoutes routes)
        {
            this.settings = settings;
            this.routes = routes;
        }

        internal override CharacterBehaviorContext PrepareRequest(CharacterBehaviorContext context) =>
            IsActive ? context : new CharacterBehaviorContext(context, Opening(context));

        private CharacterAttackOpening Opening(CharacterBehaviorContext context)
        {
            if (context.AttackOpening.HasValue) return context.AttackOpening.Value;
            CharacterAttackOpening rejected = default;
            var presses = context.Input.AttackPresses;
            for (int i = 0; i < presses.Count; i++)
            {
                var link = CharacterComboRoutes.Find(routes.Openers, presses[i], IsMoving(context));
                if (link.To == CharacterAttackMove.None) continue;
                var opening = ResolveMove(link);
                if (opening.Duration > 0f && IsReady(link.To, context.SimulationTime)) return opening;
                rejected = opening;
            }
            return rejected;
        }

        private static bool IsMoving(CharacterBehaviorContext context) => context.GroundVelocity.sqrMagnitude > 0.0025f;

        private bool IsReady(CharacterAttackMove target, double at) =>
            !readyAt.TryGetValue(target, out double ready) || ready <= at + TimingTolerance;

        public override bool Evaluate(CharacterBehaviorContext context) => Opening(context).Link.To != CharacterAttackMove.None;

        private CharacterAttackMoveSettings SettingsFor(CharacterAttackMove attack) => settings.Moves?.Move(attack);

        public override CharacterBehaviorRejection GetStartRejection(CharacterBehaviorContext context)
        {
            var opening = Opening(context);
            if (opening.Duration <= 0f) return CharacterBehaviorRejection.InvalidSettings;
            return IsReady(opening.Link.To, context.SimulationTime) ? CharacterBehaviorRejection.None :
                CharacterBehaviorRejection.Cooldown;
        }

        private CharacterAttackOpening ResolveMove(CharacterAttackLink link)
        {
            var next = link.To;
            var definition = SettingsFor(next);
            float seconds = settings.DurationFor(next, definition);
            return new CharacterAttackOpening(link, definition, seconds);
        }

        public override void Start(CharacterBehaviorContext context)
        {
            base.Start(context);
            movingOpening = IsMoving(context);
            bufferResult = null;
            Enter(Opening(context), context, 0f, true);
            openingStep = true;
        }

        private void Enter(CharacterAttackOpening opening, CharacterBehaviorContext context, float stepOffset, bool first)
        {
            move = opening.Link.To;
            blendRange = opening.Link.BlendRange;
            instanceId++;
            duration = opening.Duration;
            elapsed = startProgress = 0f;
            var definition = opening.Definition;
            float cooldown = InputMath.NonNegative(definition.CooldownSeconds, 0f);
            if (cooldown > 0f) readyAt[move] = context.SimulationTime + stepOffset + cooldown;
            strike = definition.StrikeStart * duration;
            recovery = definition.RecoveryStart * duration;
            recoveryMoveStart = null;
            hasAttackMotion = definition.HasMotionClip;
            if (hasAttackMotion)
                attackMotion = new CharacterAttackMotion(definition, context, duration, strike, recovery,
                    first ? InputMath.NonNegative(settings.OpeningStepDistance, 0f) : (float?)null, movingOpening);
            ClearBuffer();
        }

        internal override void CopyStateTo(CharacterBehavior target)
        {
            base.CopyStateTo(target);
            var copy = (CharacterAttackBehavior)target;
            copy.move = move;
            copy.bufferedLink = bufferedLink;
            copy.bufferedAt = bufferedAt;
            copy.movingOpening = movingOpening;
            copy.bufferResult = bufferResult;
            copy.readyAt.Clear();
            foreach (var pair in readyAt) copy.readyAt.Add(pair.Key, pair.Value);
            copy.blendRange = blendRange;
            copy.instanceId = instanceId;
            copy.openingStep = openingStep;
            copy.duration = duration;
            copy.elapsed = elapsed;
            copy.startProgress = startProgress;
            copy.strike = strike;
            copy.recovery = recovery;
            copy.recoveryMoveStart = recoveryMoveStart;
            copy.attackMotion = attackMotion;
            if (hasAttackMotion) copy.attackMotion.BindSettings(copy.SettingsFor(move));
            copy.hasAttackMotion = hasAttackMotion;
            copy.moveStarted = moveStarted;
        }

        internal void ClearBuffer()
        {
            bufferedLink = default;
            bufferedAt = 0f;
        }

        internal override void Reset()
        {
            base.Reset();
            readyAt.Clear();
            ClearBuffer();
            bufferResult = null;
        }

        private void ReadLinks(CharacterBehaviorContext context)
        {
            var presses = context.Input.AttackPresses;
            if (presses.Count == 0) return;
            var source = routes.ForMove(move);
            for (int i = 0; i < presses.Count; i++)
            {
                var link = CharacterComboRoutes.Find(source?.Links, presses[i], movingOpening);
                if (link.To == CharacterAttackMove.None)
                {
                    bufferResult = CharacterComboRoutes.Find(source?.Links, presses[i]).To == CharacterAttackMove.None ?
                        "未覆盖：当前招式无此路线" : "未覆盖：起手条件不满足";
                    continue;
                }
                if (!source.HasValidWindow)
                {
                    bufferResult = "未覆盖：当前招式的接续时间无效";
                    continue;
                }
                float open = source.LinkStart * duration;
                float early = Mathf.Max(0f, open - source.EarlySeconds);
                if (elapsed + TimingTolerance < early || elapsed > source.LinkEnd * duration + TimingTolerance)
                {
                    bufferResult = "未覆盖：窗口外";
                    continue;
                }
                float linkAt = Mathf.Max(open, elapsed);
                if (!IsReady(link.To, context.SimulationTime + linkAt - elapsed))
                {
                    bufferResult = "未覆盖：接续时仍在冷却";
                    continue;
                }
                if (ResolveMove(link).Duration <= 0f)
                {
                    bufferResult = "未覆盖：条件或招式配置不满足";
                    continue;
                }
                bufferResult = bufferedLink.To == CharacterAttackMove.None ? "已预输入" : "后一次输入已覆盖";
                bufferedLink = link;
                bufferedAt = linkAt;
            }
        }

        internal void RecordStep(bool moveApproved, ref CharacterBehaviorStep step)
        {
            if (!IsActive) return;
            if (moveApproved && Phase == CharacterBehaviorPhase.Recovery && !recoveryMoveStart.HasValue)
                recoveryMoveStart = Mathf.Max(startProgress, recovery / duration);
            step.Attack = State;
            step.AttackMoveStarted = moveStarted;
        }

        public override void End()
        {
            base.End();
            move = CharacterAttackMove.None;
            hasAttackMotion = false;
            ClearBuffer();
        }

        public override Vector2 Update(CharacterBehaviorContext context) => AdvanceStep(context).Displacement;

        public override CharacterBehaviorMotion AdvanceStep(CharacterBehaviorContext context)
        {
            moveStarted = openingStep;
            if (!openingStep) ReadLinks(context);
            openingStep = false;

            float seconds = context.DeltaTime;
            float untilLink = Mathf.Max(0f, bufferedAt - elapsed);
            if (bufferedLink.To == CharacterAttackMove.None || untilLink > seconds + TimingTolerance)
                return Advance(context, seconds);

            var next = ResolveMove(bufferedLink);
            untilLink = Mathf.Min(seconds, untilLink);
            if (next.Duration <= 0f || !IsReady(next.Link.To, context.SimulationTime + untilLink))
            {
                ClearBuffer();
                return Advance(context, seconds);
            }
            var previous = Advance(context, untilLink);
            Enter(next, context, untilLink, false);
            moveStarted = true;
            var current = Advance(context, seconds - untilLink);
            // 两段均消耗本步时间，帧末的阶段、转向与表现时钟属于接入的新招。
            return new CharacterBehaviorMotion(previous.Displacement + current.Displacement,
                previous.OrdinaryMovementSeconds + current.OrdinaryMovementSeconds, current.ControlsDisplacement, current.Motion);
        }

        private CharacterBehaviorMotion Advance(CharacterBehaviorContext context, float seconds)
        {
            float before = elapsed;
            startProgress = before / duration;
            elapsed = Mathf.Min(duration, elapsed + seconds);
            if (!hasAttackMotion)
                return new CharacterBehaviorMotion(Vector2.zero, seconds, controlsDisplacement: false);

            Vector2 displacement = attackMotion.Sample(context, before, elapsed, out var motion, out float movementSeconds);
            movementSeconds += Mathf.Max(0f, before + seconds - duration); // 自然结束后的步内余量交还给移动。
            return new CharacterBehaviorMotion(displacement, movementSeconds, Phase == CharacterBehaviorPhase.Active || motion.OpeningStep, motion);
        }
    }
}
