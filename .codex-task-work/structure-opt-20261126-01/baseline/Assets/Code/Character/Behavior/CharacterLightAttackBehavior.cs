using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>保存单招的作者参数，并可将片段状态和值捕获到预测副本。</summary>
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
        [InspectorName("目标对齐")] public CharacterActionAlignmentSettings Alignment;
        private bool captured, hasClip;
        private float clipDuration;
        internal bool HasMotionClip => captured ? hasClip : (bool)Clip;
        internal float ClipDuration => captured ? clipDuration : Clip.isLooping ? 0f : Clip.length;

        // 在主线程读取片段时长，预测副本只保留片段状态和参数值。
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

    /// <summary>保存攻击的起手参数和各招式的配置。</summary>
    [Serializable]
    internal sealed class CharacterLightAttackSettings
    {
        [Header("准入与打断")]
        [InspectorName("允许短突进打断的阶段")] public CharacterBehaviorPhase DashInterruptPhases =
            CharacterBehaviorPhase.Startup | CharacterBehaviorPhase.Recovery;
        [Header("执行参数")]
        [Min(0f), InspectorName("移动起手小步（米）")] public float OpeningStepDistance;
        [SerializeField, InspectorName("招式参数")] internal CharacterAttackComboSettings Moves = new CharacterAttackComboSettings();

        // 按有效片段的原速时长或允许的无片段时长解析招式。
        internal float DurationFor(CharacterAttackMove move, CharacterAttackMoveSettings definition)
        {
            if (definition == null || !definition.HasValidPhases) return 0f;
            if (definition.HasMotionClip) return definition.ClipDuration;
            return AllowWithoutClip
                ? InputMath.NonNegative(move == CharacterAttackMove.Heavy ? HeavySeconds : LightSeconds, 0f) : 0f;
        }

        // 捕获本次预测要读取的招式参数，独立复制追加招式数组。
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

    /// <summary>按招式编号保存追加招式的参数。</summary>
    [Serializable]
    internal struct CharacterAttackMoveEntry
    {
        [InspectorName("招式")] public CharacterAttackMove Id;
        [InspectorName("参数")] public CharacterAttackMoveSettings Parameters;
    }

    /// <summary>维护现有内置和追加招式的配置查找。</summary>
    [Serializable]
    internal sealed class CharacterAttackComboSettings
    {
        [InspectorName("轻击一")] public CharacterAttackMoveSettings LightOne = new CharacterAttackMoveSettings();
        [InspectorName("轻击二")] public CharacterAttackMoveSettings LightTwo = new CharacterAttackMoveSettings();
        [InspectorName("重击")] public CharacterAttackMoveSettings Heavy = new CharacterAttackMoveSettings();

        [InspectorName("追加招式")] public CharacterAttackMoveEntry[] Additional;
        internal bool HasClip => LightOne?.HasMotionClip == true || LightTwo?.HasMotionClip == true ||
            Heavy?.HasMotionClip == true || Additional != null && Array.Exists(Additional, entry => entry.Parameters?.HasMotionClip == true);

        // 按招式编号查找单招参数。
        internal CharacterAttackMoveSettings Move(CharacterAttackMove move) => move switch
        {
            CharacterAttackMove.LightOne => LightOne,
            CharacterAttackMove.LightTwo => LightTwo,
            CharacterAttackMove.Heavy => Heavy,
            CharacterAttackMove.None => null,
            _ => AdditionalMove(move)
        };

        // 沿用追加列表顺序解析首个匹配的招式参数。
        private CharacterAttackMoveSettings AdditionalMove(CharacterAttackMove move)
        {
            if (Additional != null)
                foreach (var entry in Additional)
                    if (entry.Id == move) return entry.Parameters;
            return null;
        }
    }
}
