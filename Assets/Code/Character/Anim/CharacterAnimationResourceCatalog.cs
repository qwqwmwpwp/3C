using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 记录语义动作对应的片段、入点和淡入参数。
    internal readonly struct CharacterAnimationBinding
    {
        public readonly string StateName, Library, Track;
        public readonly int StateHash;
        public readonly float Start;
        public readonly float Transition;
        public readonly float ExitTransition;
        public readonly float LayerBlend;
        public readonly float BrakingClipSeconds;
        public readonly float StopExitPhase;
        public readonly CharacterLocomotionVariant Variant;
        public readonly CharacterLocomotionGaitPhase Gait;
        public bool IsStop => Variant == CharacterLocomotionVariant.StopLeft || Variant == CharacterLocomotionVariant.StopRight;

        // 以动作配置和可选变体参数构造片段绑定。
        public CharacterAnimationBinding(CharacterAnimationActionSettings action,
            CharacterLocomotionVariant variant = CharacterLocomotionVariant.None,
            CharacterLocomotionGaitPhase gait = default, float brakingClipSeconds = 0f, float stopExitPhase = 0.98f,
            string stateOverride = null, int? playbackKey = null, CharacterMotionTrack source = null)
        {
            StateName = stateOverride ?? action.StateName;
            StateHash = playbackKey ?? Animator.StringToHash(StateName ?? string.Empty);
            Library = source?.LibraryLabel;
            Track = source?.TrackLabel;
            Start = ClampUnit(action.StartNormalizedTime);
            Transition = ClampUnit(action.StateTransitionDuration);
            ExitTransition = ClampUnit(action.StateExitTransitionDuration);
            LayerBlend = InputMath.NonNegative(action.LayerBlendSeconds, 0f);
            BrakingClipSeconds = brakingClipSeconds;
            StopExitPhase = Mathf.Clamp01(stopExitPhase);
            Variant = variant;
            Gait = gait;
        }

        private const float StopTransitionLimit = 0.12f;

        // 为收步片段设置制动标记和离开相位，优先读取烘焙值。
        public static CharacterAnimationBinding Stop(string state, CharacterLocomotionVariant side, float transition,
            float brakingSeconds, CharacterMotionTrack track = null, int? playbackKey = null)
        {
            var settings = new CharacterAnimationActionSettings
            {
                StateName = state,
                StateTransitionDuration = track?.Blend ?? Mathf.Min(transition, StopTransitionLimit),
                StateExitTransitionDuration = StopTransitionLimit
            };
            float exit = track != null && track.ExitSeconds > 0f && track.Seconds > 0f ? track.ExitSeconds / track.Seconds : 0.98f;
            return new CharacterAnimationBinding(settings, variant: side, brakingClipSeconds: track?.BrakeSeconds ?? brakingSeconds,
                stopExitPhase: exit, playbackKey: playbackKey, source: track);
        }

        private static float ClampUnit(float value) => Mathf.Clamp01(InputMath.NonNegative(value, 0f));
    }

    /// <summary>维护语义动作到烘焙片段的绑定，并验证必需状态路径和步态变体。</summary>
    internal sealed class CharacterAnimationResourceCatalog
    {
        private const float LocomotionVariantEntryLimit = 0.04f;

        private readonly bool requiresMovement;
        private readonly CharacterClipPlayback clips;
        private readonly CharacterMotionDatabase playbackLibrary;
        private readonly GroundSearchLibraries searchLibraries;

        // 检查已启用的地面匹配是否具备显式搜索库和可播放轨道。
        internal string ValidateMatchLibrary()
        {
            if (searchLibraries?.Databases.Length == 0) return GroundSearchLibraries.NoDeclaredLibraries;
            if (!playbackLibrary) return "角色缺少动画播放库，将使用常规步态规则。";
            if (groundTracks.Count == 0) return searchLibraries?.Issue ?? "当前范围没有与播放素材对应的有效地面轨道，使用常规步态规则。";
            return null;
        }

        private readonly HashSet<(int, CharacterMotionTrack)> groundTracks = new HashSet<(int, CharacterMotionTrack)>();
        private readonly Dictionary<int, float?> lengths = new Dictionary<int, float?>();
        private readonly HashSet<string> oneShots = new HashSet<string>();
        private readonly Dictionary<string, (int id, bool single)> oneShotClips = new Dictionary<string, (int, bool)>(StringComparer.Ordinal);
        private readonly float[] weights = new float[4];
        internal CharacterTransitionProfiles Transitions { get; set; }
        private float blendDelta;
        internal float PlaybackClockScale { get; private set; } = 1f;
        // 读取实际播放状态的时钟策略，供后台匹配与图推进使用同一倍率。
        internal GroundPlaybackPace Pace(int state) => clips.Pace(state);
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
        private readonly Dictionary<int, CharacterLocomotionGaitPhase> phasesByState =
            new Dictionary<int, CharacterLocomotionGaitPhase>();
        private bool ready;
        private string validationError;

        public float WalkSpeed { get; }
        public IReadOnlyDictionary<string, CharacterAnimationRule> Rules => rules;

        // 汇集模块提供的基础步态、可选变体和动作绑定。
        public CharacterAnimationResourceCatalog(CharacterClipPlayback clips, CharacterAnimationConfiguration config,
            UnityEngine.Object logContext)
        {
            requiresMovement = config.HasGait;
            this.clips = clips;
            Transitions = config.Transitions;
            searchLibraries = config.SearchLibraries;
            playbackLibrary = config.PlaybackLibrary;
            Dictionary<string, CharacterLocomotionGaitPhase> gaitPhases = BuildGaitPhases(config.LocomotionGaitPhases, logContext);
            foreach (var pair in gaitPhases)
                if (pair.Value.IsValid)
                    phasesByState[Animator.StringToHash(pair.Key)] = pair.Value;
            WalkSpeed = NominalSpeed(config.WalkSpeed);
            AddBehavior(CharacterAnimationIntentConverter.IdleKey, config.IdleState, config.LocomotionPriority, config.Transition);
            AddBehavior(CharacterAnimationIntentConverter.MoveKey, config.MoveState, config.LocomotionPriority, config.Transition,
                gait: GetGait(gaitPhases, config.MoveState));
            AddBehavior(CharacterAnimationIntentConverter.WalkKey, config.WalkState, config.LocomotionPriority, config.Transition,
                gait: GetGait(gaitPhases, config.WalkState));
            AddBehavior(CharacterAnimationIntentConverter.SprintKey, config.SprintState, config.LocomotionPriority, config.Transition,
                gait: GetGait(gaitPhases, config.SprintState));
            AddBehavior(CharacterAnimationIntentConverter.DashKey, config.DashState, config.DashPriority,
                config.DashTransition, config.DashTransition);

            float variantTransition = Mathf.Min(config.Transition, LocomotionVariantEntryLimit); // 短起步和急转需要更快淡入。
            AddVariant(CharacterAnimationIntentConverter.WalkKey, CharacterLocomotionVariant.Start, config.WalkStartState,
                config.WalkStartNormalizedTime, variantTransition, GetGait(gaitPhases, config.WalkStartState));
            AddVariant(CharacterAnimationIntentConverter.MoveKey, CharacterLocomotionVariant.Start, config.RunStartState,
                config.RunStartNormalizedTime, variantTransition, GetGait(gaitPhases, config.RunStartState));
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

        // 把模块的身体范围转换为基础姿势之上的播放源。
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
                var source = action.Region switch
                {
                    CharacterAnimationBodyRegion.UpperBody => CharacterAnimationSource.UpperBody,
                    CharacterAnimationBodyRegion.LowerBody => CharacterAnimationSource.LowerBody,
                    _ => CharacterAnimationSource.FullBodyOverlay
                };
                var handoff = action.Handoff?.OverridePhases == true ? action.Handoff : null;
                if (handoff != null && !handoff.Valid)
                {
                    Debug.LogWarning($"动作 {action.Key} 的接管阶段无效，沿用普通播放。", logContext);
                    handoff = null;
                }
                rules.Add(action.Key, new CharacterAnimationRule(action.Key, source, action.Priority,
                    action.InterruptionPolicy, action.InertiaSuppression.ToVector(), action.Partners, handoff));
                bindings.Add(action.Key, new CharacterAnimationBinding(action));
                if (!string.IsNullOrWhiteSpace(action.MovingStateName))
                    movingBindings.Add(action.Key, new CharacterAnimationBinding(action, stateOverride: action.MovingStateName));
                if (!action.Key.StartsWith("$", StringComparison.Ordinal))
                    externalKeys.Add(action.Key);
            }
        }

        // 把意图转换器直接驱动的步态和突进绑定到基础姿势源。
        private void AddBehavior(string key, string state, int priority, float transition, float exitTransition = 1f,
            CharacterLocomotionGaitPhase gait = default)
        {
            var action = new CharacterAnimationActionSettings
            {
                Key = key, Priority = priority,
                StateName = state, StateTransitionDuration = transition, StateExitTransitionDuration = exitTransition
            };
            rules.Add(key, new CharacterAnimationRule(key, CharacterAnimationSource.Baseline, priority));
            bindings.Add(key, new CharacterAnimationBinding(action, gait: gait));
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

        // 为起步或转向补充基础姿势可以选择的片段。
        private void AddVariant(string key, CharacterLocomotionVariant variant, string state, float start, float transition,
            CharacterLocomotionGaitPhase gait = default)
        {
            if (string.IsNullOrWhiteSpace(state))
                return;

            var action = new CharacterAnimationActionSettings
            {
                StateName = state,
                StartNormalizedTime = start, StateTransitionDuration = transition
            };
            variants.Add((key, variant), new CharacterAnimationBinding(action, variant, gait));
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

        // 重新核对片段库并刷新当前角色的可播放动作和匹配轨道。
        public void RefreshBindings()
        {
            groundTracks.Clear();
            lengths.Clear();
            oneShots.Clear();
            oneShotClips.Clear();
            validBindings.Clear();
            validVariants.Clear();
            validMoving.Clear();
            ready = false;
            validationError = clips.ValidateBinding();
            if (validationError != null) return;

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
            if (!validBindings.ContainsKey(CharacterAnimationIntentConverter.DashKey) &&
                validBindings.TryGetValue(CharacterAnimationIntentConverter.IdleKey, out var idle))
                validBindings.Add(CharacterAnimationIntentConverter.DashKey, idle);
            foreach (var pair in validBindings)
            {
                CacheLength(pair.Value.StateHash);
                if (clips.CanPlayOnce(pair.Value.StateHash))
                {
                    oneShots.Add(pair.Key);
                    oneShotClips[pair.Key] = clips.OneShotIdentity(pair.Value.StateHash);
                }
            }
            foreach (var value in validVariants.Values) CacheLength(value.StateHash);
            foreach (var key in validMoving) CacheLength(movingBindings[key].StateHash);
            if (searchLibraries != null)
                foreach (var entry in searchLibraries.Tracks)
                    foreach (int hash in entry.Hashes)
                        if (clips.MatchesTrack(hash, entry.Source))
                        {
                            groundTracks.Add((hash, entry.Frozen));
                            CacheLength(hash);
                        }
            ready = Require(CharacterAnimationIntentConverter.IdleKey) &&
                (!requiresMovement || Require(CharacterAnimationIntentConverter.MoveKey) && Require(CharacterAnimationIntentConverter.DashKey));
        }

        // 为收步状态合入同一播放库中有效的制动采样。
        private CharacterAnimationBinding WithBakedStopTrack(CharacterAnimationBinding binding)
        {
            if (!binding.IsStop) return binding;
            var track = playbackLibrary.FindStopTrack(binding.StateName, clips.StateClip(binding.StateHash));
            if (track != null && !clips.MatchesTrack(binding.StateHash, track)) return binding;
            return track == null ? binding : CharacterAnimationBinding.Stop(binding.StateName, binding.Variant,
                binding.Transition, binding.BrakingClipSeconds, track);
        }

        private bool Require(string key)
        {
            if (validBindings.ContainsKey(key))
                return true;
            string state = bindings.TryGetValue(key, out var binding) ? binding.StateName : null;
            validationError = $"角色动画必需资源不可用：{key} → {state ?? "未配置状态"}。请检查基础动画数据组与完整状态路径；需求仲裁尚未执行。";
            return false;
        }

        // 为缺少独立片段的步态使用跑步绑定。
        private void UseRunIfMissingOrShared(string key, CharacterAnimationBinding run)
        {
            if (validBindings.TryGetValue(key, out CharacterAnimationBinding binding) && binding.StateHash != run.StateHash)
                return;
            validBindings[key] = run;
        }

        // 判断此动作能否参与权重接管。
        public bool IsWeightedAction(string key) => key != null && rules.TryGetValue(key, out var rule) &&
            rule.Source != CharacterAnimationSource.Baseline;

        public bool CanSubmit(string key) => ready && key != null && externalKeys.Contains(key) && validBindings.ContainsKey(key);

        // 查询主线程冻结的可播放单次素材，后台不读取资源对象。
        public bool CanPlayOnce(string key) => CanSubmit(key) && oneShots.Contains(key);

        // 以实际资源标识核对已冻结的播放请求；不匹配时不得复用旧素材游标。
        internal int OneShotClipId(string key, bool singleOnly = false) => key != null && oneShotClips.TryGetValue(key, out var clip) &&
            (!singleOnly || clip.single) ? clip.id : 0;

        // 主线程在每次帧采集和显式调用时核对实际素材，后台只查本批身份和值。
        internal void CaptureOneShots()
        {
            foreach (string key in actionKeys)
            {
                if (validBindings.TryGetValue(key, out var binding) && clips.CanPlayOnce(binding.StateHash))
                {
                    oneShots.Add(key);
                    oneShotClips[key] = clips.OneShotIdentity(binding.StateHash);
                }
                else
                {
                    oneShots.Remove(key);
                    oneShotClips.Remove(key);
                }
            }
        }

        public bool HasBinding(string key) => validBindings.ContainsKey(key);

        // 检查绑定状态是否存在于当前播放库。
        private bool CanUse(CharacterAnimationBinding binding) =>
            !string.IsNullOrWhiteSpace(binding.StateName) && clips.HasState(binding.StateHash);

        // 根据当前移动状态与动作变体选定可播放绑定。
        public CharacterAnimationBinding ResolveBinding(CharacterAnimationDemand demand, bool useMovingVariant)
        {
            if (useMovingVariant && demand.Playback == null && movingBindings.TryGetValue(demand.Key, out var movingBinding))
                return validMoving.Contains(demand.Key) ? movingBinding : validBindings[demand.Key];
            if (demand.IsLocomotion && demand.LocomotionVariant != CharacterLocomotionVariant.None)
            {
                string gaitKey = null;
                if (demand.LocomotionVariant == CharacterLocomotionVariant.Start)
                    gaitKey = demand.Key == CharacterAnimationIntentConverter.SprintKey
                        ? CharacterAnimationIntentConverter.MoveKey : demand.Key;
                if (validVariants.TryGetValue((gaitKey, demand.LocomotionVariant), out CharacterAnimationBinding variant))
                    return variant;
            }
            return validBindings[demand.Key];
        }

        public bool TryGetPhase(int state, out CharacterLocomotionGaitPhase phase) => phasesByState.TryGetValue(state, out phase);

        // 缓存工作线程规划时所需的片段长度。
        private void CacheLength(int state) => lengths[state] = clips.StateSeconds(state);
        public float? StateSeconds(int state) => lengths.TryGetValue(state, out var length) ? length : null;

        // 在主线程捕获混合权重和推进时间，供规划使用。
        public void CaptureWeights(in CharacterPlaybackFrameStep step)
        {
            CaptureOneShots();
            RefreshWeights(step);
        }

        // 尾段求值或新帧采集后，从真实覆盖与剩余图步长重新预测各层的接管。
        internal void RefreshWeights(in CharacterPlaybackFrameStep step)
        {
            for (int i = 0; i < weights.Length; i++)
                weights[i] = clips.LayerWeight((CharacterAnimationSource)i);
            blendDelta = step.GraphDelta;
            PlaybackClockScale = step.FrameDelta > 0f ? blendDelta / step.FrameDelta : 0f;
        }
        public float LayerWeight(CharacterAnimationSource region) => weights[(int)region];

        // 预测本次推进后指定播放源的权重。
        public float ProjectLayerWeight(CharacterAnimationSource region, in CharacterAnimationLayerPlan plan) =>
            plan.WeightAfter(LayerWeight(region), blendDelta);

        // 按步态和承重脚选定收步片段，缺少可用片段时回到待机。
        public CharacterAnimationBinding ResolveStop(string key, bool? left)
        {
            var side = left == true ? CharacterLocomotionVariant.StopLeft : CharacterLocomotionVariant.StopRight;
            if (left.HasValue && validVariants.TryGetValue((key, side), out var stop))
                return stop;
            return validBindings[CharacterAnimationIntentConverter.IdleKey];
        }

        private static float NominalSpeed(float value) => Mathf.Max(0.01f, InputMath.NonNegative(value, 1f));
    }
}
