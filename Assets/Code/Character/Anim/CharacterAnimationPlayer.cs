using UnityEngine;
using UnityEngine.Playables;

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
        private CharacterAnimationPlaybackPlan lastEvaluated;
        private long cursorVersion;
        private readonly CharacterPlaybackFrameBudget budget = new CharacterPlaybackFrameBudget();
        internal CharacterPlaybackFrameStep Remaining => budget.Remaining;

        // 在主线程把本帧普通及非缩放 Animator 时间各登记一次。
        internal CharacterPlaybackFrameStep CaptureFrame(double now, float dt)
        {
            budget.Capture(Time.frameCount, now, dt, clips.LayerBlendDelta(dt));
            return budget.Remaining;
        }

        private readonly CharacterLayerObservation[] seen = new CharacterLayerObservation[4];
        private readonly float[] visibleWeights = new float[4];

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

        // 已播声部按时间先后共同推进；每段图事件返回后重新核对仲裁、资源和批次代次。
        internal (bool NeedsSelection, long PublishedEndVersion) DrainRetired(CharacterAnimationComposer composer,
            CharacterAnimationRequest behavior, double now,
            CharacterPosePipeline pose, CharacterAnimationMotion motion, System.Func<bool> current, ref CharacterAnimationPlaybackPlan committed)
        {
            bool changed = false;
            while (current())
            {
                bool activated = composer.RetireDue(now, behavior);
                changed |= activated;
                var retiring = composer.Retiring;
                if (retiring.Count == 0)
                {
                    if (activated) continue;
                    break;
                }
                changed = true;
                double from = double.PositiveInfinity, to = double.PositiveInfinity;
                for (int i = retiring.Count - 1; i >= 0; i--)
                {
                    var handle = retiring[i];
                    bool available = CanResume(handle);
                    // 角色预算已结算时，仍允许给已显示的声部一次零步长的真实末端采样。
                    bool needsEndSample = handle.Clock.NaturalEnd <= handle.Clock.StopAt && !handle.EndSampleEvaluated;
                    if (!available || handle.Clock.End <= handle.LastEvaluatedAt && !needsEndSample)
                    {
                        composer.FinishRetired(handle, available || !handle.HasStarted);
                        continue;
                    }
                    from = System.Math.Min(from, handle.LastEvaluatedAt);
                    to = System.Math.Min(to, handle.Clock.End);
                }
                if (retiring.Count == 0) continue;
                to = System.Math.Min(to, composer.NextActivation);
                foreach (var handle in retiring)
                    if (handle.LastEvaluatedAt > from) to = System.Math.Min(to, handle.LastEvaluatedAt);
                if (budget.NextFrameEnd > from) to = System.Math.Min(to, budget.NextFrameEnd);
                var step = budget.Until(to);
                if (to > from && step.End <= from) break; // 等下帧提供剩余预算。
                long before = cursorVersion;
                var plan = new CharacterAnimationPlaybackPlan(lastEvaluated, seen, clips, step);
                clips.Apply(plan, step, visibleWeights);
                if (!clips.Graph.IsValid())
                {
                    while (retiring.Count > 0) composer.FinishRetired(retiring[retiring.Count - 1], false);
                    break;
                }
                var owners = new CharacterAnimationComposition(plan.FullBody.Demand, plan.UpperOwner.Demand, plan.LowerOwner.Demand);
                pose.Prepare(owners, motion, plan, step);
                EvaluatePose(plan, step, composer, now);
                if (!current()) break;
                committed = plan;
                if (cursorVersion == before) break; // 原生时钟未推进时，本帧不再消耗同一预算。
                // 新请求及撤销已记下本段真实时刻，下一轮重新计算最早截止。
            }
            return (changed, composer.PublishedEndVersion);
        }

        // 核对上次真实提交的句柄、素材与当前播放库；从未显示的请求没有可补采的区间。
        private bool CanResume(CharacterActionPlayback handle)
        {
            if (!handle.HasStarted || !handle.Cursor.HasValue || handle.StopSource == CharacterPlaybackEndSource.ResourceUnavailable)
                return false;
            foreach (var layer in seen)
                if (layer.Ready && layer.Destination.PlaybackId == handle.PlaybackId)
                    return clips.MatchesMaterial(layer.Destination);
            return false;
        }

        // 读取四个原生混合源的求值结果，区间内未推进的声部保持自己的事件游标。
        private void ObserveSegment(in CharacterAnimationPlaybackPlan plan, double at)
        {
            for (int i = 0; i < seen.Length; i++)
            {
                var source = (CharacterAnimationSource)i;
                seen[i] = clips.Observe(source);
                visibleWeights[i] = clips.LayerWeight(source);
            }
            Record(plan.Overlay, at);
            Record(plan.LowerBody, at);
            Record(plan.UpperBody, at);
        }

        // 只有真实求值且素材、实例均一致的声部才能推进对应句柄。
        private void Record(in CharacterAnimationLayerPlan layer, double at)
        {
            var playback = layer.Demand.Playback;
            if (playback == null || !playback.HasStarted || playback.EndSource != default) return;
            double time = playback.Clock.Valid ? System.Math.Min(at, playback.Clock.End) : at;
            var observation = seen[(int)layer.Demand.Source];
            if (observation.Ready && observation.Destination.fullPathHash == layer.StateHash)
            {
                var sample = layer.Demand.Timing.Sample;
                double previousAt = playback.LastEvaluatedAt;
                bool hadEnd = playback.EndSampleEvaluated;
                playback.RecordCursor(observation.Destination, time, sample.Valid && sample.Time >= playback.Clock.NaturalEnd);
                if (playback.LastEvaluatedAt > previousAt || playback.EndSampleEvaluated != hadEnd) cursorVersion++;
                if (playback.Clock.Valid && playback.Cursor.HasValue) playback.EntryCoverage ??= layer.Demand.EntryCoverage;
            }
        }

        internal bool Evaluating => clips.Evaluating;

        // 事件回调触发硬重置时，延迟图与 IK 的释放直至当前求值退出。
        internal void AfterEvaluation(System.Action change) => clips.AfterEvaluation(change);

        // 仅给实际下发播放指令的部位发布单次动作开始回执。
        internal void PublishOneShotStarts(in CharacterAnimationPlaybackPlan plan, in CharacterAnimationPlaybackEmission emission,
            CharacterAnimationComposer composer)
        {
            if (emission.Overlay) composer.Start(plan.Overlay.Demand.Playback);
            if (emission.LowerBody) composer.Start(plan.LowerBody.Demand.Playback);
            if (emission.UpperBody) composer.Start(plan.UpperBody.Demand.Playback);
        }

        // 推进各层到计划指定的状态并返回本帧实际开始的播放。
        internal CharacterAnimationPlaybackEmission ApplyPlan(in CharacterAnimationPlaybackPlan plan, in CharacterPlaybackFrameStep step) =>
            clips.Apply(plan, step);

        // 丢弃旧装配的基础声部与补播快照，动作层的回执和游标仍然有效。
        internal void ResetGround()
        {
            clips.ResetGround();
            lastEvaluated = lastEvaluated.WithoutGround();
            seen[(int)CharacterAnimationSource.Baseline] = default;
        }

        // 通知片段图释放旧动作声部，但保留已装配资源和图本身。
        internal void ClearMotionSources(bool resetTime)
        {
            clips.ClearMotionSources();
            if (resetTime) ClearFrameHistory();
        }

        // 已求值的基础片段仍对任一身体分区有贡献时，才允许提交策略历史。
        internal bool UsedBasePose(int state)
        {
            var baseline = seen[(int)CharacterAnimationSource.Baseline];
            return state != 0 && baseline.Destination.fullPathHash == state && baseline.BlendProgress > 0f &&
                visibleWeights[(int)CharacterAnimationSource.FullBodyOverlay] < 1f &&
                (visibleWeights[(int)CharacterAnimationSource.UpperBody] < 1f ||
                visibleWeights[(int)CharacterAnimationSource.LowerBody] < 1f);
        }

        // 在各层权重和 IK 输入均就绪后求值本帧姿态。
        internal bool EvaluatePose(CharacterAnimationPlaybackPlan plan, CharacterPlaybackFrameStep step,
            CharacterAnimationComposer composer, double frameEnd)
        {
            bool evaluated = false;
            composer.BeginSegment(step.End);
            try
            {
                clips.Evaluate(() =>
                {
                    evaluated = true;
                    budget.Commit(step);
                    lastEvaluated = plan;
                    ObserveSegment(plan, step.End);
                });
            }
            finally { composer.EndSegment(frameEnd); }
            return evaluated;
        }

#if UNITY_EDITOR
        // 采集已求值片段的实际权重供编辑器发布。
        internal CharacterDebugBodyMix CaptureDebugMix() => new CharacterDebugBodyMix(clips);
#endif

        // 停用 Animator 时释放当前播放图，保留资源绑定供恢复使用。
        public void CloseGraph() => clips.Close();

        // 角色硬重置同时清除旧角色的已消费时刻和分层快照。
        internal void ResetPlayback()
        {
            clips.Close();
            ClearFrameHistory();
        }

        // 角色绝对时间域变化时清空图预算、上次实际姿势和各声部游标。
        private void ClearFrameHistory()
        {
            budget.Reset();
            lastEvaluated = default;
            System.Array.Clear(seen, 0, seen.Length);
            System.Array.Clear(visibleWeights, 0, visibleWeights.Length);
        }

        // 释放图并恢复由暂停暂存的 Animator 速度。
        public void Release()
        {
            clips.Close();
            if (animator) SetPaused(false);
        }
    }
}
