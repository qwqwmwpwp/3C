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
        public readonly float StopExitPhase;
        public readonly CharacterLocomotionVariant Variant;
        public readonly CharacterLocomotionGaitPhase Gait;
        public bool IsStop => Variant == CharacterLocomotionVariant.StopLeft || Variant == CharacterLocomotionVariant.StopRight;

        public CharacterAnimationBinding(CharacterAnimationActionSettings action, float nominalSpeed = 0f,
            CharacterLocomotionVariant variant = CharacterLocomotionVariant.None,
            CharacterLocomotionGaitPhase gait = default, float brakingClipSeconds = 0f, float stopExitPhase = 0.98f,
            string stateOverride = null, CharacterAnimationBodyRegion? source = null)
        {
            Region = source ?? action.Region;
            StateName = stateOverride ?? action.StateName;
            StateHash = Animator.StringToHash(StateName ?? string.Empty);
            Start = ClampUnit(action.StartNormalizedTime);
            Transition = ClampUnit(action.StateTransitionDuration);
            ExitTransition = ClampUnit(action.StateExitTransitionDuration);
            LayerBlend = InputMath.NonNegative(action.LayerBlendSeconds, 0f);
            NominalSpeed = nominalSpeed;
            BrakingClipSeconds = brakingClipSeconds;
            StopExitPhase = Mathf.Clamp01(stopExitPhase);
            Variant = variant;
            Gait = gait;
        }

        private const float StopTransitionLimit = 0.12f;

        public static CharacterAnimationBinding Stop(string state, CharacterLocomotionVariant side, float transition,
            float brakingSeconds, CharacterMotionTrack track = null)
        {
            var settings = new CharacterAnimationActionSettings
            {
                Region = CharacterAnimationBodyRegion.FullBody, StateName = state,
                StateTransitionDuration = track != null && track.Use != GroundMotionUse.Legacy
                    ? track.Blend : Mathf.Min(transition, StopTransitionLimit),
                StateExitTransitionDuration = StopTransitionLimit
            };
            float exit = track != null && track.ExitSeconds > 0f && track.Seconds > 0f ? track.ExitSeconds / track.Seconds : 0.98f;
            return new CharacterAnimationBinding(settings, variant: side, brakingClipSeconds: track?.BrakeSeconds ?? brakingSeconds,
                stopExitPhase: exit);
        }

        private static float ClampUnit(float value) => Mathf.Clamp01(InputMath.NonNegative(value, 0f));
    }

    /// <summary>保存配置的资源绑定，并按当前播放库或控制器校验状态路径、层和变体回退。</summary>
    internal sealed class CharacterAnimationResourceCatalog
    {
        private const float LocomotionVariantEntryLimit = 0.04f;

        private readonly bool requiresMovement, modular;
        private readonly CharacterAnimationPlayer playback;
        private readonly CharacterMotionDatabase matchDatabase, playbackLibrary;
        public RuntimeAnimatorController Controller => playback.Controller;
        public RuntimeAnimatorController MotionController => playback.Direct != null && Controller is AnimatorOverrideController overrides
            ? overrides.runtimeAnimatorController : Controller;
        internal bool MatchesLibrary(CharacterMotionDatabase db)
        {
            if (!db) return false;
            if (db.Playback?.FromGroups == true) return db == playbackLibrary && playback.Direct?.Available == true;
            var expected = db.Format == 0 ? db.Controller : db.BakedController;
            return expected && MotionController == expected;
        }
        private readonly HashSet<(int, CharacterMotionTrack)> groundTracks = new HashSet<(int, CharacterMotionTrack)>();
        private readonly Dictionary<CharacterMotionTrack, CharacterMotionTrack> snapshots =
            new Dictionary<CharacterMotionTrack, CharacterMotionTrack>();
        internal CharacterMotionTrack Snapshot(CharacterMotionTrack source)
        {
            if (!snapshots.TryGetValue(source, out var frozen))
            {
                frozen = source.CopyForBake();
                frozen.Poses = (CharacterFootPose[])source.Poses?.Clone();
                frozen.RootPath = (GroundPathKey[])source.RootPath?.Clone();
                frozen.Clip = null;
                snapshots.Add(source, frozen);
            }
            return frozen;
        }

        private readonly Dictionary<int, float?> lengths = new Dictionary<int, float?>();
        private readonly HashSet<string> oneShots = new HashSet<string>();
        private readonly float[] weights = new float[4];
        private float blendDelta;
        public bool HasGroundTrack(int state, CharacterMotionTrack track) =>
            groundTracks.Contains((state, track));
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
        private readonly List<string> actionKeys = new List<string>();
        private readonly HashSet<string> externalKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, CharacterAnimationBinding> movingBindings = new Dictionary<string, CharacterAnimationBinding>();
        private readonly HashSet<string> validMoving = new HashSet<string>();
        private readonly float idleSpeed, movingSpeed;
        private bool moving;
        private readonly Dictionary<int, CharacterLocomotionGaitPhase> phasesByState =
            new Dictionary<int, CharacterLocomotionGaitPhase>();
        private bool ready;
        private readonly HashSet<string> runFallbackKeys = new HashSet<string>(StringComparer.Ordinal);
        private string validationError;

        public float WalkSpeed { get; }
        public float RunSpeed { get; }
        public float SprintSpeed { get; }
        public float DashSpeed { get; }
        public IReadOnlyDictionary<string, CharacterAnimationRule> Rules => rules;

        public CharacterAnimationResourceCatalog(CharacterAnimationPlayer playback, CharacterAnimationConfiguration config,
            UnityEngine.Object logContext)
        {
            requiresMovement = config.HasGait;
            modular = config.Modules != null;
            this.playback = playback;
            matchDatabase = config.MatchDatabase;
            playbackLibrary = config.PlaybackLibrary;
            idleSpeed = InputMath.NonNegative(config.IdleEnterSpeed, 0.08f);
            movingSpeed = Mathf.Max(idleSpeed, InputMath.NonNegative(config.IdleExitSpeed, 0.15f));
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
            AddStops(CharacterAnimationIntentConverter.WalkKey, config.WalkStop, config.Transition);
            AddStops(CharacterAnimationIntentConverter.MoveKey, config.RunStop, config.Transition);
            AddStops(CharacterAnimationIntentConverter.SprintKey, config.SprintStop, config.Transition);
            AddActions(config, logContext);
        }

        internal void ReplaceActions(CharacterAnimationConfiguration config, UnityEngine.Object logContext)
        {
            foreach (string key in actionKeys)
            {
                bindings.Remove(key);
                rules.Remove(key);
                movingBindings.Remove(key);
            }
            actionKeys.Clear();
            externalKeys.Clear();
            AddActions(config, logContext);
            RefreshBindings();
        }

        private void AddActions(CharacterAnimationConfiguration config, UnityEngine.Object logContext)
        {
            if (config.Actions == null) return;
            foreach (CharacterAnimationActionSettings action in config.Actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Key))
                    continue;
                if (rules.ContainsKey(action.Key))
                {
                    Debug.LogError($"角色动画语义键重复：{action.Key}，只采用首个配置。", logContext);
                    continue;
                }
                actionKeys.Add(action.Key);
                var source = config.PlaybackLibrary && config.PlaybackLibrary.UsesBodyOutputs &&
                    action.Region == CharacterAnimationBodyRegion.FullBody ? CharacterAnimationBodyRegion.FullBodyOverlay : action.Region;
                var handoff = CharacterBehaviorAnimationBindings.ResolveHandoff(action, config);
                if (handoff != null && (!handoff.Valid || source == CharacterAnimationBodyRegion.FullBody))
                {
                    Debug.LogWarning($"动作 {action.Key} 的接管阶段或加权层无效，沿用普通播放。", logContext);
                    handoff = null;
                }
                rules.Add(action.Key, new CharacterAnimationRule(action.Key, source, action.Priority,
                    action.InterruptionPolicy, action.InertiaSuppression.ToVector(), action.Partners, handoff));
                bindings.Add(action.Key, new CharacterAnimationBinding(action, source: source));
                if (!string.IsNullOrWhiteSpace(action.MovingStateName))
                    movingBindings.Add(action.Key, new CharacterAnimationBinding(action, stateOverride: action.MovingStateName, source: source));
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
            if (!string.IsNullOrWhiteSpace(stops.LeftSupport))
                variants.Add((key, CharacterLocomotionVariant.StopLeft),
                    CharacterAnimationBinding.Stop(stops.LeftSupport, CharacterLocomotionVariant.StopLeft, transition, braking));
            if (!string.IsNullOrWhiteSpace(stops.RightSupport))
                variants.Add((key, CharacterLocomotionVariant.StopRight),
                    CharacterAnimationBinding.Stop(stops.RightSupport, CharacterLocomotionVariant.StopRight, transition, braking));
        }

        private void AddVariant(string key, CharacterLocomotionVariant variant, string state, float start, float transition,
            float nominalSpeed = 0f, CharacterLocomotionGaitPhase gait = default)
        {
            if (string.IsNullOrWhiteSpace(state))
                return;

            var action = new CharacterAnimationActionSettings
            {
                Region = CharacterAnimationBodyRegion.FullBody, StateName = state,
                StartNormalizedTime = start, StateTransitionDuration = transition
            };
            variants.Add((key, variant), new CharacterAnimationBinding(action, nominalSpeed, variant, gait));
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

        public bool Ready => ready;
        public string ValidationError => validationError;

        public void RefreshBindings()
        {
            groundTracks.Clear();
            lengths.Clear();
            oneShots.Clear();
            validBindings.Clear();
            validVariants.Clear();
            validMoving.Clear();
            ready = false;
            runFallbackKeys.Clear();
            validationError = null;
            if (playback.Direct != null && playback.Direct.ExpectsBodyLibrary && !playback.Direct.Available)
            {
                validationError = "请检查双层动作库、身体遮罩与 Animator 的控制器绑定。";
                if (playbackLibrary.Playback?.FromGroups == true)
                    validationError = "基础动画数据组需要已烘焙的双层动作库、互补的身体遮罩，并将 Animator Controller 留空。";
                return;
            }
            if (!Controller && playback.Direct?.Available != true) return;

            foreach (KeyValuePair<string, CharacterAnimationBinding> item in bindings)
                if (CanUse(item.Value))
                    validBindings.Add(item.Key, item.Value);
            foreach (var item in variants)
                if (CanUse(item.Value))
                    validVariants.Add(item.Key, WithBakedStopTrack(item.Value));
            foreach (var item in movingBindings)
                if (CanUse(item.Value)) validMoving.Add(item.Key);
            if (validBindings.TryGetValue(CharacterAnimationIntentConverter.MoveKey, out CharacterAnimationBinding run))
            {
                UseRunIfMissingOrShared(CharacterAnimationIntentConverter.WalkKey, run);
                UseRunIfMissingOrShared(CharacterAnimationIntentConverter.SprintKey, run);
            }
            if (modular && !validBindings.ContainsKey(CharacterAnimationIntentConverter.DashKey) &&
                validBindings.TryGetValue(CharacterAnimationIntentConverter.IdleKey, out var idle))
                validBindings.Add(CharacterAnimationIntentConverter.DashKey, idle);
            foreach (var pair in validBindings)
            {
                CacheLength(pair.Value.StateHash);
                if (playback.Active.CanPlayOnce(pair.Value.StateHash)) oneShots.Add(pair.Key);
            }
            foreach (var value in validVariants.Values) CacheLength(value.StateHash);
            foreach (var key in validMoving) CacheLength(movingBindings[key].StateHash);
            var tracks = !matchDatabase ? null : matchDatabase.Format == 0 ? matchDatabase.Tracks : matchDatabase.BakedTracks;
            if (tracks != null && MatchesLibrary(matchDatabase))
                foreach (var track in tracks)
                {
                    if (track?.States == null || !track.IsReady) continue;
                    foreach (var path in track.States)
                    {
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        int hash = Animator.StringToHash(path);
                        if (playback.Active.HasState(CharacterAnimationBodyRegion.FullBody, hash) &&
                            (playback.Direct == null || playback.Direct.MatchesTrack(hash, track)))
                        {
                            groundTracks.Add((hash, Snapshot(track)));
                            CacheLength(hash);
                        }
                    }
                }
            ready = Require(CharacterAnimationIntentConverter.IdleKey) &&
                (!requiresMovement || Require(CharacterAnimationIntentConverter.MoveKey) && Require(CharacterAnimationIntentConverter.DashKey));
        }

        private CharacterAnimationBinding WithBakedStopTrack(CharacterAnimationBinding binding)
        {
            if (!binding.IsStop) return binding;
            var clip = playback.Direct?.StateClip(binding.StateHash);
            CharacterMotionTrack Find(CharacterMotionDatabase db)
            {
                if (!MatchesLibrary(db)) return null;
                var candidate = db.FindStopTrack(binding.StateName, clip);
                return candidate != null && playback.Direct?.MatchesTrack(binding.StateHash, candidate) != false ? candidate : null;
            }
            var track = Find(matchDatabase);
            if (track == null && playbackLibrary != matchDatabase) track = Find(playbackLibrary);
            return track == null ? binding : CharacterAnimationBinding.Stop(binding.StateName, binding.Variant,
                binding.Transition, binding.BrakingClipSeconds, track);
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

        public bool IsWeightedAction(string key) => key != null && rules.TryGetValue(key, out var rule) &&
            rule.Region != CharacterAnimationBodyRegion.FullBody;

        public bool CanSubmit(string key) => ready && key != null && externalKeys.Contains(key) && validBindings.ContainsKey(key);

        public bool CanPlayOnce(string key) => CanSubmit(key) && oneShots.Contains(key);

        public void ObserveMovement(CharacterAnimationMotion motion)
        {
            moving = motion.IsValid && motion.ActualSpeed > (moving ? idleSpeed : movingSpeed);
        }

        public bool HasBinding(string key) => validBindings.ContainsKey(key);

        private bool CanUse(CharacterAnimationBinding binding) =>
            !string.IsNullOrWhiteSpace(binding.StateName) && playback.Active.HasState(binding.Region, binding.StateHash);

        public CharacterAnimationBinding ResolveBinding(CharacterAnimationDemand demand, out bool usedFallback)
        {
            if (moving && demand.Playback == null && movingBindings.TryGetValue(demand.Key, out var movingBinding))
            {
                usedFallback = !validMoving.Contains(demand.Key);
                return usedFallback ? validBindings[demand.Key] : movingBinding;
            }
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

        private void CacheLength(int state) => lengths[state] = playback.Active.StateSeconds(state);
        public float? StateSeconds(int state) => lengths.TryGetValue(state, out var length) ? length : null;

        public void CaptureWeights(float dt)
        {
            for (int i = 0; i < weights.Length; i++)
                weights[i] = playback.Active.LayerWeight((CharacterAnimationBodyRegion)i);
            blendDelta = playback.Active.LayerBlendDelta(dt);
        }
        public float LayerWeight(CharacterAnimationBodyRegion region) => weights[(int)region];

        public float ProjectLayerWeight(CharacterAnimationBodyRegion region, in CharacterAnimationLayerPlan plan, float deltaTime)
        {
            if (plan.LayerBlendSeconds <= 0f) return plan.Weight;
            return plan.WeightAfter(LayerWeight(region), blendDelta);
        }

        public float ClipSeconds(AnimationClip clip)
        {
            if (!clip) return 0f;
            if (Controller is AnimatorOverrideController overrides)
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
