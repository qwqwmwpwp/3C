using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 在编辑器核对游戏招式表、角色动作映射和实际播放资源。
    internal static class CharacterComboAnimationValidation
    {
        // 在主线程核对片段、逻辑定义与实际装配的动画动作键。
        internal static void Check(CharacterAttackTable table, CharacterAnimationConfiguration animation, Action<string> report,
            CharacterBehaviorAnimationProfile profile = null)
        {
            CharacterComboValidation.CheckTable(table, true, report);
            if (animation == null || !profile) return;
            var actions = new Dictionary<string, CharacterAnimationActionSettings>(StringComparer.Ordinal);
            foreach (var action in animation.Actions ?? Array.Empty<CharacterAnimationActionSettings>())
                if (!string.IsNullOrWhiteSpace(action?.Key)) actions[action.Key] = action;
            for (int i = 0; i < profile.Mappings.Length; i++)
            {
                var row = profile.Mappings[i];
                if (row == null || row.BehaviorId != CharacterBehaviorId.Attack) continue;
                if (!row.HasValidTrigger) report($"第 {i + 1} 条攻击动画映射缺少有效的触发动作键。");
                if (row.AttackStateId != 0 && table?.Find(row.AttackStateId) == null)
                    report($"第 {i + 1} 条攻击动画映射引用了缺失的状态 {row.AttackStateId}。");
                if (row.IsAttackState && table?.Find(row.AttackStateId) is CharacterAttackMoveSettings attack &&
                    !string.IsNullOrWhiteSpace(row.EntryAction) && actions.TryGetValue(row.EntryAction, out var entry))
                    CheckMappedClip(attack, entry, animation.PlaybackLibrary, report);
                foreach (string key in new[] { row.EntryAction, row.HoldAction })
                    if (!string.IsNullOrWhiteSpace(key) && !actions.ContainsKey(key))
                        report($"第 {i + 1} 条攻击动画映射引用了缺失的动作 {key}。");
            }
        }

        // 核对攻击的进入动作键在当前播放库实际使用同一个非循环片段。
        private static void CheckMappedClip(CharacterAttackMoveSettings attack, CharacterAnimationActionSettings action,
            CharacterMotionDatabase library, Action<string> report)
        {
            var states = library ? library.Playback?.States : null;
            var state = Array.Find(states ?? Array.Empty<CharacterClipState>(), item => item != null && item.Path == action.StateName);
            if (state == null || !state.Clip || state.Clip != attack.Clip)
                report($"{attack.Name} 的动作 {action.Key} 与播放库状态 {action.StateName} 的片段不一致。");
            else if (state.Blend.Second || state.Clip.isLooping || !(state.Clip.length > 0f) || !(state.Speed > 0f))
                report($"{attack.Name} 的动作 {action.Key} 需要可正向播放的非循环单片段。");
        }

    }
}
