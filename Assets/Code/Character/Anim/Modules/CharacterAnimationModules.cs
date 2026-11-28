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
        private string reportedError;
        internal CharacterAnimationPlayer Player { get; }
        internal CharacterAnimationComposer Composer { get; }
        internal CharacterStrategySession Strategy { get; private set; }
        internal CharacterAnimationResourceCatalog Resources { get; private set; }
        internal CharacterPlaybackObserver Playback { get; private set; }
        internal CharacterPosePipeline Pose { get; private set; }

        // 依据动画资产和模型绑定建立每个角色独占的播放、选姿与求解模块。
        internal CharacterAnimationModules(Animator animator, CharacterBehaviorAnimator owner,
            CharacterAnimationConfiguration config, bool requirePlayable)
        {
            this.animator = animator;
            this.owner = owner;
            this.config = config;
            definitions = new AnimationBindingDefinitions(config);
            clips = new CharacterClipPlayback(animator, config);
            Player = new CharacterAnimationPlayer(animator, clips);
            Resources = new CharacterAnimationResourceCatalog(clips, config, owner);
            Strategy = new CharacterStrategySession(animator, config, Resources, Resources.ResolveStrategyState);
            if (requirePlayable)
            {
                Resources.RefreshBindings();
                if (!Resources.Ready) throw new System.ArgumentException(Resources.ValidationError);
            }
            Playback = new CharacterPlaybackObserver(animator, Resources, clips);
            Composer = new CharacterAnimationComposer(Resources.Rules, Resources.HasBinding, owner.ActionInstances);
            Composer.UsesStrategyIntents = Strategy.Detail == null;
#if UNITY_EDITOR
            Composer.History = owner.PlaybackHistory;
#endif
            Pose = CreatePose(config);
        }

        // 真实资源失效时立即结束单次实例，持续请求保留，恢复兼容资源后再参与仲裁。
        private void SettlePlaybackBindings()
        {
            if (Resources.Ready)
            {
                reportedError = null;
                Composer.RebindPlayback(Resources);
                return;
            }
            Player.ResetPlayback();
            Composer.LosePlaybackResources();
            if (reportedError == Resources.ValidationError) return;
            reportedError = Resources.ValidationError;
            Debug.LogError(reportedError, owner);
        }

        // 按本次组合创建姿态校正、惯性与肢体求解。
        private CharacterPosePipeline CreatePose(CharacterAnimationConfiguration next) =>
            new CharacterPosePipeline(animator, Player, clips, owner.transform, next.IkChannels, next.WalkableSlope,
                next.Orientation, next.PoseInertia, next.InertiaBones, next, owner.modelDropLimit, Pose, Strategy.Setup);

        // 重绑 Animator 时重新校验选姿与持续动作，标记当前尚无可用资源的需求。
        internal void RefreshPlayback()
        {
            Player.BindPlayback();
            Resources.RefreshBindings();
            Strategy.Detail?.BindClips();
            SettlePlaybackBindings();
            if (!Resources.Ready) return;
            foreach (var key in Composer.ActiveKeys)
                if (!Resources.HasBinding(key))
                    Debug.LogWarning($"当前动画资源缺少动作 {key}，持续请求将在资源恢复后参与选择。", owner);
        }

#if UNITY_EDITOR
        // 按需汇总当前角色的最近判定；没有观测的阶段尚未执行。
        internal string ReadDecisionTrace()
        {
            var text = new System.Text.StringBuilder("动画判定程序（各入口最近一次采样）\n");
            Strategy.Detail?.AppendTrace(text);
            return text.ToString();
        }
#endif

        // 在旧装配仍可用时校验新配置并固定所有受影响的绑定。
        internal AnimationModuleChanges Prepare(CharacterAnimationConfiguration next, bool refreshContents)
        {
            var changes = new AnimationModuleChanges(config, next, definitions, refreshContents, Strategy.Detail == null);
            if (changes.GroundChanged)
            {
                // 临时索引只读取新配置，保留旧图直至工厂、状态句柄和所需骨骼均已解析。
                var previewClips = new CharacterClipPlayback(animator, next);
                var preview = new CharacterAnimationResourceCatalog(previewClips, next, owner);
                changes.Resources = new CharacterAnimationResourceCatalog(clips, next, owner);
                changes.Strategy = new CharacterStrategySession(animator, next, changes.Resources, preview.ResolveStrategyState);
                preview.RequiresGround = changes.Resources.RequiresGround;
                preview.RefreshBindings();
                if (Resources.Ready && !preview.Ready) throw new System.ArgumentException(preview.ValidationError);
            }
            return changes;
        }

        // 按已验证的影响范围更新资源索引、动作绑定及 IK，并保留未受影响的动作实例。
        internal void Reconfigure(CharacterAnimationConfiguration next, AnimationModuleChanges changes, CharacterAnimationContext context)
        {
            foreach (string key in changes.ActionKeys) Composer.WithdrawKey(key);
            if (changes.GroundChanged)
            {
                Player.ResetGround();
                clips.ConfigurePaces(next);
                changes.Strategy.Detail?.ResumeContext(Strategy.Detail, context);
                Strategy.Instance.Reset();
                Resources = changes.Resources;
                Strategy = changes.Strategy;
                Playback = new CharacterPlaybackObserver(animator, Resources, clips);
                Composer.UsesStrategyIntents = Strategy.Detail == null;
                Resources.RefreshBindings();
                Strategy.Detail?.BindClips();
                Composer.RebindRules(Resources);
            }
            else if (changes.ActionKeys.Count != 0)
            {
                clips.ConfigurePaces(next);
                Resources.ReplaceActions(next, owner);
            }

            if (changes.GroundChanged || changes.ActionKeys.Count != 0) SettlePlaybackBindings();
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
