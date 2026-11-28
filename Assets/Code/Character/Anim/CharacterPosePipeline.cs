using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    /// <summary>管理分层混合后的姿态作业及其绑定、分段准备、冻结与重装。</summary>
    internal sealed class CharacterPosePipeline : IDisposable
    {
        private readonly Animator animator;
        private readonly CharacterAnimationPlayer player;
        private readonly CharacterClipPlayback clips;
        internal CharacterIkChannels Ik { get; }
        private readonly Transform owner;
        private readonly OrientationWarpTuning orientationTuning;
        private readonly CharacterOrientationWarp orientation;
        private readonly CharacterInertiaTuning poseInertia, inertiaSource;
        private readonly CharacterInertiaBones inertiaBones;
        private PlayableGraph graph;
        private AnimationScriptPlayable playable;
        private NativeArray<CharacterFootObservation> feet;
        private readonly bool sampleFeet;
        private readonly GroundMatchTuning matchTuning;
        private readonly GroundFeatureHistory featureHistory;
        private readonly CharacterStrategyBoneHistory strategyBones;
        private readonly CharacterVisualRoot visualRoot;
        private readonly CharacterTransitionProfiles transitionTuning;
        private NativeArray<CharacterFootObservation> rawHistory;
        private NativeArray<CharacterTransitionBone> transitionBones;
        private NativeArray<CharacterPoseBoneBinding> poseBindings;
        private NativeArray<CharacterPoseBoneHistory> poseHistory;
        private CharacterInertiaObservation inertiaObservation;
        private bool hasMotionVersion;
        private int motionVersion;
        private Avatar avatar;
        private bool triedBinding, frozen;

#if UNITY_EDITOR
        internal VisualRootDebug RootDebug => visualRoot?.Debug ?? default;
#endif

        // 装配原始采样、姿态修正和最终 IK 所需的模块。
        public CharacterPosePipeline(Animator animator, CharacterAnimationPlayer player, CharacterClipPlayback clips, Transform owner,
            CharacterBoundIkChannel[] channels, float walkableSlope, OrientationWarpTuning orientationTuning,
            CharacterInertiaTuning poseInertia, CharacterInertiaBones inertiaBones, CharacterAnimationConfiguration config,
            float modelDropLimit, CharacterPosePipeline previous = null, CharacterBasePoseSetup strategySetup = null)
        {
            this.animator = animator;
            this.player = player;
            this.clips = clips;
            this.owner = owner;
            Ik = new CharacterIkChannels(animator, owner, channels, walkableSlope, modelDropLimit, previous?.Ik);
            this.orientationTuning = orientationTuning?.Snapshot();
            orientation = this.orientationTuning == null ? null : new CharacterOrientationWarp(this.orientationTuning);
            inertiaSource = poseInertia;
            this.poseInertia = poseInertia?.Snapshot();
            this.inertiaBones = inertiaBones;
            sampleFeet = config.SamplesFeet || config.Modules.TransitionInertia;
            matchTuning = config.MatchCosts;
            featureHistory = new GroundFeatureHistory(config.SearchLibraries?.Layouts);
            strategyBones = new CharacterStrategyBoneHistory(strategySetup);
            visualRoot = config.Modules.VisualRoot ? new CharacterVisualRoot(config.Modules.VisualRoot) : null;
            transitionTuning = config.Modules.TransitionInertia ? config.Transitions : null;
        }

        // 改装一个肢体只替换它自己的输入；为新作业保留相同通道的平台锚点和胸臂状态，搜索历史从新的绑定开始。
        internal void TakeOver(CharacterPosePipeline previous)
        {
            bool live = previous.graph.IsValid() && previous.avatar == animator.avatar &&
                !player.NeedsRebind;
            if (!live) { previous.Dispose(); return; }
            previous.Ik.Snapshot(out var saved, out var plans);
            bool keepInertia = poseInertia != null && ReferenceEquals(inertiaSource, previous.inertiaSource) &&
                inertiaBones.Equals(previous.inertiaBones);
            var history = keepInertia ? previous.poseHistory.ToArray() : null;
            var observation = previous.inertiaObservation;
            bool wasFrozen = previous.frozen;
            float previousDrop = previous.Ik.Drop;
            bool hasVersion = previous.hasMotionVersion;
            int version = previous.motionVersion;
            previous.Invalidate(keepTargets: true);
            Bind(keepTargets: true);
            if (!graph.IsValid()) return;
            if (wasFrozen) SetFrozen(true);
            Ik.Restore(previous.Ik, saved, plans, previousDrop);
            if (history != null && poseHistory.Length == history.Length)
            {
                poseHistory.CopyFrom(history);
                inertiaObservation = observation;
            }
            hasMotionVersion = hasVersion;
            motionVersion = version;
        }

        // 读取最近一次与当前模拟版本一致的直接动画脚姿。
        public CharacterFootObservation ReadFeet(CharacterAnimationMotion motion)
        {
            if (!feet.IsCreated || feet.Length == 0 || !hasMotionVersion || motionVersion != motion.SimulationVersion ||
                frozen || avatar != animator.avatar || player.NeedsRebind)
                return default;
            return CharacterFootSampler.Snapshot(feet[0], rawHistory, Mathf.Clamp(matchTuning.HistorySeconds, 0f, 0.3f));
        }

        // 主线程拷贝自定义布局所需的姿态，后台规划始终只读托管快照。
        internal GroundFeatureQuery[] ReadFeatures(in CharacterFootObservation current) => featureHistory.Read(current, rawHistory);

        // 交付当前模型最近一次求值的骨姿，静止角色无需先提交一个运动步。
        internal CharacterBoneObservations ReadStrategyBones(in CharacterAnimationMotion motion) =>
            strategyBones.Requested && (!motion.IsValid || !hasMotionVersion || motionVersion == motion.SimulationVersion) &&
            !frozen && avatar == animator.avatar && !player.NeedsRebind ? strategyBones.Read() : default;

        // 切换时钟或运动来源时清除通用骨骼历史，等待下一次姿态求值。
        internal void ClearStrategyObservations() => strategyBones.Clear();

        // 读取本帧片段遮罩与曲线，准备求值前的惯性、IK 和脚姿采样参数。
        public void Prepare(in CharacterAnimationComposition selection, CharacterAnimationMotion motion,
            in CharacterAnimationPlaybackPlan plan, in CharacterPlaybackFrameStep step)
        {
            if (!Ik.HasChannels && orientation == null && poseInertia == null && !sampleFeet && !strategyBones.Requested &&
                visualRoot == null && transitionTuning == null) return;
            if (!triedBinding || animator.avatar != avatar)
                Bind();
            if (!graph.IsValid()) return;
            if (motion.IsValid && hasMotionVersion && motionVersion != motion.SimulationVersion)
                ClearPose();
            if (motion.IsValid)
            {
                hasMotionVersion = true;
                motionVersion = motion.SimulationVersion;
            }
            bool resumed = frozen;
            SetFrozen(false);
            float deltaTime = step.FrameDelta, animationDelta = step.GraphDelta;
            double sampleTime = step.End;
            Ik.AdvanceHeight(motion.IsValid && (motion.CollisionFlags & CollisionFlags.Below) != 0, animationDelta);
            bool readsFeet = feet.Length > 0;
            Vector2 contacts = default;
            float warpAngle = 0f;
            var probeWarp = new CharacterFootWarp(Quaternion.identity, Quaternion.identity, Vector3.zero);
            bool prepareJob = visualRoot != null || transitionBones.Length > 0 || orientation != null || poseBindings.Length > 0 ||
                Ik.HasFeet || readsFeet || strategyBones.Requested;
            if (prepareJob)
            {
                var job = playable.GetJobData<CharacterPoseJob>();
                job.StrategyBones.Time = sampleTime;
                if (job.VisualRoot.Bound)
                {
                    visualRoot.Advance(clips, motion, animator.transform, deltaTime);
                    job.VisualRoot.Offset = visualRoot.Offset;
                    job.VisualRoot.Yaw = visualRoot.Yaw;
                }
                job.Transition.Prepare(clips.Transition);
                float near = motion.Trajectory.Match.Valid ? motion.Trajectory.Match.Windows.x : matchTuning.Windows.x;
                if (job.Orientation.Bound)
                    warpAngle = orientation.Advance(clips, motion, animator.transform, animationDelta, job.VisualRoot.Yaw, near);
                job.Orientation.Angle = warpAngle;
                probeWarp = new CharacterFootWarp(Quaternion.AngleAxis(job.VisualRoot.Yaw, Vector3.up),
                    Quaternion.AngleAxis(warpAngle, Vector3.up), job.VisualRoot.Offset);
                Ik.PrepareGround(clips, plan, ref job.Correction);
                if (poseBindings.Length > 0)
                {
                    job.Inertia = inertiaObservation.Prepare(selection, motion, plan, owner, poseInertia,
                        clips.InertiaAllowance, resumed);
                }
                if (readsFeet)
                {
                    var left = clips.Permission(CharacterIkLimb.LeftFoot, CharacterFootSampler.LeftCurve);
                    var right = clips.Permission(CharacterIkLimb.RightFoot, CharacterFootSampler.RightCurve);
                    contacts = new Vector2(left.Value, right.Value);
                    var leftContact = clips.Permission(CharacterIkLimb.LeftFoot, CharacterFootSampler.LeftContactCurve);
                    var rightContact = clips.Permission(CharacterIkLimb.RightFoot, CharacterFootSampler.RightContactCurve);
                    job.Feet.SampleTime = sampleTime;
                    job.Feet.ClipContacts = new Vector2(leftContact.CoveredValue, rightContact.CoveredValue);
                    job.Feet.ClipConfidence = new Vector2(leftContact.Coverage, rightContact.Coverage);
                }
                playable.SetJobData(job);
            }
            Ik.PrepareInputs(selection, clips, playable, readsFeet, contacts, animationDelta, probeWarp);
        }

        // 在直接播放图中绑定骨骼、许可和脚姿缓冲区，并将作业接到最终输出。
        private void Bind(bool keepTargets = false)
        {
            Invalidate(keepTargets);
            if (!clips.Graph.IsValid()) return;
            triedBinding = true;
            avatar = animator.avatar;
            try
            {
                bool any = Ik.Bind(animator);
                var shared = new System.Collections.Generic.HashSet<Transform>();
                var transitions = CharacterTransitionInertia.Bind(animator, transitionTuning, shared);
                var bones = poseInertia != null ? inertiaBones.Bind(animator, shared) : Array.Empty<CharacterPoseBoneBinding>();
                var rootBone = CharacterVisualRootBone.Bind(animator, visualRoot, owner);
                CharacterFootSampler sampler = default;
                bool validFeet = sampleFeet && CharacterFootSampler.TryBind(animator, out sampler);
                var orientationBones = CharacterOrientationBones.Bind(animator, orientationTuning);
                if (!any && !orientationBones.Bound && !rootBone.Bound && transitions.Length == 0 && bones.Length == 0 &&
                    !validFeet && !strategyBones.Requested) { DisposeBuffers(); return; }
                poseBindings = new NativeArray<CharacterPoseBoneBinding>(bones, Allocator.Persistent);
                poseHistory = new NativeArray<CharacterPoseBoneHistory>(bones.Length, Allocator.Persistent);
                feet = new NativeArray<CharacterFootObservation>(validFeet ? 1 : 0, Allocator.Persistent);
                rawHistory = new NativeArray<CharacterFootObservation>(validFeet ? 64 : 0, Allocator.Persistent);
                transitionBones = new NativeArray<CharacterTransitionBone>(transitions, Allocator.Persistent);
                var featureSampler = featureHistory.Bind(animator, validFeet ? rawHistory.Length : 0);
                sampler.Ring = rawHistory;
                sampler.History = feet;
                graph = clips.Graph;
                playable = AnimationScriptPlayable.Create(graph, new CharacterPoseJob
                    { Skeleton = Ik.Skeleton, Correction = Ik.CreateCorrection(animator, owner), Feet = sampler, Features = featureSampler,
                        StrategyBones = strategyBones.Bind(animator),
                        VisualRoot = rootBone, Transition = new CharacterTransitionInertia { Bones = transitionBones },
                        Orientation = orientationBones,
                        PoseBindings = poseBindings, PoseHistory = poseHistory });
                clips.ConnectPose(playable);
            }
            catch (Exception error)
            {
                Invalidate();
                triedBinding = true;
                avatar = animator.avatar;
                Debug.LogWarning($"姿态作业绑定失败：{error.Message}", animator);
            }
        }

        // 冻结作业时丢弃环境运动和旧脚姿，恢复时重新建立骨骼速度基准。
        public void SetFrozen(bool value)
        {
            if (!graph.IsValid()) return;
            if (player.NeedsRebind || avatar != animator.avatar)
            {
                Invalidate();
                return;
            }
            if (frozen == value) return;
            strategyBones.Clear();
            visualRoot?.Reset();
            CharacterTransitionInertia.Reset(transitionBones);
            orientation?.Reset();
            if (feet.IsCreated && feet.Length > 0) feet[0] = default;
            var job = playable.GetJobData<CharacterPoseJob>();
            if (value)
            {
                hasMotionVersion = false;
                Ik.Freeze();
            }
            else
                for (int i = 0; i < poseHistory.Length; i++)
                {
                    var state = poseHistory[i];
                    state.RebaseVelocity = 1;
                    poseHistory[i] = state;
                }
            job.Orientation.Angle = job.VisualRoot.Yaw = 0f;
            job.VisualRoot.Offset = Vector3.zero;
            frozen = job.Frozen = value;
            playable.SetJobData(job);
        }

        // 清除旧模拟版本的握持、脚姿和惯性状态。
        public void ClearPose()
        {
            strategyBones.Clear();
            visualRoot?.Reset();
            CharacterTransitionInertia.Reset(transitionBones);
            orientation?.Reset();
            Ik.ClearPose();
            hasMotionVersion = false;
            if (feet.IsCreated && feet.Length > 0) feet[0] = default;
            inertiaObservation.ClearSelection();
            if (poseHistory.IsCreated)
                for (int i = 0; i < poseHistory.Length; i++) poseHistory[i] = default;
            if (graph.IsValid())
            {
                var job = playable.GetJobData<CharacterPoseJob>();
                job.Inertia = default;
                job.VisualRoot.Offset = Vector3.zero;
                job.Orientation.Angle = job.VisualRoot.Yaw = 0f;
                job.Correction.AppliedModelDrop = job.Correction.RemainingModelDrop = 0f;
                playable.SetJobData(job);
            }
        }

        // 丢弃姿态图与登记的 IK 请求，供新的状态代次重新开始。
        public void Reset()
        {
            Invalidate();
            Ik.ClearRequests();
        }

        // 断开本作业并释放对应缓冲区，按需为重绑保留已提交目标。
        public void Invalidate(bool keepTargets = false)
        {
            visualRoot?.Reset();
            CharacterTransitionInertia.Reset(transitionBones);
            orientation?.Reset();
            Ik.ResetHistory(keepTargets);
            clips.DisconnectPose(playable);
            graph = default;
            playable = default;
            DisposeBuffers();
            triedBinding = hasMotionVersion = frozen = false;
            inertiaObservation = default;
            avatar = null;
        }

        // 释放动画作业使用的原生缓冲区。
        private void DisposeBuffers()
        {
            strategyBones.Dispose();
            featureHistory.Dispose();
            if (transitionBones.IsCreated) transitionBones.Dispose();
            if (rawHistory.IsCreated) rawHistory.Dispose();
            if (feet.IsCreated) feet.Dispose();
            if (poseHistory.IsCreated) poseHistory.Dispose();
            if (poseBindings.IsCreated) poseBindings.Dispose();
            Ik.DisposeBuffers();
        }

        // 释放最终姿态节点与其全部原生缓冲。
        public void Dispose() => Invalidate();
    }
}
