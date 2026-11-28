#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using ThreeC.Player;

namespace ThreeC.CharacterBehavior
{
    // 检查状态总表、可执行的状态参数及其与角色动画配置的引用关系。
    internal static class CharacterComboValidation
    {
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
                if (checkExecution && !definition.IsUsable) report($"状态 {move} 的三段时长或技能冷却无效。");
                if (checkExecution && definition is CharacterAttackMoveSettings moveSettings &&
                    (!moveSettings.Clip || moveSettings.Clip.isLooping || !(moveSettings.Clip.length > 0f)))
                    report($"状态 {move} 需要非循环且非空的攻击片段。");
                if (definition is CharacterAttackMoveSettings moveTiming && !moveTiming.HasValidAnimationDuration)
                    report($"状态 {move} 的动画持续时间必须是大于零的有限秒数。");
                CheckLinks(table, definition.Links, definition.Name, false, report);
            }
            CheckLinks(table, table.Openers, "起手", true, report);
        }

        // 逐条检查目标、触发方式、首招条件、窗口与同键遮蔽。
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
