using System;
using ThreeC.Player;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    public enum CharacterAttackMove { None, LightOne, LightTwo, Heavy }

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

    [Serializable]
    internal struct CharacterAttackLink
    {
        [InspectorName("当前招式")] public CharacterAttackMove From;
        [InspectorName("按键")] public AttackButton Button;
        [InspectorName("下一招")] public CharacterAttackMove To;

        public CharacterAttackLink(CharacterAttackMove from, AttackButton button, CharacterAttackMove to)
        {
            From = from;
            Button = button;
            To = to;
        }
    }

    [Serializable]
    internal sealed class CharacterAttackComboSettings
    {
        [InspectorName("允许无片段的攻击行为")] public bool AllowWithoutClip;
        [InspectorName("轻击二")] public CharacterAttackMoveSettings LightTwo = new CharacterAttackMoveSettings();
        [InspectorName("重击")] public CharacterAttackMoveSettings Heavy = new CharacterAttackMoveSettings();
        [Min(0.01f), InspectorName("无片段轻击时长（秒）")]
        public float LightSeconds = 0.6f;
        [Min(0.01f), InspectorName("无片段重击时长（秒）")] public float HeavySeconds = 0.8f;
        [Range(0f, 1f), InspectorName("开始预输入")] public float BufferStart = 0.40f;
        [Range(0f, 1f), InspectorName("开放接续")] public float LinkStart = 0.58f;
        [Range(0f, 1f), InspectorName("关闭接续")] public float LinkEnd = 0.85f;
        [InspectorName("接续表"), Tooltip("None 表示起手，同一来源和按键取首条匹配。")]
        public CharacterAttackLink[] Links =
        {
            new CharacterAttackLink(CharacterAttackMove.None, AttackButton.Light, CharacterAttackMove.LightOne),
            new CharacterAttackLink(CharacterAttackMove.None, AttackButton.Heavy, CharacterAttackMove.Heavy),
            new CharacterAttackLink(CharacterAttackMove.LightOne, AttackButton.Light, CharacterAttackMove.LightTwo),
            new CharacterAttackLink(CharacterAttackMove.LightOne, AttackButton.Heavy, CharacterAttackMove.Heavy),
            new CharacterAttackLink(CharacterAttackMove.LightTwo, AttackButton.Heavy, CharacterAttackMove.Heavy)
        };

        public CharacterAttackMove Next(CharacterAttackMove current, AttackButtonPresses presses)
        {
            var first = Find(current, presses.First);
            return first != CharacterAttackMove.None ? first : Find(current, presses.Second);
        }

        internal static CharacterAttackMoveSettings Definition(CharacterAttackMove move,
            CharacterLightAttackSettings first, CharacterAttackComboSettings combo) => move switch
        {
            CharacterAttackMove.LightOne => first,
            CharacterAttackMove.LightTwo => combo?.LightTwo,
            CharacterAttackMove.Heavy => combo?.Heavy,
            _ => null
        };

        private CharacterAttackMove Find(CharacterAttackMove current, AttackButton button)
        {
            if (button == AttackButton.None || Links == null) return CharacterAttackMove.None;
            foreach (var link in Links)
                if (link.From == current && link.Button == button) return link.To;
            return CharacterAttackMove.None;
        }
    }

    /// <summary>同一许可内按接续表切招。每招的播放、转向、位移和打断窗口共用固定步时钟。</summary>
    internal sealed class CharacterAttackBehavior : CharacterBehavior
    {
        private readonly CharacterLightAttackSettings settings;
        private readonly CharacterAttackComboSettings combo;
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

        internal CharacterAttackBehavior(CharacterLightAttackSettings settings)
        {
            this.settings = settings;
            combo = settings.Combo ?? new CharacterAttackComboSettings();
        }

        public override bool Evaluate(CharacterBehaviorContext context) =>
            combo.Next(CharacterAttackMove.None, context.Input.AttackPresses) != CharacterAttackMove.None;

        private CharacterAttackMoveSettings SettingsFor(CharacterAttackMove attack) =>
            CharacterAttackComboSettings.Definition(attack, settings, combo);

        public override CharacterBehaviorRejection GetStartRejection(CharacterBehaviorContext context) =>
            combo.BufferStart >= 0f && combo.BufferStart <= combo.LinkStart && combo.LinkStart <= combo.LinkEnd &&
            combo.LinkEnd <= 1f && DurationFor(combo.Next(CharacterAttackMove.None, context.Input.AttackPresses)) > 0f
                ? CharacterBehaviorRejection.None : CharacterBehaviorRejection.InvalidSettings;

        private float DurationFor(CharacterAttackMove next)
        {
            var definition = SettingsFor(next);
            if (definition == null || !definition.HasValidPhases) return 0f;
            if (definition.Clip)
                return definition.Clip.isLooping ? 0f : definition.Clip.length;
            if (!combo.AllowWithoutClip) return 0f;
            return InputMath.NonNegative(next == CharacterAttackMove.Heavy ? combo.HeavySeconds : combo.LightSeconds, 0f);
        }

        public override void Start(CharacterBehaviorContext context)
        {
            base.Start(context);
            Enter(combo.Next(CharacterAttackMove.None, context.Input.AttackPresses), context);
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
                elapsed >= combo.BufferStart * duration && elapsed <= combo.LinkEnd * duration)
                bufferedMove = combo.Next(move, context.Input.AttackPresses);
            openingStep = false;

            float seconds = context.DeltaTime;
            float untilLink = Mathf.Max(0f, combo.LinkStart * duration - elapsed);
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
