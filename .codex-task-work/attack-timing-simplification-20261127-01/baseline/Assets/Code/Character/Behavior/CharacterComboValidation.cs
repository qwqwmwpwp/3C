#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using ThreeC.Player;

namespace ThreeC.CharacterBehavior
{
    // 检查状态总表、可执行的状态参数及其与角色动画配置的引用关系。
    internal static class CharacterComboValidation
    {
        // 在主线程核对片段、逻辑定义与实际装配的动画动作键。
        internal static void Check(CharacterAttackTable table, CharacterAnimationConfiguration animation, Action<string> report)
        {
            CheckTable(table, true, report);
            if (animation?.BehaviorAnimations == null) return;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in animation.Actions ?? Array.Empty<CharacterAnimationActionSettings>())
                if (action != null) keys.Add(action.Key);
            for (int i = 0; i < animation.BehaviorAnimations.Length; i++)
            {
                var row = animation.BehaviorAnimations[i];
                if (row == null || row.BehaviorId != CharacterBehaviorId.Attack) continue;
                if (!row.HasValidTrigger) report($"第 {i + 1} 条攻击动画映射缺少有效的触发动作键。");
                if (row.AttackStateId != 0 && table?.Find(row.AttackStateId) == null)
                    report($"第 {i + 1} 条攻击动画映射引用了缺失的状态 {row.AttackStateId}。");
                foreach (string key in new[] { row.EntryAction, row.HoldAction })
                    if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key))
                        report($"第 {i + 1} 条攻击动画映射引用了缺失的动作 {key}。");
            }
        }

        // 检查四个固定状态的参数与路线，自动校验时避开只能从主线程读取的动画资产。
        internal static void CheckTable(CharacterAttackTable table, bool checkExecution, Action<string> report)
        {
            if (table == null) { report("缺少攻击状态表。"); return; }
            foreach (CharacterAttackMove move in Enum.GetValues(typeof(CharacterAttackMove)))
            {
                if (move == CharacterAttackMove.None) continue;
                var definition = table.Find((int)move);
                if (definition == null) { report($"状态 {move} 缺少配置。"); continue; }
                if (string.IsNullOrWhiteSpace(definition.Name)) report($"状态 {move} 缺少显示名称。");
                if (checkExecution && !definition.IsUsable) report($"状态 {move} 的执行参数或片段时长无效。");
                CheckLinks(table, definition.Links, definition.Name, false, report);
            }
            CheckLinks(table, table.Openers, "起手", true, report);
        }

        // 逐条检查目标、触发方式、首招条件、窗口、表现交接与同键遮蔽。
        private static void CheckLinks(CharacterAttackTable table, CharacterAttackLink[] links, string source,
            bool opener, Action<string> report)
        {
            if (links == null) return;
            for (int i = 0; i < links.Length; i++)
            {
                var link = links[i];
                string prefix = $"{source} 第 {i + 1} 条路线";
                if (table.Find(link.TargetId) == null) report($"{prefix} 的目标 {link.To} 没有对应的状态配置。");
                if (!Enum.IsDefined(typeof(CharacterComboOpeningCondition), link.OpeningCondition)) report($"{prefix} 的首招条件无效。");
                if (!link.HasValidBlend) report($"{prefix} 的缓入结束比例须小于等于缓出开始比例，并位于 0～1。");
                if (!Enum.IsDefined(typeof(CharacterAttackTrigger), link.Trigger) || opener && link.Trigger != CharacterAttackTrigger.Input)
                    report($"{prefix} 的触发方式无效。");
                if (link.Trigger == CharacterAttackTrigger.Time && !link.HasValidTime) report($"{prefix} 的触发进度无效。");
                if (link.Trigger == CharacterAttackTrigger.Result && link.Result == 0) report($"{prefix} 缺少状态结果编号。");
                if (link.Trigger != CharacterAttackTrigger.Input) continue;
                if (link.Button == AttackButton.None || !Enum.IsDefined(typeof(AttackButton), link.Button)) report($"{prefix} 缺少有效按键。");
                if (!opener && !link.HasValidWindow) report($"{prefix} 的输入窗口无效。");
                for (int j = 0; j < i; j++)
                {
                    var earlier = links[j];
                    if (!opener && (earlier.LinkStart != link.LinkStart || earlier.LinkEnd != link.LinkEnd || earlier.EarlySeconds != link.EarlySeconds)) continue;
                    if (earlier.Trigger != link.Trigger || earlier.Button != link.Button ||
                        earlier.OpeningCondition != link.OpeningCondition && earlier.OpeningCondition != CharacterComboOpeningCondition.Any &&
                        link.OpeningCondition != CharacterComboOpeningCondition.Any) continue;
                    report($"{prefix} 与第 {j + 1} 条按键条件重叠，优先采用首条可准入路线。");
                    break;
                }
            }
        }
    }
}
#endif
