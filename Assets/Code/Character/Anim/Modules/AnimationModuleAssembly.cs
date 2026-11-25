using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在主线程将模块图展开一次。重复引用共享同一份定义，所有权冲突在安装前报告。</summary>
    internal sealed class AnimationModuleAssembly
    {
        private readonly HashSet<AnimationModule> excluded;
        private AnimationModuleAssembly(HashSet<AnimationModule> excluded) => this.excluded = excluded;

        private readonly HashSet<AnimationModule> visiting = new HashSet<AnimationModule>();
        private readonly HashSet<AnimationModule> visited = new HashSet<AnimationModule>();
        private readonly List<CharacterAnimationActionSettings> actions = new List<CharacterAnimationActionSettings>();
        private readonly List<CharacterBehaviorAnimationMapping> mappings = new List<CharacterBehaviorAnimationMapping>();
        private readonly List<CharacterIkTuning> channels = new List<CharacterIkTuning>();
        private readonly Dictionary<string, ActionSetModule> owners = new Dictionary<string, ActionSetModule>(StringComparer.Ordinal);
        private readonly HashSet<CharacterIkLimb> limbs = new HashSet<CharacterIkLimb>();
        internal bool Contains(AnimationModule module) => visited.Contains(module);
        internal GaitModule Gait { get; private set; }
        internal StartTurnModule StartTurn { get; private set; }
        internal StopModule Stop { get; private set; }
        internal MotionMatchModule Match { get; private set; }
        internal OrientationWarpModule Orientation { get; private set; }
        internal VisualRootModule VisualRoot { get; private set; }
        internal TransitionInertiaModule TransitionInertia { get; private set; }
        internal PoseInertiaModule Inertia { get; private set; }

        internal static AnimationModuleAssembly Resolve(params AnimationModule[] roots)
        {
            return Resolve(roots, null);
        }

        internal static AnimationModuleAssembly Resolve(IEnumerable<AnimationModule> roots, HashSet<AnimationModule> excluded, bool checkActionReferences = true)
        {
            var result = new AnimationModuleAssembly(excluded);
            foreach (var root in roots) result.Visit(root);
            for (int i = 0; i < result.mappings.Count; i++)
            {
                var row = result.mappings[i];
                if (!row.HasValidTrigger)
                    throw new ArgumentException($"第 {i + 1} 条行为映射（{row.BehaviorId} / {row.AttackMove}）需要单一许可行为和有效动作键。");
                if (checkActionReferences)
                {
                    result.RequireAction(row.EntryAction, row, i);
                    result.RequireAction(row.HoldAction, row, i);
                }
            }
            return result;
        }

        internal static bool Includes(List<AnimationModule> roots, HashSet<AnimationModule> excluded, AnimationModule target)
        {
            var pending = new Stack<AnimationModule>(roots);
            var seen = new HashSet<AnimationModule>();
            while (pending.Count > 0)
            {
                var module = pending.Pop();
                if (!module || excluded.Contains(module) || !seen.Add(module)) continue;
                if (module == target) return true;
                if (module is AnimationModuleGroup group && group.Children != null)
                    foreach (var child in group.Children) pending.Push(child);
            }
            return false;
        }

        private void RequireAction(string key, CharacterBehaviorAnimationMapping row, int index)
        {
            if (!string.IsNullOrWhiteSpace(key) && !owners.ContainsKey(key))
                throw new ArgumentException($"第 {index + 1} 条行为映射（{row.BehaviorId} / {row.AttackMove}）引用了组合中缺少的动作 {key}。");
        }

        internal void Visit(AnimationModule module)
        {
            if (!module) throw new ArgumentException("组合中有空的模块引用。");
            if (excluded?.Contains(module) == true || visited.Contains(module)) return;
            if (!visiting.Add(module)) throw new ArgumentException($"动画组合存在循环引用：{module.name}。");
            module.Include(this);
            visiting.Remove(module);
            visited.Add(module);
        }

        private static T Single<T>(T previous, T next) where T : AnimationModule
        {
            if (previous) throw new ArgumentException($"动画组合中的 {previous.name} 与 {next.name} 同时提供同一项能力。");
            return next;
        }

        internal void Add(GaitModule module) => Gait = Single(Gait, module);
        internal void Add(StartTurnModule module) => StartTurn = Single(StartTurn, module);
        internal void Add(StopModule module) => Stop = Single(Stop, module);
        internal void Add(MotionMatchModule module) => Match = Single(Match, module);
        // 同一角色只允许安装一个步态朝向校正模块。
        internal void Add(OrientationWarpModule module) => Orientation = Single(Orientation, module);
        // 视觉根修正独占水平根位移和整体偏航。
        internal void Add(VisualRootModule module) => VisualRoot = Single(VisualRoot, module);
        // 通用切换余势在同一角色上只安装一次。
        internal void Add(TransitionInertiaModule module) => TransitionInertia = Single(TransitionInertia, module);
        internal void Add(PoseInertiaModule module) => Inertia = Single(Inertia, module);

        // 校验动作键与申请部位，再收集模块的动作定义和行为映射。
        internal void Add(ActionSetModule owner, CharacterAnimationActionSettings[] definitions, CharacterBehaviorAnimationMapping[] triggers)
        {
            foreach (var action in definitions ?? Array.Empty<CharacterAnimationActionSettings>())
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Key) || action.Key.StartsWith("$", StringComparison.Ordinal))
                    throw new ArgumentException($"{owner.name} 有空的动作或保留的动作键。");
                if (action.Region is not (CharacterAnimationBodyRegion.FullBody or CharacterAnimationBodyRegion.UpperBody or
                    CharacterAnimationBodyRegion.LowerBody))
                    throw new ArgumentException($"动画模块 {owner.name} 的动作 {action.Key} 申请了无效部位 {(int)action.Region}。请选择全身、上身或下身。");
                if (owners.TryGetValue(action.Key, out var previous))
                    throw new ArgumentException($"{previous.name} 与 {owner.name} 重复定义动作 {action.Key}。");
                owners.Add(action.Key, owner);
                actions.Add(action);
            }
            if (triggers != null)
                foreach (var row in triggers)
                    if (row != null) mappings.Add(row);
        }

        internal void Add(CharacterIkTuning[] settings)
        {
            if (settings == null) return;
            foreach (var channel in settings)
            {
                if (channel == null) continue;
                if (!limbs.Add(channel.Limb)) throw new ArgumentException($"动画组合重复配置 IK 部位 {channel.Limb}。");
                channels.Add(channel);
            }
        }

        // 将选中的模块语义和模型绑定写入本角色的配置快照。
        internal void Apply(CharacterAnimationConfiguration config, CharacterModelBindings model, CharacterProfileOverrides overrides)
        {
            if (Gait) Gait.Apply(config, overrides);
            if (StartTurn) StartTurn.Apply(config, overrides);
            if (Stop) Stop.Apply(config);
            if (Match) Match.Apply(config);
            config.Actions = actions.ToArray();
            config.BehaviorAnimations = mappings.ToArray();
            config.IkChannels = model.BindChannels(channels.ToArray());
            config.InertiaBones = model.Inertia;
            config.Orientation = Orientation ? Orientation.Tuning : null;
            config.Transitions = new CharacterTransitionProfiles(TransitionInertia);
            config.PoseInertia = Inertia ? Inertia.Tuning : null;
        }
    }
}
