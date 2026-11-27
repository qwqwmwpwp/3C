channels='''    // 管理一个角色的 IK 来源、通道缓冲和骨架绑定，按每个播放分段准备修正输入。
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
'''+part(old,'            sources = new CharacterIkInputSource','        // 改装一个肢体')+'''        // 将全部有效通道绑定到当前模型，并建立各通道独立的计划及状态槽。
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
'''+part(old,'        public bool Submit','        // 读取本帧片段遮罩').replace('sources[i].Submit(source, instance, target, priority, action, provider)','sources[i].Requests.Submit(source, instance, target, priority, action, provider, sources[i].HasMarker)').replace('        public bool Withdraw','        // 从指定部位撤回该提供者的匹配实例。\n        public bool Withdraw').replace('sources[i].Withdraw','sources[i].Requests.Withdraw').replace('        private int IndexOf','        // 相同部位仅使用首个完成装配的通道。\n        private int IndexOf')+'''        // 根据上次承重需求在图求值前推进模型高度。
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
'''+part(old,'            EnsureCandidateCapacity();','        // 在直接播放图中').replace('EnsureCandidateCapacity();','EnsureCandidateCapacity(playable);').replace('                var binding = bindings[i];\n                if (!binding.Valid) continue;', '                if (!skeleton.Bindings[i].Valid) continue;\n                var binding = weights[i];').replace('bindings[i] = binding;', 'weights[i] = binding;').replace('state.ProbeMotion','state.Ground.ProbeMotion').replace('source?.SceneCapacity','source?.Requests.SceneCapacity').replace('private void EnsureCandidateCapacity()','private void EnsureCandidateCapacity(AnimationScriptPlayable playable)').replace('CharacterIkJob','CharacterPoseJob').replace('job.Scenes','job.Correction.Scenes')+'''        // 保存旧图的跨段状态和冻结时仍需应用的计划。
        internal void Snapshot(out CharacterIkState[] saved, out CharacterIkPlan[] plans)
        {
            saved = states.ToArray();
            plans = skeleton.Plans.ToArray();
        }

        // 仅相同来源且骨链未变的通道可以接回旧状态与计划。
        internal void Restore(CharacterIkChannels previous, CharacterIkState[] saved, CharacterIkPlan[] plans, float drop)
        {
'''+part(old,'            for (int i = 0; i < channels.Length; i++)','            if (history != null').replace('state.ProbeMotion','state.Ground.ProbeMotion').replace('states[i] = state;', 'states[i] = state;\n                skeleton.Plans[i] = plans[old];').replace('SetDrop(previousDrop)','SetDrop(drop)')+'''        }

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
    }'''
