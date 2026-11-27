using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 定义一种攻击状态的作者数据、准入条件及其独占执行实例的工厂。
    [Serializable]
    internal abstract class CharacterAttackDefinition
    {
        [InspectorName("显示名称")] public string Name;
        [InspectorName("后续路线"), Tooltip("同类候选按列表顺序选择。输入按路线自己的窗口受理。")] public CharacterAttackLink[] Links = Array.Empty<CharacterAttackLink>();
        [Min(0f), InspectorName("状态冷却（秒）")] public float CooldownSeconds;
        internal abstract bool IsUsable { get; }

        // 捕获一次执行需要的独立参数，预测副本只读取普通值，路线由所属配置表保存。
        internal abstract CharacterAttackDefinition Snapshot();

        // 创建尚未进入的全新实例，每次起手、接续或预测都拥有自己的可变状态。
        internal abstract CharacterAttackExecution CreateExecution();

        // 在预输入与实际交接时按角色时刻和持续输入检查专属条件，按键边沿由来源状态和路线处理。
        internal virtual bool CanEnter(CharacterBehaviorContext context) => true;

        // 独立调用动画动作键时可选用本定义提供的交接参数。
        internal virtual CharacterActionHandoffSettings StandaloneHandoff(bool startAtZero) => null;
    }

    // 维护一个招式实例的时钟与生命周期，具体状态决定阶段、边界、运动和切换意图。
    internal abstract class CharacterAttackExecution
    {
        internal double Elapsed { get; private set; }
        internal abstract float Duration { get; } // 正有限值声明线性进度 Elapsed / Duration；不定时或非线性状态填零并覆盖 Progress。
        internal virtual float Progress => Duration > 0f ? Mathf.Clamp01((float)(Elapsed / Duration)) : 0f;
        internal abstract CharacterBehaviorPhase Phase { get; } // 向外部打断规则报告的阶段；内部流程由状态自行定义。
        internal abstract bool IsComplete { get; }
        internal abstract double NextBoundary { get; } // 下一个绝对招式时刻；自定义进度应报告其阶段或进度变化的裁决边界，无已知边界时为正无穷。
        internal virtual double NextDecision => NextBoundary; // 整步推进时必须停下来检查状态结果的时刻。
        internal abstract bool AllowsOrdinaryMotion { get; }
        internal virtual bool ReturnsToMovement => AllowsOrdinaryMotion; // 动画与预测用此语义查询移动交接。
        internal virtual int Result => 0;
        internal virtual int RequestedTarget => 0; // 只声明意图；目标准入及退出交接由攻击行为统一处理。
        internal virtual float? RecoveryMoveStart => null;

        // 仅以持续输入进入已获准的状态，不重发起手或接续按键；预测上下文禁止真实副作用。
        internal abstract void Enter(CharacterBehaviorContext context, bool movingOpening, float? openingDistance);

        // 向状态交付本次终止原因及是否为预测，释放本实例持有的专属运行数据。
        internal virtual void Exit(CharacterBehaviorEndReason reason, bool isPrediction) { }

        // 按状态时钟推进一个区间；零长区间可读取本次输入或刷新专属状态结果。
        internal CharacterBehaviorMotion Advance(CharacterBehaviorContext context, double end)
        {
            double before = Elapsed;
            Elapsed = end;
            return AdvanceInterval(context, before, end);
        }

        // 计算当前区间的阶段、运动和结果，行为层统一发布运动输出。
        protected abstract CharacterBehaviorMotion AdvanceInterval(CharacterBehaviorContext context, double before, double end);

        // 创建可重复采样的独立运动值，禁止捕获当前实例的可变引用。
        internal abstract CharacterFrozenMotion Freeze(long instance, in CharacterMotionCapture capture);

        // 深复制可变字段到独立预测实例；不得重放 Enter 或共享活动状态。
        internal abstract CharacterAttackExecution CopyForPrediction();

        // 在普通移动实际获准时记录状态自己的交接时点。
        internal virtual void RecordMovement(bool approved, float startProgress) { }
    }
}
