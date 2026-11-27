gripLogic=part(inp,'        // 按动作实例','        private bool Select').replace('private CharacterIkCandidate GripCandidate(in CharacterAnimationComposition actions, CharacterClipPlayback clips)','internal CharacterIkCandidate Sample(CharacterIkTuning channel, in CharacterAnimationComposition actions, CharacterClipPlayback clips)').replace('SelectedGrip(actions)','SelectedGrip(actions, channel.GripActionKey)').replace('private CharacterAnimationDemand SelectedGrip(in CharacterAnimationComposition actions)','// 依次读取可见上身、叠加层和全身的握持动作。\n        private static CharacterAnimationDemand SelectedGrip(in CharacterAnimationComposition actions, string key)')
pos=gripLogic.index('        private static CharacterAnimationDemand SelectedGrip')
gripLogic=gripLogic[:pos]+gripLogic[pos:].replace('channel.GripActionKey','key')
hand=part(job,'        // 保存惯性前','        // 根据手部目标').replace('        private struct HandReference','        internal struct HandReference')
hand=hand.replace('            public void TransportDefault(AnimationStream stream, TransformStreamHandle tip, ref Pose target)','            // 按胸臂惯性产生的手端变化搬运默认动画标记。\n            public void TransportDefault(in Pose tip, ref Pose target)').replace('tip.GetPosition(stream)','tip.position').replace('tip.GetRotation(stream)','tip.rotation')
grip='''    // 记录上一次可见的握持动作和权重，按真实片段贡献生成握持候选。
    internal struct CharacterGripIk
    {
        private long lastGrip;
        private float lastVisibleWeight;

'''+gripLogic+'''        // 离开可见握持时重置旧目标淡出量，新的目标从自身位置进入。
        internal static float Permission(in CharacterIkCandidate best, float fallback, ref CharacterIkState state)
        {
            if (state.WasVisibleGrip != 0 && best.UsesVisibleGrip == 0)
            {
                state.Envelope = 0f;
                state.Source = CharacterIkSource.None;
            }
            state.WasVisibleGrip = best.UsesVisibleGrip;
            return best.UsesVisibleGrip != 0 ? best.VisiblePermission : fallback;
        }

'''+hand+'''        // 采集惯性前的标记和手端，或用可见候选权重限制当前胸臂惯性。
        internal static HandReference ReadHand(AnimationStream stream, in CharacterIkBinding binding, in CharacterIkWeights weights,
            in CharacterIkCandidate best, in CharacterIkState state)
        {
'''+part(job,'            var hand = new HandReference','        // 排除动画流').replace('new Pose(binding.Tip.GetPosition(stream), binding.Tip.GetRotation(stream))','binding.ReadTip(stream)').replace('new Pose(binding.AnimationTarget.GetPosition(stream), binding.AnimationTarget.GetRotation(stream))','binding.ReadMarker(stream)').replace('BlendStep(binding','BlendStep(weights').replace('binding.Permission','weights.Permission').replace('binding.PositionWeight','weights.PositionWeight').replace('binding.RotationWeight','weights.RotationWeight')+'    }'
put('CharacterGripIk',wrap('using UnityEngine;\nusing UnityEngine.Animations;',grip))
# Main-thread requests, separating provider storage and request lifetime from grip/probe sampling.
req='''    // 按来源和提供者登记目标请求，保留实例编号以拒绝过期的提交或撤销。
    internal sealed class CharacterIkRequests
    {
        private readonly Transform owner, skeleton;
'''+part(inp,'        private readonly CharacterIkRequest[] requests','        // 安装该模型').replace('        private RaycastHit[] hits = new RaycastHit[24];\n','')+'''        // 记录角色与模型边界，用于过滤自身层级中的场景目标。
        internal CharacterIkRequests(Transform owner, Transform skeleton)
        {
            this.owner = owner;
            this.skeleton = skeleton;
        }

        // 验证目标与单调实例号，再登记默认来源或具名场景提供者。
'''+part(inp,'        public bool Submit','        // 采集动画握点').replace('string key, string provider)','string key, string provider, bool hasMarker)').replace('bones.AnimationTarget == null','!hasMarker').replace('        public bool Withdraw','        // 只撤销相同提供者下仍然活跃的指定实例。\n        public bool Withdraw').replace('        public void Clear()','        // 重置后允许每个提供者重新从初始实例号提交。\n        public void Clear()').replace('            ForgetGrip();\n','')+'''        // 判定指定来源是否由显式请求接管。
        internal bool Active(CharacterIkSource source) => requests[(int)source].Active;

        // 将场景默认目标和具名提供者的世界位姿写入本段候选缓冲。
        internal void SampleScene(in CharacterAnimationComposition actions, Transform defaultTarget,
            NativeArray<CharacterIkCandidate> candidates, ref int cursor, ref CharacterIkInput input)
        {
'''+part(inp,'            ref CharacterIkRequest sceneRequest','            if (Select(CharacterIkSource.Ground').replace('bones.SceneTarget','defaultTarget')+part(inp,'            input.SceneStart = cursor;','            return input;')+'        }\n\n'+inp[inp.index('        private bool Select('):inp.rindex('    }')].replace('        private bool Select','        // 在选中动作归属下选择显式请求，缺省时使用通道配置。\n        internal bool Select').replace('        private bool IsExternal','        // 场景目标应来自角色与模型层级之外。\n        private bool IsExternal').replace('        private static bool IsSource','        // 核对来源是否有对应的请求槽。\n        private static bool IsSource')+'    }'
put('CharacterIkRequests',wrap('using System;\nusing System.Collections.Generic;\nusing Unity.Collections;\nusing UnityEngine;',req))
inputsource='''    // 汇集动画握持、外部场景目标和地面探测的主线程输入，同段骨姿由作业补充。
    internal sealed class CharacterIkInputSource
    {
        private CharacterGripIk grip;
        private CharacterIkTuning channel;
        private CharacterIkBones bones;
        private readonly Transform owner, skeleton;
        private float walkableSlope;
        private RaycastHit[] hits = new RaycastHit[24];
        internal CharacterIkRequests Requests { get; }
        internal bool HasMarker => bones.AnimationTarget;

        // 安装该模型的来源和独立的请求登记表。
        public CharacterIkInputSource(CharacterBoundIkChannel binding, Transform owner, Transform skeleton, float walkableSlope)
        {
            this.owner = owner;
            this.skeleton = skeleton;
            Requests = new CharacterIkRequests(owner, skeleton);
            Rebind(binding, walkableSlope);
        }

'''+part(inp,'        // 重装同一通道','        public bool Submit')+'''        // 清除旧动作的可见握持历史，已登记请求保持有效。
        public void ForgetGrip() => grip = default;

        // 采集各独立来源本段的候选，保留显式动画请求对默认握点的优先权。
        public CharacterIkInput Sample(in CharacterAnimationComposition actions, NativeArray<CharacterIkCandidate> candidates,
            ref int cursor, in CharacterFootProbeMotion probeMotion, float probeDelta, in CharacterFootWarp warp, CharacterClipPlayback clips)
        {
            var input = new CharacterIkInput();
            if (bones.AnimationTarget)
            {
                var visible = grip.Sample(channel, actions, clips);
                bool selected = Requests.Select(CharacterIkSource.Animation, channel.UseAnimationTarget, 0, actions, out var animation);
                if (visible.Source != CharacterIkSource.None && (!selected || !Requests.Active(CharacterIkSource.Animation)))
                    input.Animation = visible;
                else if (selected) input.Animation = animation;
            }
            Requests.SampleScene(actions, bones.SceneTarget, candidates, ref cursor, ref input);
            if (Requests.Select(CharacterIkSource.Ground, channel.ProbeGround, 0, actions, out input.Ground))
                CharacterGroundIk.Sample(channel, owner, skeleton, walkableSlope, probeMotion, probeDelta, warp, ref hits, ref input);
            else input.Ground = default;
            return input;
        }
    }
}'''
put('CharacterIkInput',inp[:inp.index('    // 将部位请求')].replace('using System;\n','').replace('using System.Collections.Generic;\n','').replace('using UnityEngine.SceneManagement;\n','')+inputsource+'\n')
# Native correction frame is a view over the current evaluation buffers, passed to the pose job once.
cor='''    // 使用当前姿态与各来源候选制定 IK 计划，保留跨求值的目标过渡和地面接触状态。
    internal struct CharacterIkCorrection
    {
        [ReadOnly] public NativeArray<CharacterIkWeights> Weights;
        [ReadOnly] public NativeArray<CharacterIkInput> Inputs;
        [ReadOnly] public NativeArray<CharacterIkCandidate> Scenes;
        public NativeArray<CharacterIkState> States;
        public int LeftHand, RightHand;
        public CharacterFootPlantFrame FootPlant;
        public byte AdjustModelHeight;
        public float AppliedModelDrop, RemainingModelDrop;
        public TransformSceneHandle ProbeBasis;

        // 在惯性写入前采集手部握持影响和默认动画标记。
        public CharacterGripIk.HandReference ReadHand(AnimationStream stream, in CharacterIkSkeleton skeleton, int index)
        {
            if (index < 0) return default;
            var binding = skeleton.Bindings[index];
            if (!binding.IsReady(stream)) return default;
            var best = SelectTarget(stream, binding, Inputs[index], States[index]);
            return CharacterGripIk.ReadHand(stream, binding, Weights[index], best, States[index]);
        }

        // 将本段所有通道规划完毕后才交还姿态流程写骨；冻结时仅使失效链丢弃旧计划。
        public void BuildPlans(AnimationStream stream, CharacterIkSkeleton skeleton, in CharacterFootWarp warp,
            Vector3 rootPivot, Vector3 legPivot, in CharacterFootObservation feet, in CharacterGripIk.HandReference left,
            in CharacterGripIk.HandReference right, bool frozen)
        {
            for (int i = 0; i < skeleton.Bindings.Length; i++)
            {
                var binding = skeleton.Bindings[i];
                if (!binding.IsReady(stream))
                {
                    States[i] = default;
                    skeleton.Plans[i] = default;
                    continue;
                }
                if (frozen) continue;
                var weights = Weights[i];
                var input = Inputs[i];
                var state = States[i];
                var tip = binding.ReadTip(stream);
                var ground = CharacterGroundIk.Observe(stream, ProbeBasis, tip.position, warp, rootPivot, legPivot,
                    weights.IsFoot, ref input, ref state.Ground);
                var hand = i == LeftHand ? left : i == RightHand ? right : default;
                var best = hand.Selected ? hand.Best : SelectTarget(stream, binding, input, state);
                var plan = Resolve(stream, binding, weights, input, tip, ground, best, hand, feet, ref state);
                States[i] = state;
                skeleton.Plans[i] = plan;
            }
        }

        // 仲裁单通道的过渡并应用选中来源的目标规则，生成最终计划和下段承重需求。
        private CharacterIkPlan Resolve(AnimationStream stream, in CharacterIkBinding binding, in CharacterIkWeights weights,
            in CharacterIkInput input, in Pose tip, in CharacterGroundFootHit ground, in CharacterIkCandidate best,
            in CharacterGripIk.HandReference hand, in CharacterFootObservation feet, ref CharacterIkState state)
        {
            float gate = CharacterGripIk.Permission(best, weights.Permission, ref state);
            float step = CharacterIkDecision.BlendStep(weights, stream.deltaTime);
            bool groundedFoot = weights.IsFoot && state.Source == CharacterIkSource.Ground;
            bool changed = CharacterIkDecision.Advance(best, step, gate, groundedFoot,
                groundedFoot && best.Source == CharacterIkSource.Ground, ref state);
            if (changed || best.Source == CharacterIkSource.None) state.Ground.Release();
            float length = 0f;
            Vector3 hip = default;
            if (best.Source != CharacterIkSource.None)
            {
                var target = best.Target;
                if (best.Source == CharacterIkSource.Animation)
                {
                    target = binding.ReadMarker(stream);
                    hand.TransportDefault(tip, ref target);
                }
                else if (best.Source == CharacterIkSource.Ground)
                {
                    CharacterFootReach reach = default;
                    if (weights.IsFoot)
                    {
                        length = binding.Measure(stream, tip.position, out hip);
                        reach = new CharacterFootReach(hip, length, CharacterModelHeight.AllowsDrop(ground) ? RemainingModelDrop : 0f);
                    }
                    target = CharacterGroundIk.Place(best, ground, input.Probes.Up, tip, weights, FootPlant,
                        gate, state.Envelope, stream.deltaTime, reach, ref state.Ground);
                }
                CharacterIkDecision.BlendTarget(target, step, ref state);
            }
            var plan = CharacterIkDecision.Plan(weights, gate, state);
            if (AdjustModelHeight != 0 && weights.IsFoot)
                state.Ground.RequiredModelDrop = CharacterModelHeight.RequiredDrop(hip, length, weights.Limb,
                    state.Ground, plan, ground, feet, gate, AppliedModelDrop);
            return plan;
        }

'''+part(job,'        // 排除动画流','        // 使用基础动画')+'    }'
put('CharacterIkCorrection',wrap('using Unity.Collections;\nusing UnityEngine;\nusing UnityEngine.Animations;',cor))
pj=part(job,'    /// <summary>先采集','            for (int i = 0; i < Bindings.Length; i++)')
pj=section(pj,'        [ReadOnly] public NativeArray<CharacterIkBinding>','        [ReadOnly] public NativeArray<CharacterPoseBoneBinding>','''        public CharacterIkSkeleton Skeleton;
        public CharacterIkCorrection Correction;
''')
pj=section(pj,'        public CharacterFootPlantFrame FootPlant','        private byte frozen;','')
pj=pj.replace('        public int LeftHand, RightHand;\n','').replace('CharacterIkJob','CharacterPoseJob').replace('先采集所有骨链的基础姿态并完成判断，再按绑定顺序写入同一动画流。','从原始片段姿态开始，按固定顺序应用各表现模块，最终由 IK 骨架执行计划。').replace('        public void ProcessRootMotion','        // 该流程仅修饰姿态，根运动由既有播放图保留。\n        public void ProcessRootMotion').replace('ReadHand(stream, LeftHand)','Correction.ReadHand(stream, Skeleton, Correction.LeftHand)').replace('ReadHand(stream, RightHand)','Correction.ReadHand(stream, Skeleton, Correction.RightHand)').replace('default(HandReference)','default(CharacterGripIk.HandReference)')
pj=pj.replace('            if (!Frozen && Feet.History.Length > 0)','            if (!Frozen && Feet.History.Length > 0)').replace('            if (!Frozen && Transition.Bones.Length > 0)','            var rawFeet = Feet.History.Length > 0 ? Feet.History[0] : default;\n            if (!Frozen && Transition.Bones.Length > 0)').replace('                var contact = Feet.History.Length > 0 ? Feet.History[0] : default;\n','').replace('contact.Pose.Contact, contact.ContactConfidence','rawFeet.Pose.Contact, rawFeet.ContactConfidence')
put('CharacterPoseJob',wrap('using Unity.Collections;\nusing UnityEngine;\nusing UnityEngine.Animations;',pj+'''            Correction.BuildPlans(stream, Skeleton, warp, rootPivot, pivot, rawFeet, left, right, Frozen);
            Skeleton.Apply(stream);
        }
    }'''))
