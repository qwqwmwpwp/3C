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

        public CharacterAnimationPlaybackEmission(bool fullBody, bool lowerBody, bool upperBody)
        {
            FullBody = fullBody;
            LowerBody = lowerBody;
            UpperBody = upperBody;
        }
    }

    /// <summary>唯一写入 Animator 的端点，只执行资源解析后的播放指令、参数、层权重及暂停恢复。</summary>
    internal sealed class CharacterAnimationPlayer
    {
        private readonly Animator animator;
        private readonly int fullLayer;
        private readonly int lowerLayer;
        private readonly int upperLayer;
        private bool paused;
        private float speedBeforePause;
        private bool ownsLayerWeights;
        private RuntimeAnimatorController weightOwnerController;
        private float originalLowerWeight;
        private float originalUpperWeight;
        private readonly HashSet<int> floatParameters = new HashSet<int>();
        private RuntimeAnimatorController parameterController;

        public CharacterAnimationPlayer(Animator animator, int fullLayer, int lowerLayer, int upperLayer)
        {
            this.animator = animator;
            this.fullLayer = fullLayer;
            this.lowerLayer = lowerLayer;
            this.upperLayer = upperLayer;
        }

        public CharacterAnimationPlaybackEmission Apply(in CharacterAnimationPlaybackPlan plan, float deltaTime)
        {
            RefreshParameterCache();
            SetFloat("ActualSpeed", plan.Motion.ActualSpeed);
            SetFloat("ExpectedSpeed", plan.Motion.ExpectedSpeed);
            SetFloat("LocalMoveX", plan.Motion.LocalMoveDirection.x);
            SetFloat("LocalMoveY", plan.Motion.LocalMoveDirection.y);
            SetFloat("FacingDelta", plan.Motion.FacingDeltaDegrees);
            SetFloat("Obstruction", plan.Motion.Obstruction);
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
                weightOwnerController = animator.runtimeAnimatorController;
                ownsLayerWeights = true;
            }
            bool full = Play(plan.FullBody, fullLayer);
            bool lower = ApplyLocal(plan.LowerBody, lowerLayer, deltaTime);
            bool upper = ApplyLocal(plan.UpperBody, upperLayer, deltaTime);
            return new CharacterAnimationPlaybackEmission(full, lower, upper);
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
            if (plan.Command == CharacterAnimationPlaybackCommand.CrossFade && plan.FixedStartSeconds.HasValue)
                animator.CrossFadeInFixedTime(plan.StateHash, plan.TransitionDuration, layer, plan.FixedStartSeconds.Value);
            else if (plan.Command == CharacterAnimationPlaybackCommand.CrossFade)
                animator.CrossFade(plan.StateHash, plan.TransitionDuration, layer, plan.StartNormalizedTime);
            else
                animator.Play(plan.StateHash, layer, plan.StartNormalizedTime);
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
            // 尚未接管的权重无需清理；控制器已被外部替换时也不能写入旧权重所属的层。
            if (animator == null || !ownsLayerWeights || weightOwnerController != animator.runtimeAnimatorController)
                return;
            if (IsLocalLayer(lowerLayer))
                animator.SetLayerWeight(lowerLayer, 0f);
            if (IsLocalLayer(upperLayer))
                animator.SetLayerWeight(upperLayer, 0f);
        }

        public void Release()
        {
            if (animator == null)
                return;
            SetPaused(false);
            if (ownsLayerWeights && weightOwnerController == animator.runtimeAnimatorController)
            {
                if (IsLocalLayer(lowerLayer))
                    animator.SetLayerWeight(lowerLayer, originalLowerWeight);
                if (IsLocalLayer(upperLayer))
                    animator.SetLayerWeight(upperLayer, originalUpperWeight);
            }
            ownsLayerWeights = false;
            weightOwnerController = null;
        }

        private bool IsValidLayer(int layer) => layer >= 0 && layer < animator.layerCount;
        private bool IsLocalLayer(int layer) => IsValidLayer(layer) && layer != fullLayer && lowerLayer != upperLayer;

    }
}
