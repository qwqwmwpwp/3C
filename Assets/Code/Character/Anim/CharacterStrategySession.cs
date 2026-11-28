using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 内置地面扩展可利用既有的匹配、调速和诊断数据；普通策略只需五项公共操作。
    internal interface ICharacterDetailedPose
    {
        // 在主线程绑定该角色的地面算法和配置。
        void Bind(Animator animator, CharacterAnimationResourceCatalog resources, CharacterAnimationConfiguration config, CharacterBasePoseSetup setup);
        // 在主线程冻结足部特征及资源兼容性。
        void Capture(CharacterPosePipeline pose, in CharacterFootObservation feet);
        // 在主线程刷新可播放片段的长度。
        void BindClips();
        // 判断固定步是否需要等待最终预测。
        bool NeedsForecast(in CharacterAnimationMotion motion, in CharacterAnimationMovement frame);
        // 读取该步的专用地面投影，其他策略始终直接收到运动事实。
        CharacterGroundTrajectory StepTrajectory(in CharacterAnimationMovement frame);
        // 为需要预测的固定步独立取用其原始结算时刻。
        CharacterAnimationMotion ProjectStep(in CharacterAnimationMovement frame, in CharacterAnimationMotion motion,
            double time, in CharacterPresentationInput captured);
        // 在同帧重选之前推进一次表现时钟。
        void BeginFrame(in CharacterPresentationInput input);
        // 清除地面采样历史，保留已有运动覆盖及其语义实例。
        void InvalidateMotion();
        // 装配新资源后接续仍获准的覆盖实例。
        void ResumeContext(ICharacterDetailedPose previous, in CharacterAnimationContext context);
        // 应用本帧最新的运动覆盖语义。
        void ObserveContext(in CharacterAnimationContext context);
        // 提供地面候选所需的既有动作仲裁、历史和姿态观测。
        void Prepare(in CharacterPresentationInput input, in CharacterAnimationComposition owners,
            in CharacterPlaybackObservation seen, in CharacterAnimationPlaybackPlan previous, CharacterAnimationComposer composer);
        CharacterAnimationPlaybackPlan Plan { get; }
        CharacterAnimationComposition Owners { get; }
        CharacterAnimationMotion PoseMotion { get; }
#if UNITY_EDITOR
        // 发布真实提交的地面匹配观测。
        void Publish(in CharacterAnimationDebugSnapshot playback, double time, long step);
        // 记录连续性中断的诊断边界。
        void MarkBoundary(string reason);
        // 汇总已执行的地面判定。
        void AppendTrace(System.Text.StringBuilder text);
#endif
    }

    // 保存本角色的策略实例，并将可重复试选的结果与实际播放提交分开。
    internal sealed class CharacterStrategySession
    {
        internal readonly ICharacterBasePoseStrategy Instance;
        internal readonly CharacterBasePoseSetup Setup;
        internal readonly ICharacterDetailedPose Detail;
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly CharacterActionLayerPlanner actions;
        private readonly CharacterAnimationBinding fallback;
        internal CharacterAnimationComposition Owners { get; private set; }
        internal CharacterAnimationPlaybackPlan Plan { get; private set; }
        internal CharacterBasePoseSelection Selection { get; private set; }
        internal CharacterAnimationMotion PoseMotion { get; private set; }

        // 调用资产工厂并在正式换装前验证本角色的资源声明。
        internal CharacterStrategySession(Animator animator, CharacterAnimationConfiguration config,
            CharacterAnimationResourceCatalog resources, Func<string, int> resolve)
        {
            this.resources = resources;
            actions = new CharacterActionLayerPlanner(resources);
            Setup = new CharacterBasePoseSetup(resolve);
            var library = config.PlaybackLibrary ? config.PlaybackLibrary.Playback : null;
            string path = config.IdleState;
            int hash = resolve(path);
            if (hash == 0 && library?.Layers?.Length > 0)
            {
                path = library.Layers[0]?.DefaultState;
                hash = resolve(path);
            }
            if (hash == 0 && library?.States != null)
            {
                foreach (var state in library.States)
                {
                    if (state == null || state.Layer != 0 || (hash = resolve(state.Path)) == 0) continue;
                    path = state.Path;
                    break;
                }
            }
            fallback = new CharacterAnimationBinding(hash, path);
            try
            {
                Instance = config.PoseStrategy.CreateInstance(Setup) ?? throw new ArgumentException("基础姿态策略没有创建运行实例。");
                Detail = Instance as ICharacterDetailedPose;
                Detail?.Bind(animator, resources, config, Setup);
                Setup.Seal();
            }
            catch (Exception error)
            {
                throw new ArgumentException($"基础姿态策略装配失败：{error.Message}", error);
            }
            resources.RequiresGround = Detail != null;
        }

        // 按本次仲裁向策略公开上下身占用，并将试选结果翻译为播放计划。
        internal void Select(in CharacterPresentationInput input, in CharacterPlaybackObservation seen,
            in CharacterAnimationPlaybackPlan previous, CharacterAnimationComposer composer)
        {
            Owners = composer.Preview(Instance.GetIntent());
            bool moving = input.Motion.WorldVelocity.sqrMagnitude > 0.01f;
            var upper = actions.Select(Owners.UpperBody, previous.UpperBody, moving);
            var lower = actions.Select(Owners.LowerBody, previous.LowerBody, moving);
            var overlay = actions.Select(Owners.Overlay, previous.Overlay, moving);
            float shared = resources.ProjectLayerWeight(CharacterAnimationSource.FullBodyOverlay, overlay);
            float arms = resources.ProjectLayerWeight(CharacterAnimationSource.UpperBody, upper);
            float legs = resources.ProjectLayerWeight(CharacterAnimationSource.LowerBody, lower);
            var occupancy = new CharacterBasePoseOccupancy(Owners.UpperOwner.Key, Owners.LowerOwner.Key,
                shared + (1f - shared) * arms, shared + (1f - shared) * legs);
            var facts = new CharacterBasePoseInput(input.Frame, input.Context, input.Motion, input.Trajectory, input.Time,
                input.DeltaTime, seen, occupancy, input.Bones);
            Detail?.Prepare(input, Owners, seen, previous, composer);
            Selection = Instance.SelectPose(facts);
            if (Detail != null)
            {
                Plan = Detail.Plan;
                Owners = Detail.Owners;
                PoseMotion = Detail.PoseMotion;
                return;
            }
            PoseMotion = input.Motion;
            var full = SelectBase(previous.FullBody);
            Plan = new CharacterAnimationPlaybackPlan(full, lower, upper, overlay, null, null, false, 1f, default, default);
        }

        // 检查最终状态属于当前装配，使用策略指定的入点与秒数混合。
        private CharacterAnimationLayerPlan SelectBase(in CharacterAnimationLayerPlan previous)
        {
            var demand = Owners.FullBody;
            if (!demand.IsValid) return default;
            if (demand.Key != Owners.RequestedBaseline.Key)
            {
                Selection = default;
                return actions.Select(demand, previous, false);
            }
            if (!Selection.IsValid || !Setup.Owns(Selection.State))
            {
                Selection = default;
                bool keepPrevious = previous.HasState && previous.Demand.Key == demand.Key &&
                    previous.Demand.InstanceId == demand.InstanceId;
                if (!keepPrevious && fallback.StateHash == 0) return default;
                var retained = keepPrevious ? previous.Binding : fallback;
                var fallbackCommand = keepPrevious ? CharacterAnimationPlaybackCommand.Keep : CharacterAnimationPlaybackCommand.Play;
                return new CharacterAnimationLayerPlan(demand, retained, fallbackCommand, keepPrevious ? CharacterAnimationPlaybackReason.KeepInstance :
                    CharacterAnimationPlaybackReason.Enter, 1f, default, default, 0f);
            }
            var binding = new CharacterAnimationBinding(Selection.State.Hash, demand.Key);
            bool keep = Selection.ContinuePlayback && previous.HasState && previous.StateHash == binding.StateHash;
            var command = keep ? CharacterAnimationPlaybackCommand.Keep : previous.HasState && Selection.TransitionSeconds > 0f
                ? CharacterAnimationPlaybackCommand.CrossFade : CharacterAnimationPlaybackCommand.Play;
            return new CharacterAnimationLayerPlan(demand, binding, command, keep ? CharacterAnimationPlaybackReason.KeepInstance :
                CharacterAnimationPlaybackReason.Enter, 1f, CharacterClipStart.Normalized(Selection.StartNormalizedTime),
                CharacterBlendDuration.Seconds(Selection.TransitionSeconds), 0f);
        }

        // 只有实际贡献了可见姿势的所选状态才交接策略历史。
        internal void Commit(CharacterAnimationPlayer player)
        {
            if (Selection.IsValid && Setup.Owns(Selection.State) && Plan.FullBody.StateHash == Selection.State.Hash &&
                player.UsedBasePose(Selection.State.Hash)) Instance.CommitSelection(Selection);
        }

        // 局部姿态中断时由内置策略保留语义归属，公共策略清空自己的连续性历史。
        internal void InvalidateMotion()
        {
            if (Detail != null) Detail.InvalidateMotion();
            else Instance.Reset();
        }

        // 更新编辑器中的地面连续性诊断。
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        internal void MarkBoundary(string reason)
        {
#if UNITY_EDITOR
            Detail?.MarkBoundary(reason);
#endif
        }
    }
}
