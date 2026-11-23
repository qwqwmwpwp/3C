using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>执行 Animator Controller 的状态、参数和图层权重。</summary>
    internal sealed class CharacterControllerPlayback : ICharacterPlaybackBackend
    {
        private readonly Animator animator;
        private readonly int fullLayer;
        private readonly int lowerLayer;
        private readonly int upperLayer;
        private readonly int overlayLayer;
        private bool ownsLayerWeights;
        private RuntimeAnimatorController weightOwnerController;
        private float originalLowerWeight;
        private float originalUpperWeight;
        private float originalOverlayWeight;
        private readonly HashSet<int> floatParameters = new HashSet<int>();
        private RuntimeAnimatorController parameterController;

        public CharacterControllerPlayback(Animator animator, CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            fullLayer = config.FullLayer;
            lowerLayer = config.LowerLayer;
            upperLayer = config.UpperLayer;
            overlayLayer = config.ValidOverlayLayer;
        }

        private readonly List<AnimatorClipInfo> clips = new List<AnimatorClipInfo>(2);
        public int LayerCount => animator ? animator.layerCount : 0;
        public bool HasState(int layer, int hash) => animator && IsValidLayer(layer) && animator.HasState(layer, hash);
        public bool CanPlayOnce(int hash) => true; // Animator 在状态开始后才能报告循环标记。
        public float StateSeconds(int state, float fallback) => fallback;
        public float LayerWeight(int layer) => animator && animator.isInitialized && IsValidLayer(layer) ? animator.GetLayerWeight(layer) : 0f;

        public CharacterLayerObservation Observe(int layer, bool readLengths, bool readNext)
        {
            if (!animator || !animator.isInitialized) return default;
            var seen = new CharacterLayerObservation(animator, layer);
            if (!readLengths || !seen.Ready) return seen;

            animator.GetCurrentAnimatorClipInfo(layer, clips);
            float weight = 0f, weightedLength = 0f;
            foreach (var clip in clips)
            {
                if (clip.clip == null) continue;
                weightedLength += clip.weight * clip.clip.length;
                weight += clip.weight;
            }
            float source = weight > 0f ? weightedLength / weight : seen.SourceLength;
            if (readNext && seen.InTransition) animator.GetNextAnimatorClipInfo(layer, clips);
            float destination = clips.Count > 0 && clips[0].clip ? clips[0].clip.length : seen.DestinationLength;
            return new CharacterLayerObservation(seen.Current, seen.Next, seen.InTransition, seen.BlendProgress, source, destination);
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
                if (IsLocalLayer(lowerLayer))
                    originalLowerWeight = animator.GetLayerWeight(lowerLayer);
                if (IsLocalLayer(upperLayer))
                    originalUpperWeight = animator.GetLayerWeight(upperLayer);
                if (IsValidLayer(overlayLayer))
                    originalOverlayWeight = animator.GetLayerWeight(overlayLayer);
                weightOwnerController = animator.runtimeAnimatorController;
                ownsLayerWeights = true;
            }
            bool full = Play(plan.FullBody, fullLayer);
            bool overlay = ApplyLocal(plan.Overlay, overlayLayer, deltaTime);
            bool lower = ApplyLocal(plan.LowerBody, lowerLayer, deltaTime);
            bool upper = ApplyLocal(plan.UpperBody, upperLayer, deltaTime);
            return new CharacterAnimationPlaybackEmission(full, lower, upper, overlay);
        }

        public void InvalidateController()
        {
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
            float targetWeight = plan.HasState ? plan.Weight : 0f;
            float weight = plan.LayerBlendSeconds > 0f
                ? Mathf.MoveTowards(animator.GetLayerWeight(layer), targetWeight, deltaTime / plan.LayerBlendSeconds)
                : targetWeight;
            animator.SetLayerWeight(layer, weight);
            return emitted;
        }

        private bool Play(in CharacterAnimationLayerPlan plan, int layer)
        {
            if (!plan.HasState || plan.Command == CharacterAnimationPlaybackCommand.Keep)
                return false;
            float start = plan.StartNormalizedTime;
            var source = animator.GetCurrentAnimatorStateInfo(layer);
            bool resume = plan.ResumeLoop && source.loop && source.fullPathHash == plan.StateHash;
            if (resume) start = Mathf.Repeat(source.normalizedTime, 1f);
            if (!resume && plan.Command == CharacterAnimationPlaybackCommand.CrossFade && plan.FixedStartSeconds.HasValue)
                animator.CrossFadeInFixedTime(plan.StateHash, plan.TransitionDuration, layer, plan.FixedStartSeconds.Value);
            else if (plan.Command == CharacterAnimationPlaybackCommand.CrossFade)
                animator.CrossFade(plan.StateHash, plan.TransitionDuration, layer, start);
            else
                animator.Play(plan.StateHash, layer, start);
            return true;
        }

        public void ResetPlayback()
        {
            // 尚未接管的权重无需清理；控制器已被外部替换时也不能写入旧权重所属的层。
            if (animator == null || !ownsLayerWeights || weightOwnerController != animator.runtimeAnimatorController)
                return;
            if (IsLocalLayer(lowerLayer))
                animator.SetLayerWeight(lowerLayer, 0f);
            if (IsLocalLayer(upperLayer))
                animator.SetLayerWeight(upperLayer, 0f);
            if (IsValidLayer(overlayLayer))
                animator.SetLayerWeight(overlayLayer, 0f);
        }

        public void Release()
        {
            if (animator == null)
                return;
            if (ownsLayerWeights && weightOwnerController == animator.runtimeAnimatorController)
            {
                if (IsLocalLayer(lowerLayer))
                    animator.SetLayerWeight(lowerLayer, originalLowerWeight);
                if (IsLocalLayer(upperLayer))
                    animator.SetLayerWeight(upperLayer, originalUpperWeight);
                if (IsValidLayer(overlayLayer))
                    animator.SetLayerWeight(overlayLayer, originalOverlayWeight);
            }
            ownsLayerWeights = false;
            weightOwnerController = null;
        }

        private bool IsValidLayer(int layer) => layer >= 0 && layer < animator.layerCount;
        private bool IsLocalLayer(int layer) => IsValidLayer(layer) && layer != fullLayer &&
            (layer == overlayLayer || lowerLayer != upperLayer);

    }
}
