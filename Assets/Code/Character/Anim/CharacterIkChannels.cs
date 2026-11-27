using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 管理一个角色的 IK 来源、通道缓冲和骨架绑定，按每个播放分段准备修正输入。
    internal sealed class CharacterIkChannels
    {
        private readonly CharacterBoundIkChannel[] channels;
        private readonly CharacterIkInputSource[] sources;
        private CharacterIkSkeleton skeleton;
        private NativeArray<CharacterIkWeights> weights;
        private NativeArray<CharacterIkInput> inputs;
        private NativeArray<CharacterIkCandidate> sceneCandidates;
        private NativeArray<CharacterIkState> states;
        private readonly CharacterModelHeight modelHeight;
        public bool HasFeet { get; }
        public bool HasChannels => channels.Length > 0;
        public CharacterIkSkeleton Skeleton => skeleton;
        public float Drop => modelHeight?.Drop ?? 0f;

        // 按配置顺序建立各通道的来源，同一模块重装时延续其请求编号和握持历史。
        internal CharacterIkChannels(Animator animator, Transform owner, CharacterBoundIkChannel[] channels,
            float walkableSlope, float modelDropLimit, CharacterIkChannels previous)
        {
            this.channels = channels ?? Array.Empty<CharacterBoundIkChannel>();
            HasFeet = IndexOf(CharacterIkLimb.LeftFoot) >= 0 || IndexOf(CharacterIkLimb.RightFoot) >= 0;
            if (HasFeet) modelHeight = CharacterModelHeight.Bind(animator, owner, modelDropLimit);
            sources = new CharacterIkInputSource[this.channels.Length];
            for (int i = 0; i < sources.Length; i++)
                if (this.channels[i].Tuning != null && IndexOf(this.channels[i].Tuning.Limb) == i)
                {
                    var tuning = this.channels[i].Tuning;
                    int old = previous?.IndexOf(tuning.Limb) ?? -1;
                    if (old >= 0 && ReferenceEquals(previous.channels[old].Source, this.channels[i].Source))
                    {
                        sources[i] = previous.sources[old];
                        sources[i].Rebind(this.channels[i], walkableSlope);
                    }
                    else sources[i] = new CharacterIkInputSource(this.channels[i], owner, animator ? animator.transform : null, walkableSlope);
                }
        }

        // 将全部有效通道绑定到当前模型，并建立各通道独立的计划及状态槽。
        internal bool Bind(Animator animator)
        {
            skeleton.Allocate(channels.Length);
            weights = new NativeArray<CharacterIkWeights>(channels.Length, Allocator.Persistent);
            inputs = new NativeArray<CharacterIkInput>(channels.Length, Allocator.Persistent);
            states = new NativeArray<CharacterIkState>(channels.Length, Allocator.Persistent);
            sceneCandidates = new NativeArray<CharacterIkCandidate>(1, Allocator.Persistent);
            bool any = false;
            for (int i = 0; i < channels.Length; i++)
            {
                var channel = channels[i];
                if (sources[i] == null || string.IsNullOrWhiteSpace(channel.Tuning.WeightCurve) ||
                    !skeleton.Bind(i, animator, channel.Bones, channels)) continue;
                weights[i] = new CharacterIkWeights { Limb = channel.Tuning.Limb,
                    PositionWeight = Mathf.Clamp01(channel.Tuning.PositionWeight), RotationWeight = Mathf.Clamp01(channel.Tuning.RotationWeight),
                    BlendSeconds = InputMath.NonNegative(channel.Tuning.BlendSeconds, 0f) };
                any = true;
            }
            return any;
        }

        // 将当前通道修正所需的缓冲和固定索引交给姿态作业。
        internal CharacterIkCorrection CreateCorrection(Animator animator, Transform owner) => new CharacterIkCorrection
        {
            Weights = weights, Inputs = inputs, States = states, Scenes = sceneCandidates,
            ProbeBasis = animator.BindSceneTransform(owner), AdjustModelHeight = (byte)(modelHeight != null ? 1 : 0),
            LeftHand = IndexOf(CharacterIkLimb.LeftHand), RightHand = IndexOf(CharacterIkLimb.RightHand)
        };

        // 将外部目标登记到对应部位和来源。
        public bool Submit(CharacterIkLimb limb, CharacterIkSource source, long instance, Transform target, int priority,
            string action, string provider)
        {
            int i = IndexOf(limb);
            return i >= 0 && sources[i].Requests.Submit(source, instance, target, priority, action, provider, sources[i].HasMarker);
        }

        // 从指定部位撤回该提供者的匹配实例。
        public bool Withdraw(CharacterIkLimb limb, CharacterIkSource source, long instance, string provider)
        {
            int i = IndexOf(limb);
            return i >= 0 && sources[i].Requests.Withdraw(source, instance, provider);
        }

        // 相同部位仅使用首个完成装配的通道。
        private int IndexOf(CharacterIkLimb limb)
        {
            for (int i = 0; i < channels.Length; i++)
                if (channels[i].Tuning != null && channels[i].Tuning.Limb == limb) return i;
            return -1;
        }

        // 根据上次承重需求在图求值前推进模型高度。
        internal void AdvanceHeight(bool grounded, float delta) => modelHeight?.Advance(states, grounded, delta);

        // 准备与本段骨姿一同使用的承重片段和剩余降模余量。
        internal void PrepareGround(CharacterClipPlayback clips, in CharacterAnimationPlaybackPlan plan, ref CharacterIkCorrection correction)
        {
            correction.AppliedModelDrop = Drop;
            correction.RemainingModelDrop = modelHeight?.Remaining ?? 0f;
            if (HasFeet) correction.FootPlant = CharacterFootPlantFrame.Read(clips, plan);
        }

        // 按每段的实际片段权重准备许可、场景候选与物理探测。
        internal void PrepareInputs(in CharacterAnimationComposition selection, CharacterClipPlayback clips,
            AnimationScriptPlayable playable, bool readsFeet, Vector2 contacts, float animationDelta, in CharacterFootWarp probeWarp)
        {
            EnsureCandidateCapacity(playable);
            int cursor = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                if (!skeleton.Bindings[i].Valid) continue;
                var channelWeights = weights[i];
                var tuning = channels[i].Tuning;
                if (readsFeet && tuning.Limb == CharacterIkLimb.LeftFoot && tuning.WeightCurve == CharacterFootSampler.LeftCurve)
                    channelWeights.ClipPermission = contacts.x;
                else if (readsFeet && tuning.Limb == CharacterIkLimb.RightFoot && tuning.WeightCurve == CharacterFootSampler.RightCurve)
                    channelWeights.ClipPermission = contacts.y;
                else channelWeights.ClipPermission = clips.Permission(tuning.Limb, tuning.WeightCurve).Value;
                weights[i] = channelWeights;
                var state = states[i];
                inputs[i] = sources[i].Sample(selection, sceneCandidates, ref cursor,
                    state.Ground.ProbeMotion, animationDelta, probeWarp, clips);
            }
        }

        // 保证动画作业的场景候选缓冲能容纳本帧所有通道。
        private void EnsureCandidateCapacity(AnimationScriptPlayable playable)
        {
            int required = 0;
            foreach (var source in sources) required += source?.Requests.SceneCapacity ?? 0;
            if (required <= sceneCandidates.Length) return;
            var larger = new NativeArray<CharacterIkCandidate>(Mathf.NextPowerOfTwo(required), Allocator.Persistent);
            try
            {
                var job = playable.GetJobData<CharacterPoseJob>();
                job.Correction.Scenes = larger;
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

        // 保存旧图的跨段状态和冻结时仍需应用的计划。
        internal void Snapshot(out CharacterIkState[] saved, out CharacterIkPlan[] plans)
        {
            saved = states.ToArray();
            plans = skeleton.Plans.ToArray();
        }

        // 仅相同来源且骨链未变的通道可以接回旧状态与计划。
        internal void Restore(CharacterIkChannels previous, CharacterIkState[] saved, CharacterIkPlan[] plans, float drop)
        {
            for (int i = 0; i < channels.Length; i++)
            {
                int old = channels[i].Tuning != null ? previous.IndexOf(channels[i].Tuning.Limb) : -1;
                if (old < 0 || !ReferenceEquals(channels[i].Source, previous.channels[old].Source) ||
                    !channels[i].Bones.Equals(previous.channels[old].Bones)) continue;
                var state = saved[old];
                state.Ground.ProbeMotion = default; // 重新装配后从新的骨盆与朝向基准采样。
                states[i] = state;
                skeleton.Plans[i] = plans[old];
            }
            modelHeight?.SetDrop(drop);
        }

        // 冻结时弃用脚踝的速度基准，同时保留锁点和最终计划。
        internal void Freeze()
        {
            for (int i = 0; i < states.Length; i++)
            {
                var state = states[i];
                state.Ground.ProbeMotion = default;
                states[i] = state;
            }
        }

        // 清除先前模拟版本产生的修正状态，已登记的外部目标仍然有效。
        internal void ClearPose()
        {
            ResetHistory(false);
            if (!states.IsCreated) return;
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = default;
                inputs[i] = default;
                skeleton.Plans[i] = default;
            }
        }

        // 恢复模型静止高度，并按重装策略决定是否延续握持来源。
        internal void ResetHistory(bool keepTargets)
        {
            modelHeight?.SetDrop(0f);
            if (!keepTargets) foreach (var source in sources) source?.ForgetGrip();
        }

        // 完整重置时释放持久请求的注册关系。
        internal void ClearRequests()
        {
            foreach (var source in sources) source?.Requests.Clear();
        }

        // 播放节点断开后释放修正数据和骨链执行缓冲。
        internal void DisposeBuffers()
        {
            if (states.IsCreated) states.Dispose();
            if (inputs.IsCreated) inputs.Dispose();
            if (sceneCandidates.IsCreated) sceneCandidates.Dispose();
            if (weights.IsCreated) weights.Dispose();
            skeleton.Dispose();
        }
    }
}
