using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>记录本帧哪些层实际下发了播放命令；保持、未选中及不可用的层没有播放起点。</summary>
    internal readonly struct CharacterAnimationPlaybackEmission
    {
        public bool FullBody { get; }
        public bool LowerBody { get; }
        public bool UpperBody { get; }
        public bool Overlay { get; }

        // 查询指定混合源本帧是否实际发出播放指令。
        public bool For(CharacterAnimationSource source) => source switch
        {
            CharacterAnimationSource.Baseline => FullBody,
            CharacterAnimationSource.UpperBody => UpperBody,
            CharacterAnimationSource.LowerBody => LowerBody,
            _ => Overlay
        };

        public CharacterAnimationPlaybackEmission(bool fullBody, bool lowerBody, bool upperBody, bool overlay)
        {
            FullBody = fullBody;
            LowerBody = lowerBody;
            UpperBody = upperBody;
            Overlay = overlay;
        }
    }

    /// <summary>管理直接片段图的绑定和暂停状态，根据实际播放观测更新单次动作回执。</summary>
    internal sealed class CharacterAnimationPlayer
    {
        private readonly Animator animator;
        private readonly CharacterClipPlayback clips;
        private Avatar boundAvatar;
        private bool bound, boundWithController;
        private bool paused;
        private float speedBeforePause;
        private string oneShotWarning;

        public bool NeedsRebind => !bound || animator && (boundAvatar != animator.avatar ||
            boundWithController != (animator.runtimeAnimatorController != null));

        // 接管本角色片段图的播放、求值和绑定生命周期。
        public CharacterAnimationPlayer(Animator animator, CharacterClipPlayback clips)
        {
            this.animator = animator;
            this.clips = clips;
        }

        // 暂停期间冻结片段时钟，恢复时重用暂停前的 Animator 速度。
        public void SetPaused(bool value)
        {
            if (paused == value) return;
            paused = value;
            if (paused)
            {
                speedBeforePause = animator.speed;
                animator.speed = 0f;
            }
            else animator.speed = speedBeforePause;
        }

        // 更新资源绑定基准；Avatar 或 Animator 上的控制器占用变化时会重建播放图。
        public void BindPlayback()
        {
            clips.Close();
            boundAvatar = animator ? animator.avatar : null;
            boundWithController = animator && animator.runtimeAnimatorController;
            bound = true;
        }

        // 按实际播放顺序核对仍获选的单次动作，并把终态交给仲裁器随播放提交。
        internal bool ReconcileOneShots(in CharacterAnimationPlaybackPlan previous, in CharacterPlaybackObservation seen,
            in CharacterAnimationComposition selected, CharacterAnimationComposer composer)
        {
            if (previous.UpperBody.Demand.Playback == null &&
                previous.LowerBody.Demand.Playback == null && previous.Overlay.Demand.Playback == null) return false;
            bool changed = InspectOneShot(previous.Overlay, seen.Overlay, selected.Overlay, composer);
            changed |= InspectOneShot(previous.LowerBody, seen.Lower, selected.LowerBody, composer);
            changed |= InspectOneShot(previous.UpperBody, seen.Upper, selected.UpperBody, composer);
            return changed;
        }

        // 用已提交动作的观测确认完成或打断，并记录误绑循环片段的诊断。
        private bool InspectOneShot(in CharacterAnimationLayerPlan previous, in CharacterLayerObservation layer,
            CharacterAnimationDemand selected, CharacterAnimationComposer composer)
        {
            var playback = previous.Demand.Playback;
            if (playback == null || playback.Status != CharacterActionPlaybackStatus.Started ||
                selected.Playback != playback) return false;
            var status = playback.Observe(previous.StateHash, layer);
            if (status == CharacterActionPlaybackStatus.Started) return false;
            if (layer.Destination.fullPathHash == previous.StateHash && layer.Destination.loop)
                oneShotWarning = $"单次动作 {playback.Key} 绑定了循环状态 {previous.StateName}，已中止。";
            var source = status == CharacterActionPlaybackStatus.Completed ? CharacterPlaybackEndSource.ClipFinished :
                layer.Destination.loop && layer.Destination.fullPathHash == previous.StateHash
                    ? CharacterPlaybackEndSource.ResourceUnavailable : CharacterPlaybackEndSource.ArbitrationLost;
            return composer.End(playback.Key, playback.InstanceId, status, source);
        }

        // 仅给实际下发播放指令的部位发布单次动作开始回执。
        internal void PublishOneShotStarts(in CharacterAnimationPlaybackPlan plan, in CharacterAnimationPlaybackEmission emission,
            CharacterAnimationComposer composer)
        {
            if (emission.Overlay) composer.Start(plan.Overlay.Demand.Playback);
            if (emission.LowerBody) composer.Start(plan.LowerBody.Demand.Playback);
            if (emission.UpperBody) composer.Start(plan.UpperBody.Demand.Playback);
        }

        // 在播放提交结束后报告本次观测到的循环绑定错误。
        internal void FlushWarnings(Object context)
        {
            if (oneShotWarning == null) return;
            Debug.LogWarning(oneShotWarning, context);
            oneShotWarning = null;
        }

        // 推进各层到计划指定的状态并返回本帧实际开始的播放。
        internal CharacterAnimationPlaybackEmission ApplyPlan(in CharacterAnimationPlaybackPlan plan, float deltaTime) =>
            clips.Apply(plan, deltaTime);

        // 通知片段图释放旧动作声部，但保留已装配资源和图本身。
        internal void ClearMotionSources() => clips.ClearMotionSources();

        // 在各层权重和 IK 输入均就绪后求值本帧姿态。
        internal void EvaluatePose() => clips.Evaluate();

#if UNITY_EDITOR
        // 采集已求值片段的实际权重供编辑器发布。
        internal CharacterDebugBodyMix CaptureDebugMix() => new CharacterDebugBodyMix(clips);
#endif

        // 停用 Animator 时释放当前播放图，保留资源绑定供恢复使用。
        public void CloseGraph() => clips.Close();

        // 释放图并恢复由暂停暂存的 Animator 速度。
        public void Release()
        {
            clips.Close();
            if (animator) SetPaused(false);
        }
    }
}
