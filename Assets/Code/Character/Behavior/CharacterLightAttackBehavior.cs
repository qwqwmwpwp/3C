using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [Serializable]
    internal class CharacterAttackMoveSettings
    {
        [InspectorName("攻击片段（原速时钟）")] public AnimationClip Clip;
        [Range(0f, 1f), InspectorName("出招开始相位")] public float StrikeStart = 0.24f;
        [Range(0f, 1f), InspectorName("收招开始相位")] public float RecoveryStart = 0.58f;
        [Min(0f), InspectorName("后摇转移动淡入（秒）"), Tooltip("收招中首次获准移动时开始计时；0 表示直接交给移动。")]
        public float RecoveryMoveBlendSeconds = 0.16f;
        [Min(0f), InspectorName("起手转速（度/秒）")] public float StartupTurnSpeed = 180f;
        [Min(0.01f), InspectorName("起手制动（米/秒²）")] public float Braking = 28f;
        [Min(0f), InspectorName("出招前移距离（米）")] public float StrikeTravel;
        [Min(0f), InspectorName("招式冷却（秒）")] public float CooldownSeconds;
        private bool captured, hasClip;
        private float clipDuration;
        internal bool HasMotionClip => captured ? hasClip : (bool)Clip;
        internal float ClipDuration => captured ? clipDuration : Clip.isLooping ? 0f : Clip.length;

        internal CharacterAttackMoveSettings Snapshot()
        {
            var copy = (CharacterAttackMoveSettings)MemberwiseClone();
            copy.hasClip = HasMotionClip;
            copy.clipDuration = copy.hasClip ? ClipDuration : 0f;
            copy.Clip = null;
            copy.captured = true;
            return copy;
        }

        internal bool HasValidPhases => StrikeStart > 0f && RecoveryStart > StrikeStart && RecoveryStart < 1f;
    }

    [Serializable]
    internal sealed class CharacterLightAttackSettings
    {
        [Header("准入与打断")]
        [InspectorName("允许短突进打断的阶段")] public CharacterBehaviorPhase DashInterruptPhases =
            CharacterBehaviorPhase.Startup | CharacterBehaviorPhase.Recovery;
        [Header("执行参数")]
        [Min(0f), InspectorName("移动起手小步（米）")] public float OpeningStepDistance;
        [SerializeField, InspectorName("招式参数")] internal CharacterAttackComboSettings Moves = new CharacterAttackComboSettings();
        internal float DurationFor(CharacterAttackMove move, CharacterAttackMoveSettings definition)
        {
            if (definition == null || !definition.HasValidPhases) return 0f;
            if (definition.HasMotionClip) return definition.ClipDuration;
            return AllowWithoutClip
                ? InputMath.NonNegative(move == CharacterAttackMove.Heavy ? HeavySeconds : LightSeconds, 0f) : 0f;
        }

        internal CharacterLightAttackSettings Snapshot()
        {
            var copy = (CharacterLightAttackSettings)MemberwiseClone();
            copy.Moves = Moves == null ? null : new CharacterAttackComboSettings {
                LightOne = Moves.LightOne?.Snapshot(), LightTwo = Moves.LightTwo?.Snapshot(), Heavy = Moves.Heavy?.Snapshot(),
                Additional = Moves.Additional == null ? null : Array.ConvertAll(Moves.Additional,
                    entry => new CharacterAttackMoveEntry { Id = entry.Id, Parameters = entry.Parameters?.Snapshot() })
            };
            return copy;
        }

        [Header("无片段时的执行时长")]
        [InspectorName("允许无片段执行")] public bool AllowWithoutClip;
        [Min(0.01f), InspectorName("轻击时长（秒）")] public float LightSeconds = 0.6f;
        [Min(0.01f), InspectorName("重击时长（秒）")] public float HeavySeconds = 0.8f;
    }

    [Serializable]
    internal struct CharacterAttackMoveEntry
    {
        [InspectorName("招式")] public CharacterAttackMove Id;
        [InspectorName("参数")] public CharacterAttackMoveSettings Parameters;
    }

    [Serializable]
    internal sealed class CharacterAttackComboSettings
    {
        [InspectorName("轻击一")] public CharacterAttackMoveSettings LightOne = new CharacterAttackMoveSettings();
        [InspectorName("轻击二")] public CharacterAttackMoveSettings LightTwo = new CharacterAttackMoveSettings();
        [InspectorName("重击")] public CharacterAttackMoveSettings Heavy = new CharacterAttackMoveSettings();

        [InspectorName("追加招式")] public CharacterAttackMoveEntry[] Additional;
        internal bool HasClip => LightOne?.HasMotionClip == true || LightTwo?.HasMotionClip == true ||
            Heavy?.HasMotionClip == true || Additional != null && Array.Exists(Additional, entry => entry.Parameters?.HasMotionClip == true);

        internal CharacterAttackMoveSettings Move(CharacterAttackMove move) => move switch
        {
            CharacterAttackMove.LightOne => LightOne,
            CharacterAttackMove.LightTwo => LightTwo,
            CharacterAttackMove.Heavy => Heavy,
            CharacterAttackMove.None => null,
            _ => AdditionalMove(move)
        };

        private CharacterAttackMoveSettings AdditionalMove(CharacterAttackMove move)
        {
            if (Additional != null)
                foreach (var entry in Additional)
                    if (entry.Id == move) return entry.Parameters;
            return null;
        }
    }

    /// <summary>按当前招式的起手、出招和收招区间，计算本步位移与转向。</summary>
    internal struct CharacterAttackMotion
    {
        private CharacterAttackMoveSettings settings;
        internal void BindSettings(CharacterAttackMoveSettings value) => settings = value;
        private readonly float duration, strike, recovery;
        private Vector2 requestedFacing, strikeDirection;
        private readonly float? openingDistance; // 仅起手有值；零米也应由攻击接管前摇。
        private readonly Vector2 openingDirection;

        public CharacterAttackMotion(CharacterAttackMoveSettings settings, CharacterBehaviorContext context,
            float duration, float strike, float recovery, float? openingDistance = null, bool movingOpening = false)
        {
            this.settings = settings;
            this.duration = duration;
            this.openingDistance = openingDistance;
            openingDirection = movingOpening ? context.GroundVelocity.normalized : Vector2.zero;
            this.strike = strike;
            this.recovery = recovery;
            requestedFacing = context.Input.Direction == Vector2.zero ? context.Facing : context.Input.Direction.normalized;
            strikeDirection = context.Facing.normalized;
        }

        public Vector2 Sample(CharacterBehaviorContext context, float before, float elapsed,
            out CharacterActionMotion stepMotion, out float movementSeconds)
        {
            float startupTime = Mathf.Max(0f, Mathf.Min(elapsed, strike) - before);
            float strikeTime = Mathf.Max(0f, Mathf.Min(elapsed, recovery) - Mathf.Max(before, strike));
            float recoveryTime = Mathf.Max(0f, elapsed - Mathf.Max(before, recovery));
            float endRecovery = Mathf.InverseLerp(recovery, duration, elapsed);
            float meanRecovery = (Mathf.InverseLerp(recovery, duration, before) + endRecovery) * 0.5f;
            float meanStartup = (Mathf.Clamp01(before / strike) + Mathf.Clamp01(elapsed / strike)) * 0.5f;
            bool openingStep = openingDistance.HasValue && startupTime > 0f;
            movementSeconds = (openingStep ? 0f : startupTime * (1f - meanStartup)) + recoveryTime * meanRecovery;
            if (startupTime > 0f && context.Input.Direction != Vector2.zero)
                requestedFacing = context.Input.Direction.normalized;
            if (strikeTime > 0f && before <= strike) strikeDirection = context.Facing.normalized;
            Vector2 targetFacing = recoveryTime > 0f ? context.Input.Direction : requestedFacing;
            stepMotion = new CharacterActionMotion
            {
                OpeningStep = openingStep,
                StartPhase = before / duration, EndPhase = elapsed / duration, Duration = duration,
                Facing = targetFacing, ControlsFacing = true,
                MaxTurn = startupTime * InputMath.NonNegative(settings.StartupTurnSpeed, 180f) +
                    recoveryTime * meanRecovery * Mathf.Max(0f, context.Settings.MoveTurnSpeed),
                Braking = startupTime > 0f ? Mathf.Max(0.01f, InputMath.NonNegative(settings.Braking, 28f)) : 0f,
                RecoversMovement = recoveryTime > 0f
            };
            float stride = openingStep ? openingDistance.GetValueOrDefault() * 2f * startupTime / strike * (1f - meanStartup) : 0f;
            float travel = InputMath.NonNegative(settings.StrikeTravel, 0f) * strikeTime / (recovery - strike);
            return openingDirection * stride + strikeDirection * travel;
        }
    }
}
