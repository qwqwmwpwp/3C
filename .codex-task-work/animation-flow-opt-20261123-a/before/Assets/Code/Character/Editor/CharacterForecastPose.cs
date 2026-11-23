using UnityEditor.Animations;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>为终点挑一个基础片段。播放中的覆盖片段优先，其次使用同一套状态路径、行为映射与招式定义。</summary>
    internal sealed class CharacterForecastPose
    {
        private readonly CharacterBehaviorAnimator owner;
        private readonly CharacterAnimationConfiguration config;
        private readonly Animator source;
        private readonly CharacterForecastSettings settings;
        private string cachedPath;
        private AnimationClip cachedFallback, cachedClip, last;
        private RuntimeAnimatorController cachedController;
        private bool cached;
        private double lastClock, clipTime;

        internal CharacterForecastPose(CharacterBehaviorAnimator owner, CharacterBehaviorDriver driver)
        {
            this.owner = owner;
            source = owner.PreviewAnimator;
            config = owner.Configuration(driver);
            settings = driver.ForecastSettings;
        }

        internal AnimationClip Sample(in CharacterForecastSample pose, double clock, out double seconds)
        {
            seconds = 0d;
            string path = null;
            float rate = 1f;
            bool attack = pose.Attack != CharacterAttackMove.None && !pose.ExternallyControlled;
            AnimationClip fallback = pose.ExternallyControlled ? settings.KnockbackPreview : null;
            if (attack)
            {
                path = AttackPath(pose.Attack);
                fallback = CharacterAttackComboSettings.Definition(pose.Attack, config.AttackSettings, config.AttackSettings?.Combo)?.Clip;
            }
            else if (!pose.ExternallyControlled)
            {
                float speed = Vector3.ProjectOnPlane(pose.Velocity, Vector3.up).magnitude;
                if ((pose.Behaviors & CharacterBehaviorId.Dash) != 0) path = config.DashState;
                else if (speed <= Mathf.Max(0.03f, config.IdleEnterSpeed)) path = config.IdleState;
                else
                {
                    bool sprint = pose.Gait == CharacterMoveGait.Sprint && speed >= config.SprintExitSpeed;
                    bool run = speed >= config.RunExitSpeed;
                    path = sprint ? config.SprintState : run ? config.MoveState : config.WalkState;
                    if (string.IsNullOrEmpty(path)) path = config.MoveState;
                    float nominal = sprint ? config.SprintSpeed : run ? config.RunSpeed : config.WalkSpeed;
                    rate = speed / Mathf.Max(0.01f, nominal);
                }
            }
            var clip = Resolve(path, fallback);
            if (clip != last) clipTime = 0d;
            else clipTime += System.Math.Max(0d, clock - lastClock) * rate;
            lastClock = clock;
            last = clip;
            if (clip && clip.length > 0f)
            {
                seconds = attack ? Mathf.Clamp01(pose.AttackProgress) * clip.length :
                    clip.isLooping ? clipTime % clip.length : System.Math.Min(clipTime, clip.length);
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
