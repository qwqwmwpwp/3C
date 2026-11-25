using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    /// <summary>将惯性和 IK 作业连接到直接片段图，在分层混合后输出最终姿势。</summary>
    internal sealed class CharacterAnimationIk : IDisposable
    {
        private readonly Animator animator;
        private readonly CharacterAnimationPlayer player;
        private readonly CharacterClipPlayback clips;
        private readonly CharacterBoundIkChannel[] channels;
        private readonly CharacterIkInputSource[] sources;
        private readonly Transform owner;
        private readonly CharacterModelHeight modelHeight;
        private readonly OrientationWarpTuning orientationTuning;
        private readonly CharacterOrientationWarp orientation;
        private readonly CharacterInertiaTuning poseInertia;
        private readonly CharacterInertiaBones inertiaBones;
        private PlayableGraph graph;
        private AnimationScriptPlayable playable;
        private NativeArray<CharacterIkBinding> bindings;
        private NativeArray<CharacterIkInput> inputs;
        private NativeArray<CharacterIkCandidate> sceneCandidates;
        private NativeArray<CharacterIkState> states;
        private NativeArray<CharacterFootObservation> feet;
        private readonly bool sampleFeet;
        private readonly GroundMatchTuning matchTuning;
        private readonly GroundFeatureHistory featureHistory;
        private readonly CharacterVisualRoot visualRoot;
        private readonly TransitionInertiaModule transitionTuning;
        private NativeArray<CharacterFootObservation> rawHistory;
        private NativeArray<CharacterTransitionBone> transitionBones;
        private readonly bool hasFootChannels;
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

        // 装配局部 IK 输入和模型高度状态，以便接入播放图。
        public CharacterAnimationIk(Animator animator, CharacterAnimationPlayer player, CharacterClipPlayback clips, Transform owner,
            CharacterBoundIkChannel[] channels, float walkableSlope, OrientationWarpTuning orientationTuning,
            CharacterInertiaTuning poseInertia, CharacterInertiaBones inertiaBones, CharacterAnimationConfiguration config,
            float modelDropLimit, CharacterAnimationIk previous = null)
        {
            this.animator = animator;
            this.player = player;
            this.clips = clips;
            this.owner = owner;
            this.channels = channels ?? Array.Empty<CharacterBoundIkChannel>();
            hasFootChannels = IndexOf(CharacterIkLimb.LeftFoot) >= 0 || IndexOf(CharacterIkLimb.RightFoot) >= 0;
            if (hasFootChannels)
                modelHeight = CharacterModelHeight.Bind(animator, owner, modelDropLimit);
            this.orientationTuning = orientationTuning;
            orientation = orientationTuning == null ? null : new CharacterOrientationWarp(orientationTuning);
            this.poseInertia = poseInertia;
            this.inertiaBones = inertiaBones;
            sampleFeet = config.SamplesFeet || config.Modules.TransitionInertia;
            matchTuning = config.MatchCosts;
            featureHistory = new GroundFeatureHistory(config.FeatureLayout);
            visualRoot = config.Modules.VisualRoot ? new CharacterVisualRoot(config.Modules.VisualRoot) : null;
            transitionTuning = config.Modules.TransitionInertia;
            sources = new CharacterIkInputSource[this.channels.Length];
            for (int i = 0; i < sources.Length; i++)
                if (this.channels[i].Tuning != null && IndexOf(this.channels[i].Tuning.Limb) == i)
                {
                    var tuning = this.channels[i].Tuning;
                    int old = previous?.IndexOf(tuning.Limb) ?? -1;
                    sources[i] = old >= 0 && ReferenceEquals(previous.channels[old].Tuning, tuning)
                        ? previous.sources[old] : new CharacterIkInputSource(this.channels[i], owner, animator ? animator.transform : null, walkableSlope);
                }
        }

        // 改装一个肢体只替换它自己的输入；为新作业保留相同通道的平台锚点和胸臂状态，搜索历史从新的绑定开始。
        internal void TakeOver(CharacterAnimationIk previous)
        {
            bool live = previous.graph.IsValid() && previous.avatar == animator.avatar &&
                !player.NeedsRebind;
            if (!live) { previous.Dispose(); return; }
            var saved = previous.states.ToArray();
            bool keepInertia = poseInertia != null && ReferenceEquals(poseInertia, previous.poseInertia);
            var history = keepInertia ? previous.poseHistory.ToArray() : null;
            var observation = previous.inertiaObservation;
            bool wasFrozen = previous.frozen;
            float previousDrop = previous.modelHeight?.Drop ?? 0f;
            bool hasVersion = previous.hasMotionVersion;
            int version = previous.motionVersion;
            previous.Invalidate(keepTargets: true);
            Bind(keepTargets: true);
            if (!graph.IsValid()) return;
            if (wasFrozen) SetFrozen(true);
            for (int i = 0; i < channels.Length; i++)
            {
                int old = channels[i].Tuning != null ? previous.IndexOf(channels[i].Tuning.Limb) : -1;
                if (old < 0 || !ReferenceEquals(channels[i].Tuning, previous.channels[old].Tuning)) continue;
                var state = saved[old];
                state.ProbeMotion = default; // 重新装配后从新的骨盆与朝向基准采样。
                states[i] = state;
            }
            modelHeight?.SetDrop(previousDrop);
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
        internal GroundFeatureQuery ReadFeatures(in CharacterFootObservation current) => featureHistory.Read(current, rawHistory);

        public bool Submit(CharacterIkLimb limb, CharacterIkSource source, long instance, Transform target, int priority,
            string action, string provider)
        {
            int i = IndexOf(limb);
            return i >= 0 && sources[i].Submit(source, instance, target, priority, action, provider);
        }

        public bool Withdraw(CharacterIkLimb limb, CharacterIkSource source, long instance, string provider)
        {
            int i = IndexOf(limb);
            return i >= 0 && sources[i].Withdraw(source, instance, provider);
        }

        private int IndexOf(CharacterIkLimb limb)
        {
            for (int i = 0; i < channels.Length; i++)
                if (channels[i].Tuning != null && channels[i].Tuning.Limb == limb) return i;
            return -1;
        }

        // 读取本帧片段遮罩与曲线，准备求值前的惯性、IK 和脚姿采样参数。
        public void Prepare(in CharacterAnimationComposition selection, CharacterAnimationMotion motion,
            in CharacterAnimationPlaybackPlan plan, float deltaTime, double sampleTime)
        {
            if (sources.Length == 0 && orientation == null && poseInertia == null && !sampleFeet && visualRoot == null && !transitionTuning) return;
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
            float animationDelta = clips.LayerBlendDelta(deltaTime);
            modelHeight?.Advance(states, motion.IsValid && (motion.CollisionFlags & CollisionFlags.Below) != 0, animationDelta);
            bool readsFeet = feet.Length > 0;
            Vector2 contacts = default;
            float warpAngle = 0f;
            var probeWarp = new CharacterFootWarp(Quaternion.identity, Quaternion.identity, Vector3.zero);
            bool prepareJob = visualRoot != null || transitionBones.Length > 0 || orientation != null || poseBindings.Length > 0 || hasFootChannels || readsFeet;
            if (prepareJob)
            {
                var job = playable.GetJobData<CharacterIkJob>();
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
                job.AppliedModelDrop = modelHeight?.Drop ?? 0f;
                job.RemainingModelDrop = modelHeight?.Remaining ?? 0f;
                if (hasFootChannels) job.FootPlant = CharacterFootPlantFrame.Read(clips, plan);
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
            EnsureCandidateCapacity();
            int cursor = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                var binding = bindings[i];
                if (!binding.Valid) continue;
                var tuning = channels[i].Tuning;
                if (readsFeet && tuning.Limb == CharacterIkLimb.LeftFoot && tuning.WeightCurve == CharacterFootSampler.LeftCurve)
                    binding.ClipPermission = contacts.x;
                else if (readsFeet && tuning.Limb == CharacterIkLimb.RightFoot && tuning.WeightCurve == CharacterFootSampler.RightCurve)
                    binding.ClipPermission = contacts.y;
                else binding.ClipPermission = clips.Permission(tuning.Limb, tuning.WeightCurve).Value;
                bindings[i] = binding;
                var state = states[i];
                inputs[i] = sources[i].Sample(selection, sceneCandidates, ref cursor,
                    state.ProbeMotion, animationDelta, probeWarp, clips);
            }
        }

        // 保证动画作业的场景候选缓冲能容纳本帧所有通道。
        private void EnsureCandidateCapacity()
        {
            int required = 0;
            foreach (var source in sources) required += source?.SceneCapacity ?? 0;
            if (required <= sceneCandidates.Length) return;
            var larger = new NativeArray<CharacterIkCandidate>(Mathf.NextPowerOfTwo(required), Allocator.Persistent);
            try
            {
                var job = playable.GetJobData<CharacterIkJob>();
                job.Scenes = larger;
                playable.SetJobData(job);
            }
            catch
            {
                larger.Dispose();
                throw;
            }
            sceneCandidates.Dispose();
            sceneCandidates = larger;
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
                bindings = new NativeArray<CharacterIkBinding>(channels.Length, Allocator.Persistent);
                bool any = false;
                for (int i = 0; i < channels.Length; i++)
                {
                    var channel = channels[i];
                    if (!CanBindChannel(i)) continue;
                    bool hasGoal = channel.Bones.AnimationTarget != null && channel.Bones.AnimationTarget.IsChildOf(animator.transform)
                        && !IsDriven(channel.Bones.AnimationTarget);
                    bindings[i] = new CharacterIkBinding { Valid = true, HasAnimationTarget = hasGoal,
                        Limb = channel.Tuning.Limb,
                        Root = animator.BindStreamTransform(channel.Bones.Root), Mid = animator.BindStreamTransform(channel.Bones.Mid),
                        Tip = animator.BindStreamTransform(channel.Bones.Tip),
                        AnimationTarget = hasGoal ? animator.BindStreamTransform(channel.Bones.AnimationTarget) : default,
                        PositionWeight = Mathf.Clamp01(channel.Tuning.PositionWeight), RotationWeight = Mathf.Clamp01(channel.Tuning.RotationWeight),
                        BlendSeconds = InputMath.NonNegative(channel.Tuning.BlendSeconds, 0f) };
                    any = true;
                }
                var shared = new System.Collections.Generic.HashSet<Transform>();
                var transitions = CharacterTransitionInertia.Bind(animator, transitionTuning, shared);
                var bones = poseInertia != null ? inertiaBones.Bind(animator, shared) : Array.Empty<CharacterPoseBoneBinding>();
                var rootBone = CharacterVisualRootBone.Bind(animator, visualRoot, owner);
                CharacterFootSampler sampler = default;
                bool validFeet = sampleFeet && CharacterFootSampler.TryBind(animator, out sampler);
                var orientationBones = CharacterOrientationBones.Bind(animator, orientationTuning);
                if (!any && !orientationBones.Bound && !rootBone.Bound && transitions.Length == 0 && bones.Length == 0 && !validFeet) { DisposeBuffers(); return; }
                poseBindings = new NativeArray<CharacterPoseBoneBinding>(bones, Allocator.Persistent);
                poseHistory = new NativeArray<CharacterPoseBoneHistory>(bones.Length, Allocator.Persistent);
                inputs = new NativeArray<CharacterIkInput>(channels.Length, Allocator.Persistent);
                states = new NativeArray<CharacterIkState>(channels.Length, Allocator.Persistent);
                sceneCandidates = new NativeArray<CharacterIkCandidate>(1, Allocator.Persistent);
                feet = new NativeArray<CharacterFootObservation>(validFeet ? 1 : 0, Allocator.Persistent);
                rawHistory = new NativeArray<CharacterFootObservation>(validFeet ? 64 : 0, Allocator.Persistent);
                transitionBones = new NativeArray<CharacterTransitionBone>(transitions, Allocator.Persistent);
                var featureSampler = featureHistory.Bind(animator, validFeet ? rawHistory.Length : 0);
                sampler.Ring = rawHistory;
                sampler.History = feet;
                graph = clips.Graph;
                playable = AnimationScriptPlayable.Create(graph, new CharacterIkJob
                    { Bindings = bindings, Inputs = inputs, States = states, Scenes = sceneCandidates, Feet = sampler, Features = featureSampler,
                        VisualRoot = rootBone, Transition = new CharacterTransitionInertia { Bones = transitionBones },
                        Orientation = orientationBones, ProbeBasis = animator.BindSceneTransform(owner), AdjustModelHeight = (byte)(modelHeight != null ? 1 : 0),
                        PoseBindings = poseBindings, PoseHistory = poseHistory,
                        LeftHand = IndexOf(CharacterIkLimb.LeftHand), RightHand = IndexOf(CharacterIkLimb.RightHand) });
                clips.ConnectIk(playable);
            }
            catch (Exception error)
            {
                Invalidate();
                triedBinding = true;
                avatar = animator.avatar;
                Debug.LogWarning($"IK 绑定失败：{error.Message}", animator);
            }
        }

        // 验证关节链和素材许可曲线名称是否可供动画作业使用。
        private bool CanBindChannel(int i)
        {
            var channel = channels[i];
            var bones = channel.Bones;
            return sources[i] != null && bones.Root && bones.Mid && bones.Tip &&
                bones.Root != animator.transform && bones.Root != bones.Mid && bones.Mid != bones.Tip &&
                bones.Root.IsChildOf(animator.transform) && bones.Mid.IsChildOf(bones.Root) && bones.Tip.IsChildOf(bones.Mid) &&
                !string.IsNullOrWhiteSpace(channel.Tuning.WeightCurve);
        }

        private bool IsDriven(Transform goal)
        {
            foreach (var channel in channels)
                if (channel.Bones.Root && goal.IsChildOf(channel.Bones.Root)) return true;
            return false;
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
            visualRoot?.Reset();
            CharacterTransitionInertia.Reset(transitionBones);
            orientation?.Reset();
            if (feet.IsCreated && feet.Length > 0) feet[0] = default;
            var job = playable.GetJobData<CharacterIkJob>();
            if (value)
            {
                hasMotionVersion = false;
                for (int i = 0; i < states.Length; i++)
                {
                    var state = states[i];
                    state.ProbeMotion = default;
                    states[i] = state;
                }
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
            visualRoot?.Reset();
            CharacterTransitionInertia.Reset(transitionBones);
            orientation?.Reset();
            modelHeight?.SetDrop(0f);
            foreach (var source in sources) source?.ForgetGrip();
            hasMotionVersion = false;
            if (feet.IsCreated && feet.Length > 0) feet[0] = default;
            inertiaObservation.ClearSelection();
            if (poseHistory.IsCreated)
                for (int i = 0; i < poseHistory.Length; i++) poseHistory[i] = default;
            if (graph.IsValid())
            {
                var job = playable.GetJobData<CharacterIkJob>();
                job.Inertia = default;
                job.VisualRoot.Offset = Vector3.zero;
                job.Orientation.Angle = job.VisualRoot.Yaw = 0f;
                job.AppliedModelDrop = job.RemainingModelDrop = 0f;
                playable.SetJobData(job);
            }
            if (!states.IsCreated) return;
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = default;
                inputs[i] = default;
            }
        }

        public void Reset()
        {
            Invalidate();
            foreach (var source in sources) source?.Clear();
        }

        // 断开本作业并释放对应缓冲区，按需为重绑保留已提交目标。
        public void Invalidate(bool keepTargets = false)
        {
            visualRoot?.Reset();
            CharacterTransitionInertia.Reset(transitionBones);
            orientation?.Reset();
            modelHeight?.SetDrop(0f);
            if (!keepTargets) foreach (var source in sources) source?.ForgetGrip();
            clips.DisconnectIk(playable);
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
            featureHistory.Dispose();
            if (transitionBones.IsCreated) transitionBones.Dispose();
            if (rawHistory.IsCreated) rawHistory.Dispose();
            if (feet.IsCreated) feet.Dispose();
            if (poseHistory.IsCreated) poseHistory.Dispose();
            if (poseBindings.IsCreated) poseBindings.Dispose();
            if (states.IsCreated) states.Dispose();
            if (inputs.IsCreated) inputs.Dispose();
            if (sceneCandidates.IsCreated) sceneCandidates.Dispose();
            if (bindings.IsCreated) bindings.Dispose();
        }

        public void Dispose() => Invalidate();
    }
}
