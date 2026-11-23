using UnityEditor.Animations;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>按角色的状态路径、行为映射与招式定义，为预测终点解析基础片段或混合动作。</summary>
    internal sealed class CharacterForecastPose
    {
        private readonly CharacterBehaviorAnimator owner;
        private CharacterAnimationConfiguration config;
        private readonly Animator source;
        private readonly CharacterForecastSettings settings;
        private string cachedPath;
        private AnimationClip cachedFallback, cachedClip, last;
        private CharacterClipState cachedGroupState, lastGroupState;
        private RuntimeAnimatorController cachedController;
        private bool cached;
        private double lastClock, clipTime;

        internal CharacterForecastPose(CharacterBehaviorAnimator owner, CharacterBehaviorDriver driver)
        {
            this.owner = owner;
            source = owner.PreviewAnimator;
            try { config = owner.ActiveConfiguration ?? owner.Configuration(driver); }
            catch (System.ArgumentException error) { Debug.LogWarning(error.Message, owner); }
            settings = driver.ForecastSettings;
        }

        internal AnimationClip Sample(in CharacterForecastSample pose, double clock, out double seconds, out CharacterClipState groupState)
        {
            seconds = 0d;
            groupState = null;
            var active = owner.ActiveConfiguration;
            if (active != null && !ReferenceEquals(config, active))
            {
                config = active;
                cached = false;
            }
            if (config == null) return null;
            string path = null;
            float rate = 1f;
            bool attack = pose.Attack != CharacterAttackMove.None && !pose.ExternallyControlled;
            AnimationClip fallback = pose.ExternallyControlled ? settings.KnockbackPreview : null;
            if (attack)
            {
                path = AttackPath(pose.Attack);
                attack = config.Modules == null || path != null;
                if (attack) fallback = config.AttackSettings?.Moves?.Move(pose.Attack)?.Clip;
            }
            if (!attack && !pose.ExternallyControlled)
            {
                float speed = Vector3.ProjectOnPlane(pose.Velocity, Vector3.up).magnitude;
                if (!config.HasGait) path = config.IdleState;
                else if ((pose.Behaviors & CharacterBehaviorId.Dash) != 0) path = config.DashState;
                else if (speed <= Mathf.Max(0.03f, config.IdleEnterSpeed)) path = config.IdleState;
                else
                {
                    bool sprint = pose.Gait == CharacterMoveGait.Sprint && speed >= config.SprintExitSpeed;
                    bool run = pose.Gait != CharacterMoveGait.Walk && speed > config.RunEnterSpeed;
                    path = sprint ? config.SprintState : run ? config.MoveState : config.WalkState;
                    if (string.IsNullOrEmpty(path)) path = config.MoveState;
                    float nominal = sprint ? config.SprintSpeed : run ? config.RunSpeed : config.WalkSpeed;
                    rate = speed / Mathf.Max(0.01f, nominal);
                }
            }
            var library = config.PlaybackLibrary ? config.PlaybackLibrary.Playback : null;
            AnimationClip clip;
            if (library?.FromGroups == true)
            {
                if (!cached || cachedPath != path)
                {
                    cachedGroupState = null;
                    if (!string.IsNullOrEmpty(path) && library.States != null)
                        foreach (var state in library.States)
                            if (state != null && state.Path == path) { cachedGroupState = state; break; }
                    cachedPath = path;
                    cached = true;
                }
                groupState = cachedGroupState;
                clip = groupState?.Clip ? groupState.Clip : fallback;
                if (groupState != null) rate *= groupState.Speed;
            }
            else clip = Resolve(path, fallback);
            if (clip != last || groupState != lastGroupState) clipTime = 0d;
            else clipTime += System.Math.Max(0d, clock - lastClock) * rate;
            lastClock = clock;
            last = clip;
            lastGroupState = groupState;
            if (clip)
            {
                var blend = groupState?.Blend ?? default;
                float length = blend.Second ? blend.Seconds : clip.length;
                bool loop = blend.Second ? blend.Loop : clip.isLooping;
                if (length > 0f) seconds = attack ? Mathf.Clamp01(pose.AttackProgress) * length :
                    loop ? clipTime % length : System.Math.Min(clipTime, length);
            }
            return clip;
        }

        private string AttackPath(CharacterAttackMove move)
        {
            if (config.BehaviorAnimations == null || config.Actions == null) return null;
            foreach (var binding in config.BehaviorAnimations)
            {
                if (binding == null || binding.BehaviorId != CharacterBehaviorId.Attack || (binding.AttackMove != CharacterAttackMove.None && binding.AttackMove != move)) continue;
                foreach (var action in config.Actions)
                    if (action != null && action.Key == binding.EntryAction) return action.StateName;
            }
            return null;
        }

        private AnimationClip Resolve(string path, AnimationClip fallback)
        {
            var controller = source ? source.runtimeAnimatorController : null;
            if (!string.IsNullOrEmpty(path))
            {
                var live = owner.ActivePreviewClip(path);
                if (live) return live;
            }
            if (cached && path == cachedPath && controller == cachedController && fallback == cachedFallback) return cachedClip;
            cached = true;
            cachedPath = path;
            cachedController = controller;
            cachedFallback = fallback;
            AnimationClip clip = null;
            if (!string.IsNullOrEmpty(path))
            {
                var states = config.PlaybackLibrary ? config.PlaybackLibrary.Playback?.States : null;
                if (states != null)
                    foreach (var state in states)
                        if (state != null && state.Path == path) { clip = state.Clip; break; }
            }
            var overrides = controller as AnimatorOverrideController;
            if (!clip && !string.IsNullOrEmpty(path))
            {
                var catalog = (overrides ? overrides.runtimeAnimatorController : controller) as AnimatorController;
                if (catalog)
                    foreach (var layer in catalog.layers)
                    {
                        if (!path.StartsWith(layer.name + ".", System.StringComparison.Ordinal)) continue;
                        clip = FindClip(layer.stateMachine, path.Substring(layer.name.Length + 1));
                        break;
                    }
            }
            if (!clip) clip = fallback;
            if (clip && overrides)
            {
                var replacement = overrides[clip];
                if (replacement) clip = replacement;
            }
            return cachedClip = clip;
        }

        private static AnimationClip FindClip(AnimatorStateMachine machine, string path)
        {
            foreach (var state in machine.states)
                if (state.state.name == path) return state.state.motion as AnimationClip;
            foreach (var child in machine.stateMachines)
            {
                string prefix = child.stateMachine.name + ".";
                if (path.StartsWith(prefix, System.StringComparison.Ordinal))
                    return FindClip(child.stateMachine, path.Substring(prefix.Length));
            }
            return null;
        }
    }
}
