using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 缓存编辑器按源素材核对得到的烘焙指纹问题，供运行时装配和检查工具共用。
    internal static class CharacterSearchLibraryBakeIssues
    {
        private static readonly Dictionary<CharacterMotionDatabase, IReadOnlyDictionary<string, string>> cache = new();

        // 让角色装配可以在编辑器读取采样源，并监听资产内容的变化。
        [InitializeOnLoadMethod]
        private static void Register()
        {
            GroundSearchLibraries.EditorBakeIssues = Read;
            EditorApplication.projectChanged += Refresh;
            EditorApplication.playModeStateChanged += OnPlayModeChange;
            Undo.undoRedoPerformed += Refresh;
            Undo.postprocessModifications += OnModified;
        }

        // 在检查或装配时按库重用当前编辑器的指纹核对结果。
        internal static IReadOnlyDictionary<string, string> Read(CharacterMotionDatabase database)
        {
            if (!cache.TryGetValue(database, out var issues))
                cache.Add(database, issues = CompareBakedTracks(database));
            return issues;
        }

        // 使用采样器自己的配方指纹逐条判断旧样本，保留同库未受影响的轨道。
        private static IReadOnlyDictionary<string, string> CompareBakedTracks(CharacterMotionDatabase database)
        {
            var errors = new List<string>();
            var expected = CharacterMotionGroupBaker.ReadExpectedHashes(database, errors);
            var issues = new Dictionary<string, string>(StringComparer.Ordinal);
            bool hasCurrentTrack = false;
            foreach (var track in database.BakedTracks ?? Array.Empty<CharacterMotionTrack>())
            {
                if (track?.States is not { Length: > 0 } || string.IsNullOrWhiteSpace(track.States[0])) continue;
                string state = track.States[0];
                if (!expected.TryGetValue(state, out string sourceHash))
                    issues[state] = "当前源数据组无对应的有效采样配方，请检查并重新烘焙";
                else if (string.IsNullOrEmpty(track.SampleSourceHash))
                    issues[state] = "缺少采样指纹，请重新烘焙";
                else if (!string.Equals(track.SampleSourceHash, sourceHash, StringComparison.Ordinal))
                    issues[state] = "采样源已修改，请重新烘焙";
                else hasCurrentTrack = true;
            }
            if (!hasCurrentTrack && errors.Count > 0) issues[string.Empty] = string.Join("，", errors);
            return issues;
        }

        // 固定每次播放会话的初始检查边界，包括关闭域重载的编辑器设置。
        private static void OnPlayModeChange(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) cache.Clear();
        }

        // 素材与配方在 Inspector 中原地修改后，等序列化完成再校验其依赖。
        private static UndoPropertyModification[] OnModified(UndoPropertyModification[] modifications)
        {
            foreach (var modification in modifications)
            {
                var target = modification.currentValue?.target;
                if (target is not CharacterMotionDatabase && target is not CharacterMotionGroups &&
                    target is not GroundFeatureSchema && target is not AnimationClip && target is not Avatar) continue;
                EditorApplication.delayCall -= Refresh;
                EditorApplication.delayCall += Refresh;
                break;
            }
            return modifications;
        }

        // 已播放角色只在来源核对结果改变时重新装配，普通项目变更不重置播放图。
        internal static void Refresh()
        {
            if (!EditorApplication.isPlaying)
            {
                cache.Clear();
                return;
            }
            bool changed = false;
            foreach (var database in new List<CharacterMotionDatabase>(cache.Keys))
            {
                if (!database)
                {
                    cache.Remove(database);
                    changed = true;
                    continue;
                }
                var updated = CompareBakedTracks(database);
                changed |= !SameIssues(cache[database], updated);
                cache[database] = updated;
            }
            if (changed) GroundSearchLibraries.InvalidateBindings();
        }

        // 对照逐轨道诊断，判断变更是否影响已装配候选。
        private static bool SameIssues(IReadOnlyDictionary<string, string> previous, IReadOnlyDictionary<string, string> updated)
        {
            if (previous.Count != updated.Count) return false;
            foreach (var pair in previous)
                if (!updated.TryGetValue(pair.Key, out string issue) || issue != pair.Value) return false;
            return true;
        }
    }
}
