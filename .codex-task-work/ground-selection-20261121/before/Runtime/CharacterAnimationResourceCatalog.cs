using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal readonly struct CharacterAnimationBinding
    {
        public readonly CharacterAnimationBodyRegion Region;
        public readonly string StateName;
        public readonly int StateHash;
        public readonly float Start;
        public readonly float Transition;
        public readonly float ExitTransition;
        public readonly float LayerBlend;
        public readonly float NominalSpeed;
        public readonly float BrakingClipSeconds;
        public readonly CharacterLocomotionVariant Variant;
        public readonly CharacterLocomotionGaitPhase Gait;
        public bool IsStop => Variant == CharacterLocomotionVariant.StopLeft || Variant == CharacterLocomotionVariant.StopRight;

        public CharacterAnimationBinding(CharacterAnimationActionSettings action, float nominalSpeed = 0f,
            CharacterLocomotionVariant variant = CharacterLocomotionVariant.None,
            CharacterLocomotionGaitPhase gait = default, float brakingClipSeconds = 0f)
        {
            Region = action.Region;
            StateName = action.StateName;
            StateHash = Animator.StringToHash(action.StateName ?? string.Empty);
            Start = ClampUnit(action.StartNormalizedTime);
            Transition = ClampUnit(action.StateTransitionDuration);
            ExitTransition = ClampUnit(action.StateExitTransitionDuration);
            LayerBlend = InputMath.NonNegative(action.LayerBlendSeconds, 0f);
            NominalSpeed = nominalSpeed;
            BrakingClipSeconds = brakingClipSeconds;
            Variant = variant;
            Gait = gait;
        }

        private static float ClampUnit(float value) => Mathf.Clamp01(InputMath.NonNegative(value, 0f));
    }

    /// <summary>保存配置的资源绑定，并按当前控制器校验状态路径、层和变体回退。</summary>
    internal sealed class CharacterAnimationResourceCatalog
    {
        private const float LocomotionVariantEntryLimit = 0.04f;

        private readonly Animator animator;
        private readonly int fullLayer;
        private readonly int lowerLayer;
        private readonly int upperLayer;
        private readonly Dictionary<string, CharacterAnimationRule> rules =
            new Dictionary<string, CharacterAnimationRule>(StringComparer.Ordinal);
        private readonly Dictionary<string, CharacterAnimationBinding> bindings =
            new Dictionary<string, CharacterAnimationBinding>(StringComparer.Ordinal);
        private readonly Dictionary<string, CharacterAnimationBinding> validBindings =
            new Dictionary<string, CharacterAnimationBinding>(StringComparer.Ordinal);
        // 步行单独起步，跑步与持续冲刺共用跑步起步，各档共用左右转身。
        private readonly Dictionary<(string, CharacterLocomotionVariant), CharacterAnimationBinding> variants =
            new Dictionary<(string, CharacterLocomotionVariant), CharacterAnimationBinding>();
        private readonly Dictionary<(string, CharacterLocomotionVariant), CharacterAnimationBinding> validVariants =
            new Dictionary<(string, CharacterLocomotionVariant), CharacterAnimationBinding>();
        private readonly HashSet<string> externalKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, CharacterLocomotionGaitPhase> phasesByState =
            new Dictionary<int, CharacterLocomotionGaitPhase>();
        private RuntimeAnimatorController checkedController;
        private bool hasCheckedController;
        private bool ready;
        private readonly HashSet<string> runFallbackKeys = new HashSet<string>(StringComparer.Ordinal);
        private string validationError;

        public float WalkSpeed { get; }
        public float RunSpeed { get; }
        public float SprintSpeed { get; }
        public float DashSpeed { get; }
        public IEnumerable<CharacterAnimationRule> Rules => rules.Values;

        public CharacterAnimationResourceCatalog(Animator animator, CharacterAnimationConfiguration config,
            UnityEngine.Object logContext)
        {
            this.animator = animator;
            fullLayer = config.FullLayer;
            lowerLayer = config.LowerLayer;
            upperLayer = config.UpperLayer;
            Dictionary<string, CharacterLocomotionGaitPhase> gaitPhases = BuildGaitPhases(config.LocomotionGaitPhases, logContext);
            foreach (var pair in gaitPhases)
                if (pair.Value.IsValid)
                    phasesByState[Animator.StringToHash(pair.Key)] = pair.Value;
            WalkSpeed = NominalSpeed(config.WalkSpeed);
            RunSpeed = NominalSpeed(config.RunSpeed);
            SprintSpeed = NominalSpeed(config.SprintSpeed);
            DashSpeed = InputMath.NonNegative(config.DashClipSpeed, 0f) > 0f ? NominalSpeed(config.DashClipSpeed) : 0f;
            AddBehavior(CharacterAnimationIntentConverter.IdleKey, config.IdleState, config.LocomotionPriority, config.Transition);
            AddBehavior(CharacterAnimationIntentConverter.MoveKey, config.MoveState, config.LocomotionPriority, config.Transition,
                nominalSpeed: RunSpeed, gait: GetGait(gaitPhases, config.MoveState));
            AddBehavior(CharacterAnimationIntentConverter.WalkKey, config.WalkState, config.LocomotionPriority, config.Transition,
                nominalSpeed: WalkSpeed, gait: GetGait(gaitPhases, config.WalkState));
            AddBehavior(CharacterAnimationIntentConverter.SprintKey, config.SprintState, config.LocomotionPriority, config.Transition,
                nominalSpeed: SprintSpeed, gait: GetGait(gaitPhases, config.SprintState));
            AddBehavior(CharacterAnimationIntentConverter.DashKey, config.DashState, config.DashPriority,
                config.DashTransition, config.DashTransition, nominalSpeed: DashSpeed);

            float variantTransition = Mathf.Min(config.Transition, LocomotionVariantEntryLimit); // 短起步和急转需要更快淡入。
            AddVariant(CharacterAnimationIntentConverter.WalkKey, CharacterLocomotionVariant.Start, config.WalkStartState,
                config.WalkStartNormalizedTime, variantTransition, WalkSpeed, GetGait(gaitPhases, config.WalkStartState));
            AddVariant(CharacterAnimationIntentConverter.MoveKey, CharacterLocomotionVariant.Start, config.RunStartState,
                config.RunStartNormalizedTime, variantTransition, RunSpeed, GetGait(gaitPhases, config.RunStartState));
            AddVariant(null, CharacterLocomotionVariant.TurnLeft, config.TurnLeftState, 0f, variantTransition);
            AddVariant(null, CharacterLocomotionVariant.TurnRight, config.TurnRightState, 0f, variantTransition);
            float stopTransition = Mathf.Min(config.Transition, 0.12f);
            AddStops(CharacterAnimationIntentConverter.WalkKey, config.WalkStop, stopTransition);
            AddStops(CharacterAnimationIntentConverter.MoveKey, config.RunStop, stopTransition);
            AddStops(CharacterAnimationIntentConverter.SprintKey, config.SprintStop, stopTransition);
            if (config.Actions == null)
                return;
            foreach (CharacterAnimationActionSettings action in config.Actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Key))
                    continue;
                if (rules.ContainsKey(action.Key))
                {
                    Debug.LogError($"角色动画语义键重复：{action.Key}，只采用首个配置。", logContext);
                    continue;
                }
                rules.Add(action.Key, new CharacterAnimationRule(action.Key, action.Region, action.Priority,
                    action.InterruptionPolicy, action.InertiaSuppression.ToVector()));
                bindings.Add(action.Key, new CharacterAnimationBinding(action));
                if (!action.Key.StartsWith("$", StringComparison.Ordinal))
                    externalKeys.Add(action.Key);
            }
        }

        private void AddBehavior(string key, string state, int priority, float transition, float exitTransition = 1f,
            float nominalSpeed = 0f, CharacterLocomotionGaitPhase gait = default)
        {
            var action = new CharacterAnimationActionSettings
            {
                Key = key, Region = CharacterAnimationBodyRegion.FullBody, Priority = priority,
                StateName = state, StateTransitionDuration = transition, StateExitTransitionDuration = exitTransition
            };
            rules.Add(key, new CharacterAnimationRule(key, action.Region, priority));
            bindings.Add(key, new CharacterAnimationBinding(action, nominalSpeed, gait: gait));
        }

        private void AddStops(string key, CharacterStopAnimationSettings stops, float transition)
        {
            if (stops == null)
                return;
            float braking = InputMath.NonNegative(stops.BrakingClipSeconds, 0f);
            AddVariant(key, CharacterLocomotionVariant.StopLeft, stops.LeftSupport, 0f, transition,
                exitTransition: 0.12f, brakingClipSeconds: braking);
            AddVariant(key, CharacterLocomotionVariant.StopRight, stops.RightSupport, 0f, transition,
                exitTransition: 0.12f, brakingClipSeconds: braking);
        }

        private void AddVariant(string key, CharacterLocomotionVariant variant, string state, float start, float transition,
            float nominalSpeed = 0f, CharacterLocomotionGaitPhase gait = default, float exitTransition = 1f,
            float brakingClipSeconds = 0f)
        {
            if (string.IsNullOrWhiteSpace(state))
                return;

            var action = new CharacterAnimationActionSettings
            {
                Region = CharacterAnimationBodyRegion.FullBody, StateName = state,
                StartNormalizedTime = start, StateTransitionDuration = transition, StateExitTransitionDuration = exitTransition
            };
            variants.Add((key, variant), new CharacterAnimationBinding(action, nominalSpeed, variant, gait, brakingClipSeconds));
        }

        private static Dictionary<string, CharacterLocomotionGaitPhase> BuildGaitPhases(
            CharacterLocomotionGaitPhaseSettings[] settings, UnityEngine.Object logContext)
        {
            var phases = new Dictionary<string, CharacterLocomotionGaitPhase>(StringComparer.Ordinal);
            if (settings == null)
                return phases;

            foreach (CharacterLocomotionGaitPhaseSettings entry in settings)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.StateName))
                    continue;
                if (phases.ContainsKey(entry.StateName))
                {
                    Debug.LogWarning($"角色动画步态相位状态重复：{entry.StateName}，只采用首个配置。", logContext);
                    continue;
                }
                phases.Add(entry.StateName, new CharacterLocomotionGaitPhase(entry.LeftContact, entry.RightContact, entry.SupportFraction));
            }
            return phases;
        }

        private static CharacterLocomotionGaitPhase GetGait(Dictionary<string, CharacterLocomotionGaitPhase> phases,
            string state) => state != null && phases.TryGetValue(state, out CharacterLocomotionGaitPhase gait) ? gait : default;

        public bool EnsureResources(out bool controllerChanged, out string error)
        {
            RuntimeAnimatorController controller = animator != null ? animator.runtimeAnimatorController : null;
            controllerChanged = !hasCheckedController || checkedController != controller;
            if (controllerChanged)
            {
                hasCheckedController = true;
                checkedController = controller;
                validBindings.Clear();
                validVariants.Clear();
                ready = false;
                runFallbackKeys.Clear();
                validationError = null;
                if (controller != null)
                {
                    foreach (KeyValuePair<string, CharacterAnimationBinding> item in bindings)
                        if (CanUse(item.Value))
                            validBindings.Add(item.Key, item.Value);
                    foreach (var item in variants)
                        if (CanUse(item.Value))
                            validVariants.Add(item.Key, item.Value);
                    if (validBindings.TryGetValue(CharacterAnimationIntentConverter.MoveKey, out CharacterAnimationBinding run))
                    {
                        UseRunIfMissingOrShared(CharacterAnimationIntentConverter.WalkKey, run);
                        UseRunIfMissingOrShared(CharacterAnimationIntentConverter.SprintKey, run);
                    }
                    ready = Require(CharacterAnimationIntentConverter.IdleKey) &&
                        Require(CharacterAnimationIntentConverter.MoveKey) && Require(CharacterAnimationIntentConverter.DashKey);
                }
            }
            error = validationError;
            return ready;
        }

        private bool Require(string key)
        {
            if (validBindings.ContainsKey(key))
                return true;
            validationError = $"角色动画必需资源不可用：{key} → {bindings[key].StateName}。请检查全身层及完整状态路径；需求仲裁尚未执行。";
            return false;
        }

        private void UseRunIfMissingOrShared(string key, CharacterAnimationBinding run)
        {
            if (validBindings.TryGetValue(key, out CharacterAnimationBinding binding) && binding.StateHash != run.StateHash)
                return;
            validBindings[key] = run;
            runFallbackKeys.Add(key);
        }

        public bool CanSubmit(string key) => ready && key != null && externalKeys.Contains(key) && validBindings.ContainsKey(key);

        public bool CanResolveActive(IEnumerable<string> keys, out string invalidKey)
        {
            foreach (string key in keys)
            {
                if (validBindings.ContainsKey(key))
                    continue;
                invalidKey = key;
                return false;
            }
            invalidKey = null;
            return true;
        }

        private bool CanUse(CharacterAnimationBinding binding)
        {
            int layer = GetLayer(binding.Region);
            return layer >= 0 && layer < animator.layerCount && !string.IsNullOrWhiteSpace(binding.StateName) &&
                (binding.Region == CharacterAnimationBodyRegion.FullBody ||
                    layer != fullLayer && lowerLayer != upperLayer) && animator.HasState(layer, binding.StateHash);
        }

        public int GetLayer(CharacterAnimationBodyRegion region)
        {
            switch (region)
            {
                case CharacterAnimationBodyRegion.FullBody: return fullLayer;
                case CharacterAnimationBodyRegion.LowerBody: return lowerLayer;
                case CharacterAnimationBodyRegion.UpperBody: return upperLayer;
                default: return -1;
            }
        }

        public CharacterAnimationBinding ResolveBinding(CharacterAnimationDemand demand, out bool usedFallback)
        {
            if (demand.IsLocomotion && demand.LocomotionVariant != CharacterLocomotionVariant.None)
            {
                string gaitKey = null;
                if (demand.LocomotionVariant == CharacterLocomotionVariant.Start)
                    gaitKey = demand.Key == CharacterAnimationIntentConverter.SprintKey
                        ? CharacterAnimationIntentConverter.MoveKey : demand.Key;
                if (validVariants.TryGetValue((gaitKey, demand.LocomotionVariant), out CharacterAnimationBinding variant))
                {
                    usedFallback = false;
                    return variant;
                }
                usedFallback = true;
            }
            else
            {
                usedFallback = runFallbackKeys.Contains(demand.Key);
            }
            return validBindings[demand.Key];
        }

        public bool TryGetPhase(int state, out CharacterLocomotionGaitPhase phase) => phasesByState.TryGetValue(state, out phase);

        public float ClipSeconds(AnimationClip clip)
        {
            if (!clip) return 0f;
            if (checkedController is AnimatorOverrideController overrides)
                clip = overrides[clip] ?? clip;
            return clip.length;
        }

        public CharacterAnimationBinding ResolveStop(string key, bool? left, out bool fallback)
        {
            var side = left == true ? CharacterLocomotionVariant.StopLeft : CharacterLocomotionVariant.StopRight;
            if (left.HasValue && validVariants.TryGetValue((key, side), out var stop))
            {
                fallback = false;
                return stop;
            }
            fallback = true;
            return validBindings[CharacterAnimationIntentConverter.IdleKey];
        }

        private static float NominalSpeed(float value) => Mathf.Max(0.01f, InputMath.NonNegative(value, 1f));
    }
}
