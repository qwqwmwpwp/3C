exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterLightAttackBehavior')
s='''using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 配置招式使用的动画片段以及进入、主体和后摇三段角色时间。
    [Serializable]
    internal class CharacterAttackMoveSettings : CharacterAttackDefinition
    {
        [InspectorName("攻击片段")] public AnimationClip Clip;
        [Min(0f), InspectorName("进入缓入（秒）")] public float EntryBlendSeconds = 0.2f;
        [Min(0f), InspectorName("主体持续（秒）")] public float ActiveSeconds = 0.4f;
        [Min(0f), InspectorName("后摇淡出（秒）")] public float RecoverySeconds = 0.4f;
        internal float Duration => EntryBlendSeconds + ActiveSeconds + RecoverySeconds;
        internal override bool IsUsable => EntryBlendSeconds >= 0f && ActiveSeconds > 0f && RecoverySeconds >= 0f &&
            !float.IsInfinity(Duration) && !float.IsNaN(Duration);

        // 冻结一次招式的纯值参数，片段和后续路线留在作者配置中。
        internal override CharacterAttackDefinition Snapshot()
        {
            var copy = (CharacterAttackMoveSettings)MemberwiseClone();
            copy.Clip = null;
            copy.Links = null;
            return copy;
        }

        // 生成一个从零开始的三段执行时钟。
        internal override CharacterAttackExecution CreateExecution() => new CharacterOrdinaryAttack(this);

        // 为行为播放和独立播放提供同一组阶段边界与总时长。
        internal override CharacterActionHandoff AttackTiming => IsUsable
            ? CharacterActionHandoff.ForAttack(Duration, EntryBlendSeconds / Duration,
                (EntryBlendSeconds + ActiveSeconds) / Duration) : default;
    }

    // 维护技能二特有的冷却参数及其按角色时钟计算的准入规则。
    [Serializable]
    internal sealed class CharacterSkillAttackSettings : CharacterAttackMoveSettings
    {
        [Min(0f), InspectorName("技能冷却（秒）")] public float CooldownSeconds = 3f;
        internal override bool IsUsable => base.IsUsable && CooldownSeconds >= 0f && !float.IsInfinity(CooldownSeconds);

        // 在预计接续时刻和实际进入前核对角色自己的技能冷却。
        internal override CharacterBehaviorRejectReason CheckSkill(double time, double readyAt) =>
            readyAt > time + CharacterAttackBehavior.Tolerance ? CharacterBehaviorRejectReason.Cooldown : default;

        // 成功进入时将下一次可用时刻写回该角色的运行状态。
        internal override void OnEntered(double time, ref double readyAt) => readyAt = time + CooldownSeconds;
    }

    // 只推进本招的三段时钟，移动与转向始终交给普通移动行为。
    internal sealed class CharacterOrdinaryAttack : CharacterAttackExecution
    {
        private readonly float entryEnd, activeEnd;
        internal override float Duration { get; }
        internal override CharacterActionHandoff Timing { get; }
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
            Timing = settings.AttackTiming;
        }

        // 复制纯值时钟，预测实例的时间独立推进。
        internal override CharacterAttackExecution CopyForPrediction() => (CharacterOrdinaryAttack)MemberwiseClone();
    }
}
'''
write()
use('CharacterAttackDefinition')
lines('public float CooldownSeconds;', 'internal abstract bool AllowsOrdinaryMotion','internal virtual bool ReturnsToMovement','internal virtual float? RecoveryMoveStart')
cut('        // 独立调用动画','    }','''        // 提供当前动作定义的表现时钟与权重边界。
        internal abstract CharacterActionHandoff AttackTiming { get; }

        // 普通攻击无需技能许可，特定技能可依据角色的就绪时间拒绝请求。
        internal virtual CharacterBehaviorRejectReason CheckSkill(double time, double readyAt) => default;

        // 通知定义本次进入已成功，需要冷却的技能可更新该角色的就绪时间。
        internal virtual void OnEntered(double time, ref double readyAt) { }
''')
sub('下一边界','下一边界') if False else None
sub('、运动和切换意图','和切换意图')
sub('        internal abstract float Duration','        internal abstract CharacterActionHandoff Timing { get; }\n        internal abstract float Duration')
sub('        internal abstract void Enter(CharacterBehaviorContext context, bool movingOpening, float? openingDistance);','        internal virtual void Enter(CharacterBehaviorContext context) { }')
cut('        // 按状态时钟推进','        // 深复制','''        // 更新本实例的角色时钟，终点已经过阶段边界校正。
        internal void Advance(double end) => Elapsed = end;

''')
cut('        // 在普通移动实际','    }')
write()
use('CharacterComboTable')
lines('public float OpeningStepDistance','public float BlendInEnd','public float BlendOutStart','internal bool HasValidBlend','internal Vector2 BlendRange')
sub('public CharacterAttackMoveSettings SkillTwo = new CharacterAttackMoveSettings','public CharacterSkillAttackSettings SkillTwo = new CharacterSkillAttackSettings')
for m in ('LightOne','LightTwo','Heavy','SkillTwo'):
 sub(m+'?.Capture(true)','Freeze('+m+')')
sub('            return copy;\n        }','''            return copy;
        }

        // 保留具名字段的具体类型，并为该预测表复制可编辑的后续路线。
        private static T Freeze<T>(T source) where T : CharacterAttackMoveSettings
        {
            if (source == null) return null;
            var copy = (T)source.Snapshot();
            copy.Links = (CharacterAttackLink[])source.Links?.Clone();
            return copy;
        }''')
