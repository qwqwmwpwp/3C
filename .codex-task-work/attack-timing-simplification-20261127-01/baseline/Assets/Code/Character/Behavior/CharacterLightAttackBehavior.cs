using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 配置共用前摇、出招、收招实现的一招及其运动和对齐规则。
    [Serializable]
    internal sealed class CharacterAttackMoveSettings : CharacterAttackDefinition
    {
        [InspectorName("攻击片段（原速时钟）")] public AnimationClip Clip;
        [Min(0f), InspectorName("无片段时长（秒，0 禁用）")] public float UnclippedDuration;
        [Range(0f, 1f), InspectorName("出招开始相位")] public float StrikeStart = 0.24f;
        [Range(0f, 1f), InspectorName("收招开始相位")] public float RecoveryStart = 0.58f;
        [Min(0f), InspectorName("独立播放时收招转移动淡入（秒）")] public float RecoveryMoveBlendSeconds = 0.16f;
        [Min(0f), InspectorName("起手转速（度/秒）")] public float StartupTurnSpeed = 180f;
        [Min(0.01f), InspectorName("起手制动（米/秒²）")] public float Braking = 28f;
        [Min(0f), InspectorName("出招前移距离（米）")] public float StrikeTravel;
        [InspectorName("目标对齐")] public CharacterActionAlignmentSettings Alignment;
        private bool captured, hasClip;
        private float capturedDuration;
        internal bool HasMotionClip => captured ? hasClip : (bool)Clip;
        internal float Seconds => captured ? capturedDuration : Clip ? (Clip.isLooping ? 0f : Clip.length) : UnclippedDuration;
        private bool HasValidPhases => StrikeStart > 0f && RecoveryStart > StrikeStart && RecoveryStart < 1f;
        internal override bool IsUsable => HasValidPhases && Seconds > 0f && !float.IsInfinity(Seconds);

        // 受理招式时只锁定动作参数，后续输入仍从本流程的路线表读取。
        internal override CharacterAttackDefinition Snapshot() => Capture(false);

        // 冻结片段时长与运动参数；整表预测另外独立复制本招路线。
        internal CharacterAttackMoveSettings Capture(bool includeLinks)
        {
            var copy = (CharacterAttackMoveSettings)MemberwiseClone();
            copy.hasClip = HasMotionClip;
            copy.capturedDuration = Seconds;
            copy.Clip = null;
            copy.Links = includeLinks ? (CharacterAttackLink[])Links?.Clone() : null;
            copy.captured = true;
            return copy;
        }

        // 使用已冻结的单招参数创建独立普通招式实例。
        internal override CharacterAttackExecution CreateExecution() => new CharacterOrdinaryAttack(this);

        // 为直接播放同一动作键提供普通招式的默认交接阶段。
        internal override CharacterActionHandoffSettings StandaloneHandoff(bool startAtZero) => HasValidPhases
            ? new CharacterActionHandoffSettings { FullFrom = StrikeStart, ReleaseFrom = RecoveryStart,
                MoveBlendSeconds = RecoveryMoveBlendSeconds, StartAtZero = startAtZero } : null;
    }

    // 执行普通招式自身的三段运动、阶段打断和收招交接。
    internal sealed class CharacterOrdinaryAttack : CharacterAttackExecution
    {
        private CharacterAttackMoveSettings definition; // 本实例独占的冻结参数。
        internal CharacterAttackMotion Motion;
        internal readonly float Strike, Recovery;
        private float? recoveryMoveStart;
        internal override float? RecoveryMoveStart => recoveryMoveStart;
        internal override float Duration => definition.Seconds;
        internal bool HasMotion => definition.HasMotionClip;
        internal CharacterActionAlignmentSettings Alignment => definition.Alignment;
        internal override bool IsComplete => Elapsed >= Duration;
        internal override double NextBoundary => Elapsed < Strike ? Strike : Elapsed < Recovery ? Recovery :
            Elapsed < Duration ? Duration : double.PositiveInfinity;
        internal override double NextDecision => IsComplete ? double.PositiveInfinity : Duration;
        internal override CharacterBehaviorPhase Phase => Elapsed < Strike ? CharacterBehaviorPhase.Startup :
            Elapsed < Recovery ? CharacterBehaviorPhase.Active : CharacterBehaviorPhase.Recovery;
        internal override bool ReturnsToMovement => Phase == CharacterBehaviorPhase.Recovery;
        internal override bool AllowsOrdinaryMotion => !HasMotion || Elapsed >= Recovery || Elapsed < Strike && !Motion.HasOpening;

        // 从冻结定义计算本实例的两个阶段边界。
        internal CharacterOrdinaryAttack(CharacterAttackMoveSettings definition)
        {
            this.definition = definition;
            Strike = definition.StrikeStart * Duration;
            Recovery = definition.RecoveryStart * Duration;
        }

        // 进入时锁定当前朝向、目标与首招小步。
        internal override void Enter(CharacterBehaviorContext context, bool movingOpening, float? openingDistance)
        {
            if (HasMotion) Motion = new CharacterAttackMotion(definition, context, Duration, Strike, Recovery, openingDistance, movingOpening);
        }

        // 积分普通招式在当前区间内的运动及应归还移动的时间。
        protected override CharacterBehaviorMotion AdvanceInterval(CharacterBehaviorContext context, double before, double end)
        {
            if (!HasMotion) return new CharacterBehaviorMotion(Vector2.zero, (float)(end - before), controlsDisplacement: false);
            var displacement = Motion.Advance(context, before, end, out var motion, out float movementSeconds);
            return new CharacterBehaviorMotion(displacement, movementSeconds, Phase == CharacterBehaviorPhase.Active || motion.OpeningStep, motion);
        }

        // 冻结本招的纯值运动与对齐窗口，供后续物理步使用。
        internal override CharacterFrozenMotion Freeze(long instance, in CharacterMotionCapture capture) =>
            new CharacterFrozenAttackMotion(this, instance, in capture);

        // 按值复制时钟与运动，再隔离普通招式的参数对象。
        internal override CharacterAttackExecution CopyForPrediction()
        {
            var copy = (CharacterOrdinaryAttack)MemberwiseClone();
            copy.definition = (CharacterAttackMoveSettings)definition.Snapshot();
            return copy;
        }

        // 收招首次获得移动许可时锁定交接起点。
        internal override void RecordMovement(bool approved, float startProgress)
        {
            if (approved && Phase == CharacterBehaviorPhase.Recovery && !recoveryMoveStart.HasValue)
                recoveryMoveStart = Mathf.Max(startProgress, Recovery / Duration);
        }
    }
}
