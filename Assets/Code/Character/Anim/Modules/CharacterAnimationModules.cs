using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>一个角色的模块实例。重新装配时保留仍在使用的动作、步态时钟和播放图。</summary>
    internal sealed class CharacterAnimationModules
    {
        private readonly Animator animator;
        private readonly CharacterBehaviorAnimator owner;
        private readonly CharacterClipPlayback clips;
        private CharacterAnimationConfiguration config;
        private AnimationBindingDefinitions definitions;
        internal CharacterAnimationPlayer Player { get; }
        internal CharacterAnimationComposer Composer { get; }
        internal CharacterAnimationIntentConverter Intent { get; }
        internal CharacterAnimationResourceCatalog Resources { get; private set; }
        internal CharacterMotionMatcher Matcher { get; private set; }
        internal CharacterPlaybackObserver Playback { get; private set; }
        internal CharacterLocomotionPoseSelector Poses { get; private set; }
        internal CharacterBehaviorAnimationBindings Bindings { get; }
        internal CharacterPosePipeline Pose { get; private set; }

        // 以当前行为许可初始化动作模块，物理观测由流水线单独接收。
        internal CharacterAnimationModules(Animator animator, CharacterBehaviorAnimator owner,
            CharacterAnimationConfiguration config, CharacterBehaviorObservation seed, int stateVersion)
        {
            this.animator = animator;
            this.owner = owner;
            this.config = config;
            definitions = new AnimationBindingDefinitions(config);
            clips = new CharacterClipPlayback(animator, config);
            Player = new CharacterAnimationPlayer(animator, clips);
            BindGround(config);
            Intent = new CharacterAnimationIntentConverter(config);
            Composer = new CharacterAnimationComposer(Resources.Rules, Resources.HasBinding);
#if UNITY_EDITOR
            Composer.Trace = owner.ActionTrace;
#endif
            Bindings = new CharacterBehaviorAnimationBindings(config, Composer, Resources, owner) { StateVersion = stateVersion };
            if (seed.FrameNumber != 0) Bindings.Observe(seed, seed: true);
            Pose = CreatePose(config);
        }

        // 按本次组合创建姿态校正、惯性与肢体求解。
        private CharacterPosePipeline CreatePose(CharacterAnimationConfiguration next) =>
            new CharacterPosePipeline(animator, Player, clips, owner.transform, next.IkChannels, next.WalkableSlope,
                next.Orientation, next.PoseInertia, next.InertiaBones, next, owner.modelDropLimit, Pose);

        // 在同一个播放图上装配地面资源、观测与选姿。
        private void BindGround(CharacterAnimationConfiguration next)
        {
            Resources = new CharacterAnimationResourceCatalog(clips, next, owner);
            Matcher = new CharacterMotionMatcher(animator, Resources, next);
            Playback = new CharacterPlaybackObserver(animator, Resources, clips);
            Poses = new CharacterLocomotionPoseSelector(Resources, next);
        }

        // 重绑 Animator 时重新校验选姿与持续动作，标记当前尚无可用资源的需求。
        internal void RefreshPlayback()
        {
            Player.BindPlayback();
            Resources.RefreshBindings();
            Poses.BindClips();
            Bindings.RebindGrantedEntries();
            if (!Resources.Ready) return;
            Composer.RebindPlayback(Resources);
            foreach (var key in Composer.ActiveKeys)
                if (!Resources.HasBinding(key))
                    Debug.LogWarning($"当前动画资源缺少动作 {key}，持续请求将在资源恢复后参与选择。", owner);
        }

#if UNITY_EDITOR
        // 按需汇总当前角色的最近判定；没有观测的阶段尚未执行。
        internal string ReadDecisionTrace()
        {
            var text = new System.Text.StringBuilder("动画判定程序（各入口最近一次采样）\n");
            Intent.AppendDecisionTrace(text);
            Poses.AppendDecisionTrace(text);
            Matcher.AppendDecisionTrace(text);
            return text.ToString();
        }
#endif

        // 在旧装配仍可用时校验新配置并固定所有受影响的绑定。
        internal AnimationModuleChanges Prepare(CharacterAnimationConfiguration next, bool refreshContents) =>
            new AnimationModuleChanges(config, next, definitions, refreshContents);

        // 按已验证的影响范围更新资源索引、动作绑定及 IK，并保留未受影响的动作实例。
        internal void Reconfigure(CharacterAnimationConfiguration next, AnimationModuleChanges changes, CharacterBehaviorObservation seed)
        {
            foreach (string key in changes.ActionKeys) Composer.WithdrawKey(key);
            if (changes.GroundChanged)
            {
                Player.ResetGround();
                clips.ConfigurePaces(next);
                BindGround(next);
                Resources.RefreshBindings();
                Poses.BindClips();
                Composer.RebindRules(Resources);
                Intent.Configure(next);
                if (changes.ResetGait) Intent.ResetGait(seed.Behavior);
            }
            else if (changes.ActionKeys.Count != 0 || changes.MappingsChanged)
                Resources.ReplaceActions(next, owner);

            Bindings.Reconfigure(next, Resources, seed, changes);
            if (changes.IkChanged)
            {
                var previous = Pose;
                Pose = CreatePose(next);
                Pose.TakeOver(previous);
            }
            Resources.Transitions = next.Transitions;
            definitions = changes.Definitions;
            config = next;
        }
    }
}
