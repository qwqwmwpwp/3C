using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>按角色的状态路径、行为映射与招式定义，为预测终点解析基础片段或混合动作。</summary>
    internal sealed class CharacterForecastPose
    {
        private readonly CharacterBehaviorAnimator owner;
        private CharacterAnimationConfiguration config;
        private readonly CharacterForecastSettings settings;
        private string cachedPath;
        private AnimationClip last;
        private CharacterClipState[] cachedStates;
        private CharacterClipState cachedGroupState, lastGroupState;
        private double lastClock, clipTime;

        // 取得角色的有效配置，供编辑状态和播放状态共用。
        internal CharacterForecastPose(CharacterBehaviorAnimator owner, CharacterBehaviorDriver driver)
        {
            this.owner = owner;
            try { config = owner.ActiveConfiguration ?? owner.Configuration(driver); }
            catch (System.ArgumentException error) { Debug.LogWarning(error.Message, owner); }
            settings = driver.ForecastSettings;
        }

        // 按预测输入选择已烘焙状态并计算预览采样时间。
        internal AnimationClip Sample(in CharacterForecastSample pose, double clock, out double seconds, out CharacterClipState groupState)
        {
            seconds = 0d;
            groupState = null;
            var active = owner.ActiveConfiguration;
            if (active != null) config = active;
            if (config == null) return null;
            string path = null;
            float rate = 1f;
            bool attack = pose.Attack != CharacterAttackMove.None && !pose.ExternallyControlled;
            AnimationClip fallback = pose.ExternallyControlled ? settings.KnockbackPreview : null;
            if (attack)
            {
                path = AttackPath(pose.Attack);
                attack = path != null;
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
            groupState = FindState(path);
            AnimationClip clip = groupState?.Clip ? groupState.Clip : fallback;
            if (groupState != null) rate *= groupState.Speed;
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

        // 查找当前预览使用的烘焙状态，片段或库重烘焙时更新缓存。
        private CharacterClipState FindState(string path)
        {
            var states = config.PlaybackLibrary ? config.PlaybackLibrary.Playback?.States : null;
            if (states == cachedStates && path == cachedPath) return cachedGroupState;
            cachedStates = states;
            cachedPath = path;
            cachedGroupState = null;
            if (states != null && !string.IsNullOrEmpty(path))
                foreach (var state in states)
                    if (state != null && state.Path == path) { cachedGroupState = state; break; }
            return cachedGroupState;
        }
    }
}