sub(', BlendInEnd = 0.1f, BlendOutStart = 0.9f','');sub('与目标动画权重','');sub('统一目标及默认表现交接','固定的目标编号');write()
use('CharacterAttackLookup');lines('internal float OpeningStepDistance');sub('definition.Name, link.BlendRange, definition.Snapshot()','definition.Name, definition.Snapshot()');write()
use('CharacterAttackState')
lines('public bool ReturnsToMovement','ReturnsToMovement =','readonly Vector2 BlendRange','BlendRange = blendRange;')
sub('Vector2 blend, in CharacterAttackPending','in CharacterAttackPending');sub('new CharacterActionHandoff(Duration, blend, execution.RecoveryMoveStart)','execution.Timing');sub('string name, Vector2 blendRange,','string name,');sub('Id, Name, BlendRange,','Id, Name,');sub('冻结参数及本路线的表现交接','冻结的动作参数');sub('当前转移的交接参数','本状态的动画交接参数');write()
use('CharacterAttackCombo')
sub('CharacterBehavior, ICharacterIntervalMotion','CharacterBehavior');sub('private const float Tolerance','internal const float Tolerance')
sub('private readonly Dictionary<int, double> readyAt = new Dictionary<int, double>();','private double skillReadyAt; // 本角色技能二的下一次可用时刻。')
lines('private Vector2 blend;','override bool SharesMovementOnStart','override bool AllowsOrdinaryMotionNow','blend = target.BlendRange;','copy.blend = blend;','current.RecordMovement(')
sub('=> readyAt;', '=> enteredAtInstant;');sub('startProgress, blend, pending','startProgress, pending')
sub('if (readyAt.TryGetValue(target, out double ready) && ready > context.SimulationTime + Tolerance)\n                return CharacterBehaviorRejectReason.Cooldown;', '''var skill = definition.CheckSkill(context.SimulationTime, skillReadyAt);
            if (skill != default) return skill;''')
sub('CheckAdmission(int target, CharacterAttackDefinition','CheckAdmission(CharacterAttackDefinition');sub('CheckAdmission(pending.Target.Id, pending.Target.Definition,','CheckAdmission(pending.Target.Definition,')
sub('Enter(target, context, true);','Enter(target, context);');sub('Enter(target, context, false);','Enter(target, context);');sub('CharacterBehaviorContext context, bool first)','CharacterBehaviorContext context)')
sub('next.Enter(At(context, 0d, 0f, true), movingOpening, first ? InputMath.NonNegative(table.OpeningStepDistance, 0f) : (float?)null);','next.Enter(At(context, 0d, 0f, true));')
cut('            float cooldown =','        }','            target.Definition.OnEntered(context.SimulationTime, ref skillReadyAt);\n')
cut('        // 冻结状态自己的运动','        // 按列表优先级')
cut('        // 兼容普通位移调用','        // 发布当前状态','''        // 普通行为入口只推进攻击时钟，位移由普通移动行为独立提供。
        public override Vector2 Update(CharacterBehaviorContext context)
        {
            if (current == null) return default;
            moveStarted = firstStep;
            startProgress = current.Progress;
            if (firstStep) context = At(context, 0d, context.DeltaTime, true);
            else ReadLinks(context);
            firstStep = false;
            if (context.SeparateBoundaries && context.DeltaTime > 0f)
            {
                AdvanceSegment(context.DeltaTime, NextEndpoint(current.NextBoundary));
                return default;
            }
            double offset = 0d;
            while (true)
            {
                var endpoint = At(context, offset, 0f, true);
                while (TrySwitch(endpoint)) { }
                double remaining = context.DeltaTime - offset;
                if (!(remaining > 0d) || current.IsComplete) return default;
                double next = NextEndpoint(current.NextDecision);
                double seconds = Math.Min(remaining, next - current.Elapsed);
                if (!(seconds > 0d) || !(offset + seconds > offset))
                    throw new InvalidOperationException($"攻击状态 {id} 在 {current.Elapsed} 秒没有可推进的下一边界。");
                AdvanceSegment(seconds, next);
                offset += seconds;
            }
        }

        // 将浮点步长吸附到实际跨过的阶段端点，避免下一次裁决重复同一边界。
        private void AdvanceSegment(double seconds, double boundary)
        {
            double end = current.Elapsed + seconds;
            if (seconds >= (float)(boundary - current.Elapsed)) end = Math.Max(end, boundary);
            if (boundary == current.Duration) end = Math.Min(end, boundary);
            current.Advance(end);
        }

        // 发布本步攻击实例和开始边沿，移动许可由移动行为独立记录。
        internal override void RecordOutcome(bool moveApproved, ref CharacterBehaviorStep step)
        {
            if (current == null) return;
            ReadOutcome(ref step);
            step.AttackMoveStarted = moveStarted;
        }

''')
sub('openingStep','firstStep');sub('copy.readyAt.Clear();\n            foreach (var pair in readyAt) copy.readyAt.Add(pair.Key, pair.Value);','copy.skillReadyAt = skillReadyAt;');sub('readyAt.Clear();','skillReadyAt = 0d;')
write()
use('CharacterAttackLookup');sub('requester.CheckAdmission(id, definition,','requester.CheckAdmission(definition,');write()
use('CharacterBehaviorConfig');cut('var interrupts = new System.Collections.Generic.List<CharacterBehaviorInterrupt> {','                if (allowGuard','var interrupts = new System.Collections.Generic.List<CharacterBehaviorInterrupt>();\n');sub(', motion: CharacterBehaviorMotionCapability.ExactInterval,',',');write()
use('CharacterComboValidation');lines('if (!link.HasValidBlend)');sub('执行参数或片段时长无效。','三段时长或技能冷却无效。');sub('窗口、表现交接与','窗口与');write()
