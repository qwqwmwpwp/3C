using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace ThreeC.CharacterBehavior
{
    internal sealed class CharacterBehaviorChoiceAttribute : PropertyAttribute { }

    internal enum CharacterAnimationTrigger
    {
        [InspectorName("许可期间持续")] WhileGranted,
        [InspectorName("获准时播放一次")] OnStart
    }

    internal enum CharacterGrantedEntryRecovery
    {
        [InspectorName("接入持续姿势")] ContinueToHold,
        [InspectorName("重新播放进入动作")] ReplayEntry
    }

    // 绑定行为许可或攻击状态编号与表现动作、权重及交接设置。
    [Serializable]
    internal sealed class CharacterBehaviorAnimationMapping
    {
        // 保留资产中的 Behavior 数值及完整位宽；Unity 不能序列化以 ulong 为底层类型的枚举。
        [SerializeField, CharacterBehaviorChoice, InspectorName("许可行为"), Tooltip("选择一个行为。普通移动按移动请求获准判断。")]
        private ulong Behavior = (ulong)CharacterBehaviorId.Guard;
        // 冻结本条映射的触发条件与非攻击动作需要的交接参数。
        internal CharacterBehaviorAnimationMapping Snapshot()
        {
            var copy = (CharacterBehaviorAnimationMapping)MemberwiseClone();
            copy.Handoff = IsAttackState ? null : Handoff?.Snapshot();
            return copy;
        }
        public CharacterBehaviorId BehaviorId => (CharacterBehaviorId)Behavior;
        internal bool IsAttackState => BehaviorId == CharacterBehaviorId.Attack && Trigger == CharacterAnimationTrigger.OnStart && AttackStateId != 0;
        internal bool HasValidTrigger => Behavior != 0 && (Behavior & (Behavior - 1)) == 0 &&
            (!string.IsNullOrWhiteSpace(EntryAction) ||
             Trigger != CharacterAnimationTrigger.OnStart && !string.IsNullOrWhiteSpace(HoldAction));

        // 仅按非攻击动作的配置判断是否在进入阶段保留原步态。
        internal bool PreservesEntryGait(in CharacterControllerFrameResult frame, CharacterActionHandoffSettings settings)
        {
            var step = frame.Behavior;
            if (Trigger != CharacterAnimationTrigger.OnStart || settings?.FollowSpeedOnEntry == true ||
                (step.ActiveBehaviors & BehaviorId) == 0) return false;
            if (IsAttackState || settings?.OverridePhases != true) return false;
            float phase = step.DisplacementProvider == BehaviorId && step.ActionMotion.StartPhase.HasValue
                ? step.ActionMotion.EndPhase : (step.GrantAgeAtStepStart(BehaviorId) + frame.DeltaTime) / settings.Duration;
            return phase < settings.FullFrom;
        }

        [InspectorName("触发方式")] public CharacterAnimationTrigger Trigger;
        [FormerlySerializedAs("AttackMove")]
        [InspectorName("单次攻击状态编号"), Tooltip("用于攻击行为的单次映射，0 表示跟随整次行为许可。")]
        public int AttackStateId = 1;
        [InspectorName("进入动作键"), Tooltip("单次模式必填；持续模式下可选，播放完成后转入持续姿势。")]
        public string EntryAction;
        [InspectorName("持续动作键")] public string HoldAction;
        [InspectorName("进入动作失去部位后的恢复")] public CharacterGrantedEntryRecovery EntryRecovery;
        [InspectorName("局部层权重"), Range(0f, 1f)] public float Weight = 1f;
        [InspectorName("单次动作交接")] public CharacterActionHandoffSettings Handoff = new CharacterActionHandoffSettings();
    }
}
