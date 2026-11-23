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
        internal bool HasValidPhases => StrikeStart > 0f && RecoveryStart > StrikeStart && RecoveryStart < 1f;
    }

    [Serializable]
    internal sealed class CharacterLightAttackSettings : CharacterAttackMoveSettings
    {
        [InspectorName("允许短突进打断的阶段")] public CharacterBehaviorPhase DashInterruptPhases =
            CharacterBehaviorPhase.Startup | CharacterBehaviorPhase.Recovery;
        [InspectorName("连招状态切换")] public CharacterAttackComboSettings Combo = new CharacterAttackComboSettings();
    }

    /// <summary>按当前招式的起手、出招和收招区间，计算本步位移与转向。</summary>
    internal struct CharacterAttackMotion
    {
        private readonly CharacterAttackMoveSettings settings;
        private readonly float duration, strike, recovery;
        private Vector2 requestedFacing, strikeDirection;

        public CharacterAttackMotion(CharacterAttackMoveSettings settings, CharacterBehaviorContext context,
            float duration, float strike, float recovery)
        {
            this.settings = settings;
            this.duration = duration;
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
            movementSeconds = startupTime * (1f - meanStartup) + recoveryTime * meanRecovery;
            if (startupTime > 0f && context.Input.Direction != Vector2.zero)
                requestedFacing = context.Input.Direction.normalized;
            if (strikeTime > 0f && before <= strike) strikeDirection = context.Facing.normalized;
            Vector2 targetFacing = recoveryTime > 0f ? context.Input.Direction : requestedFacing;
            stepMotion = new CharacterActionMotion
            {
                StartPhase = before / duration, EndPhase = elapsed / duration, Duration = duration,
                Facing = targetFacing, ControlsFacing = true,
                MaxTurn = startupTime * InputMath.NonNegative(settings.StartupTurnSpeed, 180f) +
                    recoveryTime * meanRecovery * Mathf.Max(0f, context.Settings.MoveTurnSpeed),
                Braking = startupTime > 0f ? Mathf.Max(0.01f, InputMath.NonNegative(settings.Braking, 28f)) : 0f,
                RecoversMovement = recoveryTime > 0f
            };
            return strikeDirection * (InputMath.NonNegative(settings.StrikeTravel, 0f) * strikeTime / (recovery - strike));
        }
    }
}
