using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    public enum CharacterAttackMove
    {
        [InspectorName("无（起手）")] None,
        [InspectorName("轻击一")] LightOne,
        [InspectorName("轻击二")] LightTwo,
        [InspectorName("重击")] Heavy
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

        internal CharacterAttackState(CharacterAttackMove move, long instanceId, CharacterBehaviorPhase phase,
            float startProgress, float progress, float duration, float strikeStart, float recoveryStart,
            float? recoveryMoveStart, float recoveryMoveBlendSeconds)
        {
            Move = move;
            InstanceId = instanceId;
            Phase = phase;
            StartProgress = startProgress;
            Progress = progress;
            Duration = duration;
            Handoff = new CharacterActionHandoff(duration, strikeStart, recoveryStart, recoveryMoveBlendSeconds, recoveryMoveStart);
        }
    }

    /// <summary>同一许可内按接续表切招。每招的播放、转向、位移和打断窗口共用固定步时钟。</summary>
    internal sealed class CharacterAttackBehavior : CharacterBehavior
    {
        private readonly CharacterLightAttackSettings settings;
        private readonly CharacterComboRoutes routes;
        private CharacterAttackMove move, bufferedMove;
        private long instanceId;
        private bool openingStep;
        private float duration, elapsed, startProgress, strike, recovery;
        private float? recoveryMoveStart;
        private float recoveryMoveBlendSeconds;
        private CharacterAttackMotion attackMotion;
        private bool hasAttackMotion;
        public override bool SharesMovementOnStart => true;
        internal CharacterAttackState State => move == CharacterAttackMove.None ? default :
            new CharacterAttackState(move, instanceId, Phase, startProgress, elapsed / duration, duration,
                strike / duration, recovery / duration, recoveryMoveStart, recoveryMoveBlendSeconds);
        private bool moveStarted;
        public override CharacterBehaviorPhase Phase => elapsed < strike ? CharacterBehaviorPhase.Startup :
            elapsed < recovery ? CharacterBehaviorPhase.Active : CharacterBehaviorPhase.Recovery;
        public override bool IsComplete => elapsed >= duration;

        internal CharacterAttackBehavior(CharacterLightAttackSettings settings, CharacterComboRoutes routes)
        {
            this.settings = settings;
            this.routes = routes;
        }

        public override bool Evaluate(CharacterBehaviorContext context) =>
            routes.Next(CharacterAttackMove.None, context.Input.AttackPresses) != CharacterAttackMove.None;

        private CharacterAttackMoveSettings SettingsFor(CharacterAttackMove attack) =>
            settings.Moves?.Move(attack);

        public override CharacterBehaviorRejection GetStartRejection(CharacterBehaviorContext context) =>
            routes.BufferStart >= 0f && routes.BufferStart <= routes.LinkStart && routes.LinkStart <= routes.LinkEnd &&
            routes.LinkEnd <= 1f && DurationFor(routes.Next(CharacterAttackMove.None, context.Input.AttackPresses)) > 0f
                ? CharacterBehaviorRejection.None : CharacterBehaviorRejection.InvalidSettings;

        private float DurationFor(CharacterAttackMove next)
        {
            var definition = SettingsFor(next);
            if (definition == null || !definition.HasValidPhases) return 0f;
            if (definition.Clip)
                return definition.Clip.isLooping ? 0f : definition.Clip.length;
            if (!settings.AllowWithoutClip) return 0f;
            return InputMath.NonNegative(next == CharacterAttackMove.Heavy ? settings.HeavySeconds : settings.LightSeconds, 0f);
        }

        public override void Start(CharacterBehaviorContext context)
        {
            base.Start(context);
            Enter(routes.Next(CharacterAttackMove.None, context.Input.AttackPresses), context);
            openingStep = true;
        }

        private void Enter(CharacterAttackMove next, CharacterBehaviorContext context)
        {
            move = next;
            instanceId++;
            duration = DurationFor(next);
            elapsed = startProgress = 0f;
            var definition = SettingsFor(next);
            strike = definition.StrikeStart * duration;
            recovery = definition.RecoveryStart * duration;
            recoveryMoveStart = null;
            recoveryMoveBlendSeconds = InputMath.NonNegative(definition.RecoveryMoveBlendSeconds, 0.16f);
            hasAttackMotion = definition.Clip;
            if (hasAttackMotion)
                attackMotion = new CharacterAttackMotion(definition, context, duration, strike, recovery);
            ClearBuffer();
        }

        internal override void CopyStateTo(CharacterBehavior target)
        {
            base.CopyStateTo(target);
            var copy = (CharacterAttackBehavior)target;
            copy.move = move;
            copy.bufferedMove = bufferedMove;
            copy.instanceId = instanceId;
            copy.openingStep = openingStep;
            copy.duration = duration;
            copy.elapsed = elapsed;
            copy.startProgress = startProgress;
            copy.strike = strike;
            copy.recovery = recovery;
            copy.recoveryMoveStart = recoveryMoveStart;
            copy.recoveryMoveBlendSeconds = recoveryMoveBlendSeconds;
            copy.attackMotion = attackMotion;
            copy.hasAttackMotion = hasAttackMotion;
            copy.moveStarted = moveStarted;
        }

        internal void ClearBuffer() => bufferedMove = CharacterAttackMove.None;

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
            if (!openingStep && bufferedMove == CharacterAttackMove.None &&
                elapsed >= routes.BufferStart * duration && elapsed <= routes.LinkEnd * duration)
                bufferedMove = routes.Next(move, context.Input.AttackPresses);
            openingStep = false;

            float seconds = context.DeltaTime;
            float untilLink = Mathf.Max(0f, routes.LinkStart * duration - elapsed);
            if (bufferedMove == CharacterAttackMove.None || untilLink > seconds)
                return Advance(context, seconds);

            var next = bufferedMove;
            if (DurationFor(next) <= 0f)
            {
                ClearBuffer();
                return Advance(context, seconds);
            }
            var previous = Advance(context, untilLink);
            Enter(next, context);
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
            return new CharacterBehaviorMotion(displacement, movementSeconds, Phase == CharacterBehaviorPhase.Active, motion);
        }
    }
}
