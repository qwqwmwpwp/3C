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
        private readonly List<CharacterIkTuning> channels = new List<CharacterIkTuning>();
        private readonly Dictionary<string, ActionSetModule> owners = new Dictionary<string, ActionSetModule>(StringComparer.Ordinal);
        private readonly HashSet<CharacterIkLimb> limbs = new HashSet<CharacterIkLimb>();
        internal bool Contains(AnimationModule module) => visited.Contains(module);
        // 只在角色装配入口解析程序，避免编辑器序列化校验期间访问 Resources。
        internal CompiledAnimationDecisions BindDecisions() => AnimationDecisionProgram.Bind(Decisions ? Decisions.Program : null);

        internal AnimationDecisionModule Decisions { get; private set; }
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

        // 展开根模块并检查重复能力及动作定义。
        internal static AnimationModuleAssembly Resolve(IEnumerable<AnimationModule> roots, HashSet<AnimationModule> excluded)
        {
            var result = new AnimationModuleAssembly(excluded);
            foreach (var root in roots) result.Visit(root);
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

        // 角色的各个动画阶段共享一套已编译的判定定义。
        internal void Add(AnimationDecisionModule module)
        {
            if (Decisions) throw new AnimationDecisionProgramException($"组合中的 {Decisions.name} 和 {module.name} 同时提供判定程序。");
            Decisions = module;
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

        // 校验动作键与申请部位，再收集模块的动作定义。
        internal void Add(ActionSetModule owner, CharacterAnimationActionSettings[] definitions)
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
        internal void Apply(CharacterAnimationConfiguration config, CharacterModelBindings model)
        {
            if (config.PoseStrategy is ICharacterPoseConfiguration strategy) strategy.Configure(config);
            config.Actions = actions.ToArray();
            config.IkChannels = model.BindChannels(channels.ToArray());
            config.InertiaBones = model.Inertia;
            config.Orientation = Orientation ? Orientation.Tuning : null;
            config.Transitions = new CharacterTransitionProfiles(TransitionInertia);
            config.PoseInertia = Inertia ? Inertia.Tuning : null;
        }
    }
}
