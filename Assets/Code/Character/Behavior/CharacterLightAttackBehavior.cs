using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 分别配置片段播放时长和招式进入、主体、后摇的三段角色时间。
    [Serializable]
    internal class CharacterAttackMoveSettings : CharacterAttackDefinition
    {
        [Min(0f), InspectorName("动画持续时间（秒）")] public float AnimationDurationSeconds = 1f;
        internal bool HasValidAnimationDuration => AnimationDurationSeconds > 0f && !float.IsInfinity(AnimationDurationSeconds);
        [Min(0f), InspectorName("进入缓入（秒）")] public float EntryBlendSeconds = 0.2f;
        [Min(0f), InspectorName("主体持续（秒）")] public float ActiveSeconds = 0.4f;
        [Min(0f), InspectorName("后摇淡出（秒）")] public float RecoverySeconds = 0.4f;
        private int frozenClipId;
        internal float Duration => EntryBlendSeconds + ActiveSeconds + RecoverySeconds;
        internal override bool IsUsable => EntryBlendSeconds >= 0f && ActiveSeconds > 0f && RecoverySeconds >= 0f &&
            !float.IsInfinity(Duration) && !float.IsNaN(Duration);

        // 冻结一次招式的纯值参数，片段和后续路线留在作者配置中。
        internal override CharacterAttackDefinition Snapshot()
        {
            var copy = (CharacterAttackMoveSettings)MemberwiseClone();
            copy.frozenClipId = Presentation.ClipId;
            copy.Clip = null;
            copy.Links = null;
            return copy;
        }

        // 生成一个从零开始的三段执行时钟。
        internal override CharacterAttackExecution CreateExecution() => new CharacterOrdinaryAttack(this);

        // 作者资源在主线程捕获身份；执行与预测副本只使用已冻结的整数。
        internal override CharacterAttackPresentation Presentation => new CharacterAttackPresentation(
            ReferenceEquals(Clip, null) ? frozenClipId : Clip ? Clip.GetInstanceID() : 0,
            AnimationDurationSeconds, EntryBlendSeconds, ActiveSeconds, RecoverySeconds);
    }

    // 维护技能二特有的冷却参数及其按角色时钟计算的准入规则。
    [Serializable]
    internal sealed class CharacterSkillAttackSettings : CharacterAttackMoveSettings
    {
        [Min(0f), InspectorName("技能冷却（秒）")] public float CooldownSeconds = 3f;
        internal override bool IsUsable => base.IsUsable && CooldownSeconds >= 0f && !float.IsInfinity(CooldownSeconds);

        // 在预计接续时刻和实际进入前核对角色自己的技能冷却。
        internal override CharacterBehaviorRejectReason CheckEntry(CharacterBehaviorContext context, double skillReadyAt) =>
            skillReadyAt > context.SimulationTime + CharacterAttackBehavior.Tolerance ? CharacterBehaviorRejectReason.Cooldown : default;

        // 成功进入时将下一次可用时刻写回该角色的运行状态。
        internal override void OnEntered(double time, ref double readyAt) => readyAt = time + CooldownSeconds;
    }

    // 只推进本招的三段时钟，移动与转向始终交给普通移动行为。
    internal sealed class CharacterOrdinaryAttack : CharacterAttackExecution
    {
        private readonly float entryEnd, activeEnd;
        internal override float Duration { get; }
        internal override CharacterAttackPresentation Presentation { get; }
        internal override bool IsComplete => Elapsed >= Duration;
        internal override double NextBoundary => Elapsed < entryEnd ? entryEnd : Elapsed < activeEnd ? activeEnd :
            Elapsed < Duration ? Duration : double.PositiveInfinity;
        internal override CharacterBehaviorPhase Phase => Elapsed < entryEnd ? CharacterBehaviorPhase.Startup :
            Elapsed < activeEnd || activeEnd == Duration ? CharacterBehaviorPhase.Active : CharacterBehaviorPhase.Recovery;

        // 固定本实例的阶段边界，零时长的首尾阶段不占用角色时间。
        internal CharacterOrdinaryAttack(CharacterAttackMoveSettings settings)
        {
            entryEnd = settings.EntryBlendSeconds;
            activeEnd = entryEnd + settings.ActiveSeconds;
            Duration = settings.Duration;
            Presentation = settings.Presentation;
        }

        // 复制纯值时钟，预测实例的时间独立推进。
        internal override CharacterAttackExecution CopyForPrediction() => (CharacterOrdinaryAttack)MemberwiseClone();
    }
}