put('CharacterIkChannels',wrap('using System;\nusing Unity.Collections;\nusing UnityEngine;\nusing UnityEngine.Animations;',channels))
pose=old.replace('CharacterAnimationIk','CharacterPosePipeline').replace('CharacterIkJob','CharacterPoseJob')
pose=pose.replace('        private readonly CharacterBoundIkChannel[] channels;\n        private readonly CharacterIkInputSource[] sources;', '        internal CharacterIkChannels Ik { get; }').replace('        private readonly CharacterModelHeight modelHeight;\n','').replace('        private readonly bool hasFootChannels;\n','')
pose=section(pose,'        private NativeArray<CharacterIkBinding>','        private NativeArray<CharacterFootObservation> feet;', '')
pose=section(pose,'            this.channels = channels','            this.orientationTuning','            Ik = new CharacterIkChannels(animator, owner, channels, walkableSlope, modelDropLimit, previous?.Ik);\n')
pose=section(pose,'            sources = new CharacterIkInputSource','        // 改装一个肢体','        }\n\n')
pose=pose.replace('var saved = previous.states.ToArray();','previous.Ik.Snapshot(out var saved, out var plans);').replace('previous.modelHeight?.Drop ?? 0f','previous.Ik.Drop')
pose=section(pose,'            for (int i = 0; i < channels.Length; i++)','            if (history != null','            Ik.Restore(previous.Ik, saved, plans, previousDrop);\n')
pose=section(pose,'        public bool Submit','        // 读取本帧片段遮罩','')
pose=pose.replace('sources.Length == 0','!Ik.HasChannels').replace('modelHeight?.Advance(states,','Ik.AdvanceHeight(').replace('Ik.AdvanceHeight( motion','Ik.AdvanceHeight(motion').replace('hasFootChannels','Ik.HasFeet')
pose=section(pose,'                job.AppliedModelDrop','                if (poseBindings.Length','                Ik.PrepareGround(clips, plan, ref job.Correction);\n')
pose=section(pose,'            EnsureCandidateCapacity();','        // 在直接播放图中','''            Ik.PrepareInputs(selection, clips, playable, readsFeet, contacts, animationDelta, probeWarp);
        }

''')
pose=section(pose,'                bindings = new NativeArray','                var shared','                bool any = Ik.Bind(animator);\n')
pose=section(pose,'                inputs = new NativeArray','                feet =','')
pose=pose.replace('Bindings = bindings, Inputs = inputs, States = states, Scenes = sceneCandidates,','Skeleton = Ik.Skeleton, Correction = Ik.CreateCorrection(animator, owner),').replace('Orientation = orientationBones, ProbeBasis = animator.BindSceneTransform(owner), AdjustModelHeight = (byte)(modelHeight != null ? 1 : 0),','Orientation = orientationBones,').replace('PoseBindings = poseBindings, PoseHistory = poseHistory,\n                        LeftHand = IndexOf(CharacterIkLimb.LeftHand), RightHand = IndexOf(CharacterIkLimb.RightHand)','PoseBindings = poseBindings, PoseHistory = poseHistory')
pose=section(pose,'        // 验证关节链','        // 冻结作业','')
pose=section(pose,'                for (int i = 0; i < states.Length; i++)','            }\n            else','                Ik.Freeze();\n')
pose=pose.replace('            modelHeight?.SetDrop(0f);\n            foreach (var source in sources) source?.ForgetGrip();','            Ik.ClearPose();').replace('job.AppliedModelDrop = job.RemainingModelDrop','job.Correction.AppliedModelDrop = job.Correction.RemainingModelDrop')
pose=section(pose,'            if (!states.IsCreated) return;','        public void Reset()','        }\n\n        // 丢弃姿态图与登记的 IK 请求，供新的状态代次重新开始。\n')
pose=pose.replace('foreach (var source in sources) source?.Clear();','Ik.ClearRequests();').replace('            modelHeight?.SetDrop(0f);\n            if (!keepTargets) foreach (var source in sources) source?.ForgetGrip();','            Ik.ResetHistory(keepTargets);')
pose=section(pose,'            if (states.IsCreated)','        public void Dispose()', '            Ik.DisposeBuffers();\n        }\n\n        // 释放最终姿态节点与其全部原生缓冲。\n')
pose=pose.replace('装配局部 IK 输入和模型高度状态，以便接入播放图。','装配原始采样、姿态修正和最终 IK 所需的模块。').replace('将惯性和 IK 作业连接到直接片段图，在分层混合后输出最终姿势。','管理分层混合后的姿态作业及其绑定、分段准备、冻结与重装。').replace('ConnectIk','ConnectPose').replace('DisconnectIk','DisconnectPose').replace('IK 绑定失败','姿态作业绑定失败')
put('CharacterPosePipeline',pose)
for name in ['CharacterAnimationPipeline','CharacterAnimationPlayer','CharacterBehaviorAnimator','Modules/CharacterAnimationModules','CharacterClipPlayback']:
    a=read(name)
    a=a.replace('CharacterAnimationIk','CharacterPosePipeline').replace('CreateIk','CreatePose').replace('ConnectIk','ConnectPose').replace('DisconnectIk','DisconnectPose')
    if name=='CharacterBehaviorAnimator': a=a.replace('pipeline.Ik.','pipeline.Pose.Ik.')
    elif name=='CharacterAnimationPlayer': a=a.replace('CharacterPosePipeline ik','CharacterPosePipeline pose').replace('ik.Prepare','pose.Prepare')
    elif name in ['Modules/CharacterAnimationModules','CharacterAnimationPipeline']:
        a=re.sub(r'\bIk\b','Pose',a)
    a=a.replace('将最终 IK 接入片段图','将姿态修正作业接入片段图').replace('接入最终 IK','接入姿态修正作业').replace('移除 IK 节点','移除姿态修正节点')
    put(name,a)
