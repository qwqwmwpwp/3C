using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>混合中的各步态共用同一物理速度，各自按素材原速换算步频。</summary>
    internal readonly struct CharacterAnimationPlaybackRates
    {
        public readonly float Selected;
        public readonly float Walk;
        public readonly float Run;
        public readonly float Sprint;
        public readonly float Dash;
        public readonly float? StrideSpeed;

        public CharacterAnimationPlaybackRates(float? actualSpeed, float selected, float walk, float run,
            float sprint, float dash)
        {
            StrideSpeed = actualSpeed;
            Selected = ForClip(actualSpeed, selected);
            Walk = ForClip(actualSpeed, walk);
            Run = ForClip(actualSpeed, run);
            Sprint = ForClip(actualSpeed, sprint);
            Dash = ForClip(actualSpeed, dash);
        }

        private static float ForClip(float? actualSpeed, float nominalSpeed) =>
            actualSpeed.HasValue && nominalSpeed > 0f ? actualSpeed.Value / nominalSpeed : 1f;
    }

    /// <summary>记录本帧哪些层实际下发了播放命令；保持、未选中及不可用的层没有播放起点。</summary>
    internal readonly struct CharacterAnimationPlaybackEmission
    {
        public bool FullBody { get; }
        public bool LowerBody { get; }
        public bool UpperBody { get; }
        public bool Overlay { get; }

        public CharacterAnimationPlaybackEmission(bool fullBody, bool lowerBody, bool upperBody, bool overlay)
        {
            FullBody = fullBody;
            LowerBody = lowerBody;
            UpperBody = upperBody;
            Overlay = overlay;
        }
    }

    /// <summary>唯一写入 Animator 的端点，只执行资源解析后的播放指令、参数、层权重及暂停恢复。</summary>
    internal sealed class CharacterAnimationPlayer
    {
        private readonly Animator animator;
        private readonly CharacterClipPlayback playback;
        private readonly int fullLayer;
        private readonly int lowerLayer;
        private readonly int upperLayer;
        private readonly int overlayLayer;
        private bool paused;
        private float speedBeforePause;
        private bool ownsLayerWeights;
        private RuntimeAnimatorController weightOwnerController;
        private float originalLowerWeight;
        private float originalUpperWeight;
        private float originalOverlayWeight;
        private readonly HashSet<int> floatParameters = new HashSet<int>();
        private RuntimeAnimatorController parameterController;

        public CharacterAnimationPlayer(Animator animator, CharacterClipPlayback playback, CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            this.playback = playback;
            fullLayer = config.FullLayer;
            lowerLayer = config.LowerLayer;
            upperLayer = config.UpperLayer;
            overlayLayer = config.ValidOverlayLayer;
        }

        public CharacterAnimationPlaybackEmission Apply(in CharacterAnimationPlaybackPlan plan, float deltaTime)
        {
            if (playback.Available) return playback.Apply(plan, deltaTime);
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
            playback.Close();
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

        public void SetPaused(bool value)
        {
            if (paused == value)
                return;
            paused = value;
            if (paused)
            {
                speedBeforePause = animator.speed;
                animator.speed = 0f;
            }
            else
                animator.speed = speedBeforePause;
        }

        public void ResetPlayback()
        {
            playback.Close();
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
            playback.Close();
            if (animator == null)
                return;
            SetPaused(false);
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
