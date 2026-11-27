using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在主线程装配时保存动作及映射内容，使资源原地编辑后仍能识别受影响的实例。
    internal sealed class AnimationBindingDefinitions
    {
        internal readonly Dictionary<string, (string action, string handoff)> Actions =
            new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        internal readonly Dictionary<CharacterBehaviorAnimationMapping, string> Mappings =
            new Dictionary<CharacterBehaviorAnimationMapping, string>();

        // 固定序列化动作、从招式派生的交接参数和映射内容，以供下一次装配比较。
        internal AnimationBindingDefinitions(CharacterAnimationConfiguration config)
        {
            foreach (var action in config.Actions)
            {
                var handoff = CharacterBehaviorAnimationBindings.ResolveHandoff(action, config);
                Actions.Add(action.Key, (JsonUtility.ToJson(action), handoff == null ? null : JsonUtility.ToJson(handoff)));
            }
            foreach (var mapping in config.BehaviorAnimations) Mappings[mapping] = JsonUtility.ToJson(mapping);
        }
    }

    // 在交接旧批次前确定本次装配需要替换的动作键、行为映射、步态、地面资源和姿态绑定。
    internal sealed class AnimationModuleChanges
    {
        internal const string PlaybackLibraryChanged = "更换动画播放库需要重新启用角色动画组件，以重建片段索引和播放图。";
        internal readonly HashSet<string> ActionKeys;
        internal readonly Dictionary<CharacterBehaviorAnimationMapping, CharacterBehaviorAnimationMapping> RetainedMappings =
            new Dictionary<CharacterBehaviorAnimationMapping, CharacterBehaviorAnimationMapping>();
        internal readonly bool MappingsChanged, GroundChanged, ResetGait, IkChanged;
        internal readonly AnimationBindingDefinitions Definitions;

        // 常规装卸按能力差异应用；显式重装配还重新读取同引用模块及其依赖的配置。
        internal AnimationModuleChanges(CharacterAnimationConfiguration before, CharacterAnimationConfiguration after,
            AnimationBindingDefinitions installed, bool refreshContents)
        {
            if (before.PlaybackLibrary != after.PlaybackLibrary)
                throw new ArgumentException(PlaybackLibraryChanged);
            Definitions = new AnimationBindingDefinitions(after);
            ActionKeys = new HashSet<string>(installed.Actions.Keys, StringComparer.Ordinal);
            foreach (var pair in Definitions.Actions)
            {
                if (installed.Actions.TryGetValue(pair.Key, out var previous) && previous.Equals(pair.Value))
                    ActionKeys.Remove(pair.Key);
                else ActionKeys.Add(pair.Key);
            }
            MatchMappings(installed, before.BehaviorAnimations, after.BehaviorAnimations);
            MappingsChanged = RetainedMappings.Count != Definitions.Mappings.Count ||
                RetainedMappings.Count != installed.Mappings.Count || !SameOrder(before.BehaviorAnimations, after.BehaviorAnimations);
            var old = before.Modules;
            var next = after.Modules;
            ResetGait = old.Gait != next.Gait;
            GroundChanged = refreshContents || ResetGait || old.StartTurn != next.StartTurn || old.Stop != next.Stop || old.Match != next.Match;
            IkChanged = GroundChanged || old.VisualRoot != next.VisualRoot || old.TransitionInertia != next.TransitionInertia ||
                before.Orientation != after.Orientation || before.PoseInertia != after.PoseInertia ||
                before.SamplesFeet != after.SamplesFeet || !SameChannels(before.IkChannels, after.IkChannels);
        }

        // 优先按原行保留映射；序列化重建行对象时，按内容与声明顺序接回等价绑定。
        private void MatchMappings(AnimationBindingDefinitions installed, CharacterBehaviorAnimationMapping[] before,
            CharacterBehaviorAnimationMapping[] after)
        {
            var matched = new HashSet<CharacterBehaviorAnimationMapping>();
            foreach (var row in after)
                if (installed.Mappings.TryGetValue(row, out var value) && value == Definitions.Mappings[row])
                {
                    RetainedMappings[row] = row;
                    matched.Add(row);
                }
            foreach (var row in after)
            {
                if (matched.Contains(row)) continue;
                foreach (var old in before)
                {
                    if (RetainedMappings.ContainsKey(old) || installed.Mappings[old] != Definitions.Mappings[row]) continue;
                    RetainedMappings[old] = row;
                    matched.Add(row);
                    break;
                }
            }
        }

        // 保留映射的声明顺序，确保同帧许可的处理顺序能随装配更新。
        private bool SameOrder(CharacterBehaviorAnimationMapping[] before, CharacterBehaviorAnimationMapping[] after)
        {
            if (before.Length != after.Length) return false;
            for (int i = 0; i < before.Length; i++)
                if (!RetainedMappings.TryGetValue(before[i], out var row) || row != after[i]) return false;
            return true;
        }

        // 判断当前模块组合是否提供相同的肢体配置及模型骨链。
        private static bool SameChannels(CharacterBoundIkChannel[] before, CharacterBoundIkChannel[] after)
        {
            if (before.Length != after.Length) return false;
            for (int i = 0; i < before.Length; i++)
                if (before[i].Source != after[i].Source || !before[i].Bones.Equals(after[i].Bones)) return false;
            return true;
        }
    }
}
