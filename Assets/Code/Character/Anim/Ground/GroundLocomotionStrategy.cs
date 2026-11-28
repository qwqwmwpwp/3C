using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 一个角色的地面基础姿态策略，独立拥有步态、匹配历史和收步试选结果。
    internal sealed class GroundLocomotionStrategy : ICharacterBasePoseStrategy, ICharacterDetailedPose
    {
        private Animator animator;
        private CharacterBasePoseSetup setup;
        private CharacterAnimationIntentConverter intent;
        private CharacterMotionMatcher matcher;
        private CharacterLocomotionPoseSelector poses;
        private CharacterPresentationInput input;
        private CharacterPlaybackObservation seen;
        private CharacterAnimationPlaybackPlan previous;
        private CharacterAnimationComposer composer;
        private CharacterPoseCandidate candidate;
        private GroundMatchHistory trialHistory;
        public CharacterAnimationPlaybackPlan Plan { get; private set; }
        public CharacterAnimationComposition Owners { get; private set; }
        public CharacterAnimationMotion PoseMotion => input.Motion;
        // 先使用驱动为该固定步冻结的地面轨迹。
        public CharacterGroundTrajectory StepTrajectory(in CharacterAnimationMovement frame) =>
            frame.GroundProjection?.Project(frame.FrameNumber, CharacterMatchTrajectory.DefaultWindows) ?? frame.Trajectory;

        // 从独立的步时刻及本策略窗口计算该步的步态预测。
        public CharacterAnimationMotion ProjectStep(in CharacterAnimationMovement frame, in CharacterAnimationMotion motion,
            double time, in CharacterPresentationInput captured)
        {
            var projected = CharacterForecastQuery.Project(frame, motion.Trajectory, captured.Forecast, time,
                captured.ForecastEnabled, matcher.Windows);
            return new CharacterAnimationMotion(motion, projected);
        }

        // 主线程完成地面配置、搜索索引和动作变体绑定。
        public void Bind(Animator animator, CharacterAnimationResourceCatalog resources, CharacterAnimationConfiguration config,
            CharacterBasePoseSetup setup)
        {
            this.animator = animator;
            this.setup = setup;
            intent = new CharacterAnimationIntentConverter(config);
            matcher = new CharacterMotionMatcher(animator, resources, config);
            matcher.Mode = config.Modules.Match ? config.MatchMode : CharacterMatchMode.Rules;
            poses = new CharacterLocomotionPoseSelector(resources, config);
            intent.ResetGait(default);
        }

        // 按固定步推进地面意图和动作移动变体。
        public void AdvanceStep(in CharacterBasePoseInput step)
        {
            intent.ObserveActionVariantMotion(step.RawMotion);
            intent.Receive(step.RawMotion, step.Frame);
        }

        // 提供当前地面或运动覆盖的语义实例。
        public CharacterAnimationRequest GetIntent() => intent.Current;

        // 判断本步是否需要最终发布的轨迹来确定步态。
        public bool NeedsForecast(in CharacterAnimationMotion motion, in CharacterAnimationMovement frame) =>
            intent.MayUsePredictedGait(motion, frame);

        // 资源重装配时把仍获准的覆盖交给新策略实例。
        public void ResumeContext(ICharacterDetailedPose previous, in CharacterAnimationContext context) =>
            intent.ResumeContext((previous as GroundLocomotionStrategy)?.intent, context);

        // 在地面固定步之间响应当前上下文更新。
        public void ObserveContext(in CharacterAnimationContext context) => intent.ObserveMotionOverride(context);

        // 主线程冻结素材时长。
        public void BindClips() => poses.BindClips();

        // 在主线程采集可用于各地面库的骨骼特征和可用性事实。
        public void Capture(CharacterPosePipeline pose, in CharacterFootObservation feet)
        {
            matcher.CaptureCompatibility();
            matcher.Features = pose.ReadFeatures(feet);
        }

        // 每个渲染批次只推进一次地面短暂动作的表现时间。
        public void BeginFrame(in CharacterPresentationInput frame)
        {
            if (frame.Motion.IsValid) intent.AdvancePresentation(frame.DeltaTime);
            else
            {
                intent.ObserveActionVariantMotion(frame.Motion);
                intent.InvalidateMotion();
            }
        }

        // 为同一帧的每次试选重建地面轨迹和部位观测。
        public void Prepare(in CharacterPresentationInput frame, in CharacterAnimationComposition owners,
            in CharacterPlaybackObservation observation, in CharacterAnimationPlaybackPlan last, CharacterAnimationComposer composer)
        {
            var projected = ProjectStep(frame.Frame, frame.Motion, frame.Time, frame);
            input = new CharacterPresentationInput(frame, projected);
            Owners = owners;
            seen = observation.WithFootObservation(intent.Phase == CharacterLocomotionPhase.BrakeLeadIn || intent.Phase == CharacterLocomotionPhase.Stopping);
            previous = last;
            this.composer = composer;
        }

        // 从已提交历史试算地面片段和收步入点，将新历史附在本次候选上。
        public CharacterBasePoseSelection SelectPose(in CharacterBasePoseInput facts)
        {
            var owners = poses.SelectOwners(Owners, seen, previous, intent.UsesMovingActionVariant);
            var committed = matcher.SelectionHistory;
            try
            {
                candidate = poses.EvaluateCandidate(owners, intent, matcher, input, seen, previous);
                trialHistory = matcher.SelectionHistory;
            }
            finally { matcher.SelectionHistory = committed; }
            var next = intent.PreviewRelease(candidate.Release);
            if (next.SemanticKey != intent.Current.SemanticKey || next.InstanceId != intent.Current.InstanceId)
            {
                Owners = composer.Preview(next);
                owners = poses.SelectOwners(Owners, seen, previous, intent.UsesMovingActionVariant);
                candidate = new CharacterPoseCandidate(owners, candidate.Release, candidate.Match);
            }
            Plan = poses.Select(candidate, intent.UsesMovingActionVariant, seen, input.Motion, intent.StopEntryVelocity, previous);
            return new CharacterBasePoseSelection(setup.BoundHandle(Plan.FullBody.StateHash), Plan.FullBody.Start.Phase);
        }

        // 在真实播放求值后采用试选时暂存的收步决定和匹配历史。
        public void CommitSelection(in CharacterBasePoseSelection selection)
        {
            matcher.SelectionHistory = trialHistory;
            intent.ApplyRelease(candidate.Release);
        }

        // 仅断开运动采样与匹配历史，保留已批准的覆盖实例和动作移动变体。
        public void InvalidateMotion()
        {
            intent.InvalidateMotion();
            ClearMatch();
        }

        // 角色状态或时间域已重置，清空对应的步态和运动覆盖。
        public void Reset()
        {
            intent.ResetGait(default);
            intent.ObserveActionVariantMotion(default);
            ClearMatch();
        }

        // 丢弃旧姿态源的匹配历史与试选结果。
        private void ClearMatch()
        {
            matcher.Reset();
            trialHistory = default;
            candidate = default;
        }

#if UNITY_EDITOR
        // 向地面匹配诊断交付本帧候选和实际播放结果。
        public void Publish(in CharacterAnimationDebugSnapshot playback, double time, long step) =>
            matcher.Publish(candidate.Match, Plan, playback, time, step);

        // 为本角色的匹配历史添加连续性边界。
        public void MarkBoundary(string reason) => CharacterMatchDebugCapture.MarkBoundary(animator, reason);

        // 输出当前地面实例持有的最近表达式和匹配诊断。
        public void AppendTrace(System.Text.StringBuilder trace)
        {
            intent.AppendDecisionTrace(trace);
            poses.AppendDecisionTrace(trace);
            matcher.AppendDecisionTrace(trace);
        }
#endif
    }
}
