#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using ThreeC.Player;

namespace ThreeC.CharacterBehavior
{
    internal static class CharacterComboValidation
    {
        internal static void Check(CharacterLightAttackSettings settings, CharacterComboRoutes routes,
            CharacterAnimationConfiguration animation, Action<string> report)
        {
            var ids = new HashSet<CharacterAttackMove> {
                CharacterAttackMove.None, CharacterAttackMove.LightOne, CharacterAttackMove.LightTwo, CharacterAttackMove.Heavy
            };
            if (settings?.Moves?.Additional != null)
                foreach (var row in settings.Moves.Additional)
                    if (!ids.Add(row.Id)) report($"追加招式参数 {row.Id} 占用了保留编号或与已有参数重复。");

            CheckRoutes(routes, report, Usable);
            if (animation?.BehaviorAnimations == null) return;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in animation.Actions ?? Array.Empty<CharacterAnimationActionSettings>())
                if (action != null) keys.Add(action.Key);
            for (int i = 0; i < animation.BehaviorAnimations.Length; i++)
            {
                var row = animation.BehaviorAnimations[i];
                if (row == null || row.BehaviorId != CharacterBehaviorId.Attack) continue;
                if (!row.HasValidTrigger)
                    report($"第 {i + 1} 条攻击动画映射 {row.AttackMove} 缺少有效的触发动作键。");
                if (row.Trigger == CharacterAnimationTrigger.OnStart && row.AttackMove != CharacterAttackMove.None && !Usable(row.AttackMove))
                    report($"第 {i + 1} 条攻击动画映射 {row.AttackMove} 缺少可执行的招式参数。");
                CheckKey(i, row.AttackMove, row.EntryAction);
                CheckKey(i, row.AttackMove, row.HoldAction);
            }

            void CheckKey(int index, CharacterAttackMove move, string key)
            {
                if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key))
                    report($"第 {index + 1} 条攻击动画映射 {move} 缺少动作定义 {key}。");
            }

            bool Usable(CharacterAttackMove move) => settings != null && settings.DurationFor(move, settings.Moves?.Move(move)) > 0f;
        }

        internal static void CheckRoutes(CharacterComboRoutes routes, Action<string> report,
            Func<CharacterAttackMove, bool> usable = null)
        {
            if (routes == null) return;
            CheckLinks("起手", routes.Openers);
            var ids = new HashSet<CharacterAttackMove> { CharacterAttackMove.None };
            foreach (var id in new[] { CharacterAttackMove.LightOne, CharacterAttackMove.LightTwo,
                CharacterAttackMove.Heavy, CharacterAttackMove.SkillTwo })
            {
                ids.Add(id);
                CheckSource(id, routes.ForMove(id));
            }
            if (routes.Additional == null) return;
            foreach (var row in routes.Additional)
            {
                if (!ids.Add(row.Id)) report($"追加路线 {row.Id} 占用了保留编号或与已有路线重复。");
                if (row.Routes == null) report($"追加路线 {row.Id} 缺少路线配置。");
                CheckSource(row.Id, row.Routes);
            }

            void CheckSource(CharacterAttackMove source, CharacterComboMoveRoutes row)
            {
                if (row?.Links == null || row.Links.Length == 0) return;
                if (usable != null && !usable(source)) report($"路线来源 {source} 缺少可执行参数。");
                if (!row.HasValidWindow) report($"{source}：请填写有效接续时间，提前秒数须非负，开放比例须小于等于关闭比例，关闭比例须大于零。");
                CheckLinks(source.ToString(), row.Links);
            }

            void CheckLinks(string source, CharacterAttackLink[] links)
            {
                if (links == null) return;
                for (int i = 0; i < links.Length; i++)
                {
                    var link = links[i];
                    if (link.Button == AttackButton.None || link.To == CharacterAttackMove.None)
                        report($"{source} 第 {i + 1} 条路线缺少按键或下一招。");
                    else if (usable != null && !usable(link.To))
                        report($"{source} 第 {i + 1} 条路线的目标 {link.To} 缺少可执行参数。");
                    if (!link.HasValidBlend) report($"{source} 第 {i + 1} 条路线的缓入结束比例须小于等于缓出开始比例。");
                    for (int j = 0; j < i; j++)
                    {
                        var earlier = links[j];
                        if (earlier.Button != link.Button || !(earlier.OpeningCondition == link.OpeningCondition ||
                            earlier.OpeningCondition == CharacterComboOpeningCondition.Any ||
                            link.OpeningCondition == CharacterComboOpeningCondition.Any)) continue;
                        report($"{source} 第 {j + 1}、{i + 1} 条路线条件重叠，优先采用首条匹配。");
                        break;
                    }
                }
            }
        }
    }
}
#endif
