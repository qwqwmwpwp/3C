using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>执行 Animator Controller 的状态、参数和图层权重。</summary>
    internal sealed class CharacterControllerPlayback : ICharacterPlaybackBackend
    {
        private readonly List<AnimatorClipInfo> clips = new List<AnimatorClipInfo>(2);
        private readonly Animator animator;
        private readonly RuntimeAnimatorController bakedController;
        private readonly System.Func<int, AnimationClip> findBakedClip;
        private readonly CharacterAnimationConfiguration config;
        private bool ownsLayerWeights;
        private RuntimeAnimatorController weightOwnerController;
        private float originalLowerWeight;
        private float originalUpperWeight;
        private float originalOverlayWeight;
        private readonly HashSet<int> floatParameters = new HashSet<int>();
        private RuntimeAnimatorController parameterController;

        private readonly Dictionary<int, IssuedState> issued = new Dictionary<int, IssuedState>();

        // 同一图层的目标状态在过渡时由 next 报告。只有推进过的动画时钟才消耗确认机会。
        private sealed class IssuedState
        {
            private const int ConfirmationSteps = 2; // 给原生控制器一次延迟发布目标状态的机会。
            private const double RecoveryRetrySeconds = 0.25; // 持续动作恢复指令未生效时，限制重发频率。
            private int target;
            private float? previousEnd;
            private double lastTick, retryAfter;
            private int misses;
            private CharacterPlaybackConfirmation confirmation;
            private CharacterClipProgress lastDestination;
            private float lastBlend;

            public void Begin(in CharacterAnimationLayerPlan plan, CharacterClipProgress before, double tick)
            {
                target = plan.StateHash;
                previousEnd = plan.Demand.Playback != null && before.fullPathHash == target && before.normalizedTime >= 1f
                    ? before.normalizedTime : (float?)null;
                lastTick = tick;
                retryAfter = plan.Reason == CharacterAnimationPlaybackReason.RestoreState ? tick + RecoveryRetrySeconds : tick;
                lastDestination = before;
                lastBlend = 0f;
                misses = 0;
                confirmation = CharacterPlaybackConfirmation.Awaiting;
            }

            public CharacterPlaybackConfirmation Read(in CharacterLayerObservation seen, double tick, bool mayBeCulled)
            {
                if (tick == lastTick) return confirmation;
                lastTick = tick;
                var destination = seen.Destination;
                bool advanced = destination.fullPathHash != lastDestination.fullPathHash ||
                    destination.normalizedTime != lastDestination.normalizedTime || seen.BlendProgress != lastBlend;
                lastDestination = destination;
                lastBlend = seen.BlendProgress;
                if (mayBeCulled && !advanced) return confirmation;
                bool oldEnd = previousEnd.HasValue && destination.normalizedTime >= previousEnd.Value;
                if (destination.fullPathHash == target && !oldEnd)
                {
                    previousEnd = null;
                    confirmation = CharacterPlaybackConfirmation.Confirmed;
                }
                else if (confirmation == CharacterPlaybackConfirmation.Confirmed || ++misses >= ConfirmationSteps && tick >= retryAfter)
                    confirmation = CharacterPlaybackConfirmation.Lost;
                return confirmation;
            }
        }

        private double AnimationTick => animator.updateMode == AnimatorUpdateMode.AnimatePhysics ? Time.fixedTimeAsDouble :
            animator.updateMode == AnimatorUpdateMode.UnscaledTime ? Time.unscaledTimeAsDouble : Time.timeAsDouble;

        public CharacterControllerPlayback(Animator animator, CharacterAnimationConfiguration config, System.Func<int, AnimationClip> findBakedClip)
        {
            this.animator = animator;
            this.findBakedClip = findBakedClip;
            if (config.PlaybackLibrary) bakedController = config.PlaybackLibrary.BakedController;
            this.config = config;
        }

        public bool HasState(CharacterAnimationBodyRegion source, int hash)
        {
            int layer = config.LegacyLayer(source);
            return animator && IsValidLayer(layer) && animator.HasState(layer, hash);
        }
        public bool CanPlayOnce(int hash) => true; // Animator 在状态开始后才能报告循环标记。
        public float? StateSeconds(int state)
        {
            if (!bakedController || !(animator.runtimeAnimatorController is AnimatorOverrideController overrides) ||
                overrides.runtimeAnimatorController != bakedController) return null;
            var original = findBakedClip(state);
            if (!original) return null;
            var replacement = overrides[original];
            return replacement ? replacement.length : original.length;
        }

        public float LayerWeight(CharacterAnimationBodyRegion source)
        {
            int layer = config.LegacyLayer(source);
            return animator && animator.isInitialized && IsValidLayer(layer) ? animator.GetLayerWeight(layer) : 0f;
        }

        public float LayerBlendDelta(float deltaTime) => deltaTime;

        public CharacterLayerObservation Observe(CharacterAnimationBodyRegion region, bool readLengths, bool readNext)
        {
            int layer = config.LegacyLayer(region);
            if (!animator || !animator.isInitialized || !IsValidLayer(layer)) return default;
            var seen = new CharacterLayerObservation(animator, layer);
            CharacterPlaybackConfirmation confirmation = default;
            if (issued.TryGetValue(layer, out var command))
            {
                bool culled = animator.cullingMode == AnimatorCullingMode.CullCompletely;
                confirmation = command.Read(seen, AnimationTick, culled || animator.speed == 0f);
            }
            float source = seen.SourceLength, destination = seen.DestinationLength;
            float? firstClip = null;
            if (readLengths && seen.Ready)
            {
                animator.GetCurrentAnimatorClipInfo(layer, clips);
                float weight = 0f, weightedLength = 0f;
                foreach (var clip in clips)
                {
                    if (clip.clip == null) continue;
                    weightedLength += clip.weight * clip.clip.length;
                    weight += clip.weight;
                }
                if (weight > 0f) source = weightedLength / weight;
                if (clips.Count > 0 && clips[0].clip) firstClip = clips[0].clip.length;
                if (readNext && seen.InTransition) animator.GetNextAnimatorClipInfo(layer, clips);
                if (clips.Count > 0 && clips[0].clip) destination = clips[0].clip.length;
            }
            return new CharacterLayerObservation(seen.Current, seen.Next, seen.InTransition, seen.BlendProgress,
                source, destination, confirmation, readLengths ? seen.SourceLength : (float?)null, firstClip);
        }

        public CharacterAnimationPlaybackEmission Apply(in CharacterAnimationPlaybackPlan plan, float deltaTime)
        {
            RefreshParameterCache();
            SetFloat("LocomotionRate", plan.Rates.Selected);
            SetFloat("WalkRate", plan.Rates.Walk);
            SetFloat("RunRate", plan.Rates.Run);
            SetFloat("SprintRate", plan.Rates.Sprint);
            SetFloat("DashRate", plan.Rates.Dash);
            SetFloat("StopRate", plan.StopRate);
            if (!ownsLayerWeights)
            {
                if (IsLocalLayer(config.LowerLayer))
                    originalLowerWeight = animator.GetLayerWeight(config.LowerLayer);
                if (IsLocalLayer(config.UpperLayer))
                    originalUpperWeight = animator.GetLayerWeight(config.UpperLayer);
                if (IsValidLayer(config.ValidOverlayLayer))
                    originalOverlayWeight = animator.GetLayerWeight(config.ValidOverlayLayer);
                weightOwnerController = animator.runtimeAnimatorController;
                ownsLayerWeights = true;
            }
            bool full = Play(plan.FullBody, config.FullLayer);
            bool overlay = ApplyLocal(plan.Overlay, config.ValidOverlayLayer, deltaTime);
            bool lower = ApplyLocal(plan.LowerBody, config.LowerLayer, deltaTime);
            bool upper = ApplyLocal(plan.UpperBody, config.UpperLayer, deltaTime);
            return new CharacterAnimationPlaybackEmission(fullBody: full, lowerBody: lower, upperBody: upper, overlay: overlay);
        }

        public void InvalidateController()
        {
            issued.Clear();
            parameterController = null;
            floatParameters.Clear();
            ownsLayerWeights = false;
            weightOwnerController = null;
        }

        private void RefreshParameterCache()
        {
            RuntimeAnimatorController controller = animator.runtimeAnimatorController;
            if (parameterController == controller)
                return;
            parameterController = controller;
            floatParameters.Clear();
            if (controller == null)
                return;
            foreach (AnimatorControllerParameter parameter in animator.parameters)
                if (parameter.type == AnimatorControllerParameterType.Float)
                    floatParameters.Add(parameter.nameHash);
        }

        private void SetFloat(string parameterName, float value)
        {
            int hash = Animator.StringToHash(parameterName);
            if (floatParameters.Contains(hash))
                animator.SetFloat(hash, value);
        }

        private bool ApplyLocal(in CharacterAnimationLayerPlan plan, int layer, float deltaTime)
        {
            if (!IsLocalLayer(layer))
                return false;
            bool emitted = Play(plan, layer);
            animator.SetLayerWeight(layer, plan.WeightAfter(animator.GetLayerWeight(layer), LayerBlendDelta(deltaTime)));
            return emitted;
        }

        private bool Play(in CharacterAnimationLayerPlan plan, int layer)
        {
            if (!plan.HasState || plan.Command == CharacterAnimationPlaybackCommand.Keep)
                return false;
            var source = animator.GetCurrentAnimatorStateInfo(layer);
            var before = animator.IsInTransition(layer) ? animator.GetNextAnimatorStateInfo(layer) : source;
            if (!issued.TryGetValue(layer, out var command)) issued.Add(layer, command = new IssuedState());
            command.Begin(plan, new CharacterClipProgress(before), AnimationTick);
            bool resume = plan.ResumeLoop && source.loop && source.fullPathHash == plan.StateHash;
            var start = resume ? CharacterClipStart.Normalized(Mathf.Repeat(source.normalizedTime, 1f)) : plan.Start;
            if (plan.Command == CharacterAnimationPlaybackCommand.CrossFade)
            {
                if (start.OffsetSeconds.HasValue)
                    animator.CrossFadeInFixedTime(plan.StateHash, plan.Blend.InSeconds(source.length), layer, start.OffsetSeconds.Value);
                else
                    animator.CrossFade(plan.StateHash, plan.Blend.InSourceFraction(source.length), layer, start.Phase);
            }
            else if (start.OffsetSeconds.HasValue)
                animator.PlayInFixedTime(plan.StateHash, layer, start.OffsetSeconds.Value);
            else
                animator.Play(plan.StateHash, layer, start.Phase);
            return true;
        }

        public void ResetPlayback()
        {
            issued.Clear();
            // 尚未接管的权重无需清理；控制器已被外部替换时也不能写入旧权重所属的层。
            if (animator == null || !ownsLayerWeights || weightOwnerController != animator.runtimeAnimatorController)
                return;
            if (IsLocalLayer(config.LowerLayer))
                animator.SetLayerWeight(config.LowerLayer, 0f);
            if (IsLocalLayer(config.UpperLayer))
                animator.SetLayerWeight(config.UpperLayer, 0f);
            if (IsValidLayer(config.ValidOverlayLayer))
                animator.SetLayerWeight(config.ValidOverlayLayer, 0f);
        }

        public void Release()
        {
            issued.Clear();
            if (animator == null)
                return;
            if (ownsLayerWeights && weightOwnerController == animator.runtimeAnimatorController)
            {
                if (IsLocalLayer(config.LowerLayer))
                    animator.SetLayerWeight(config.LowerLayer, originalLowerWeight);
                if (IsLocalLayer(config.UpperLayer))
                    animator.SetLayerWeight(config.UpperLayer, originalUpperWeight);
                if (IsValidLayer(config.ValidOverlayLayer))
                    animator.SetLayerWeight(config.ValidOverlayLayer, originalOverlayWeight);
            }
            ownsLayerWeights = false;
            weightOwnerController = null;
        }

        private bool IsValidLayer(int layer) => layer >= 0 && layer < animator.layerCount;
        private bool IsLocalLayer(int layer) => IsValidLayer(layer) && layer != config.FullLayer &&
            (layer == config.ValidOverlayLayer || config.LowerLayer != config.UpperLayer);

    }
}
