using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 定义一种攻击状态的作者数据、准入条件及其独占执行实例的工厂。
    [Serializable]
    internal abstract class CharacterAttackDefinition
    {
        [InspectorName("攻击片段")] public AnimationClip Clip;
        [InspectorName("显示名称")] public string Name;
        [InspectorName("后续路线"), Tooltip("同类候选按列表顺序选择。输入按路线自己的窗口受理。")] public CharacterAttackLink[] Links = Array.Empty<CharacterAttackLink>();
        internal abstract bool IsUsable { get; }

        // 捕获一次执行需要的独立参数，预测副本只读取普通值，路线由所属配置表保存。
        internal abstract CharacterAttackDefinition Snapshot();

        // 创建尚未进入的全新实例，每次起手、接续或预测都拥有自己的可变状态。
        internal abstract CharacterAttackExecution CreateExecution();

        // 提供当前动作定义的表现时钟与权重边界。
        internal abstract CharacterActionHandoff AttackTiming { get; }

        // 普通攻击无需技能许可，特定技能可依据角色的就绪时间拒绝请求。
        internal virtual CharacterBehaviorRejectReason CheckEntry(CharacterBehaviorContext context, double skillReadyAt) => default;

        // 通知定义本次进入已成功，需要冷却的技能可更新该角色的就绪时间。
        internal virtual void OnEntered(double time, ref double readyAt) { }
    }

    // 维护一个招式实例的时钟与生命周期，具体状态决定阶段、边界和切换意图。
    internal abstract class CharacterAttackExecution
    {
        internal double Elapsed { get; private set; }
        internal abstract CharacterActionHandoff Timing { get; }
        internal abstract float Duration { get; } // 正有限值声明线性进度 Elapsed / Duration；不定时或非线性状态填零并覆盖 Progress。
        internal virtual float Progress => Duration > 0f ? Mathf.Clamp01((float)(Elapsed / Duration)) : 0f;
        internal abstract CharacterBehaviorPhase Phase { get; } // 向外部打断规则报告的阶段；内部流程由状态自行定义。
        internal abstract bool IsComplete { get; }
        internal abstract double NextBoundary { get; } // 下一个绝对招式时刻；自定义进度应报告其阶段或进度变化的裁决边界，无已知边界时为正无穷。
        internal virtual double NextDecision => NextBoundary; // 整步推进时必须停下来检查状态结果的时刻。
        internal virtual int Result => 0;
        internal virtual int RequestedTarget => 0; // 只声明意图；目标准入及退出交接由攻击行为统一处理。

        // 仅以持续输入进入已获准的状态，不重发起手或接续按键；预测上下文禁止真实副作用。
        internal virtual void Enter(CharacterBehaviorContext context) { }

        // 向状态交付本次终止原因及是否为预测，释放本实例持有的专属运行数据。
        internal virtual void Exit(CharacterBehaviorEndReason reason, bool isPrediction) { }

        // 更新本实例的角色时钟，终点已经过阶段边界校正。
        internal void Advance(CharacterBehaviorContext context, double end)
        {
            double start = Elapsed;
            Elapsed = end;
            OnAdvance(context, start, end);
        }

        // 处理状态专属的时间或输入事件，普通招式只需推进时钟。
        protected virtual void OnAdvance(CharacterBehaviorContext context, double start, double end) { }

        // 深复制可变字段到独立预测实例；不得重放 Enter 或共享活动状态。
        internal abstract CharacterAttackExecution CopyForPrediction();

    }
}
