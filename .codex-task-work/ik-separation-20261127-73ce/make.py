from pathlib import Path
import re, uuid, shutil
task=Path('.codex-task-work/ik-separation-20261127-73ce')
anim=Path('Assets/Code/Character/Anim')
base=task/'baseline'/anim
staged=task/'stage'
staged.mkdir(exist_ok=True)
def read(name):
    return (base/(name+'.cs')).read_text(encoding='utf-8-sig')
def put(name,source):
    p=staged/(name+'.cs'); p.parent.mkdir(exist_ok=True,parents=True)
    p.write_text(source,encoding='utf8')
def wrap(imports,body):
    return imports+'\n\nnamespace ThreeC.CharacterBehavior\n{\n'+body.rstrip()+'\n}\n'
def part(s,start,end):
    return s[s.index(start):s.index(end,s.index(start))]
def section(s,start,end,repl):
    return s.replace(part(s,start,end),repl)
job=read('CharacterIkJob')
old=read('CharacterAnimationIk')
inp=read('CharacterIkInput')
bind=part(job,'    // 保存用于','    // 保存各骨链')
bind=section(bind,'        public CharacterIkLimb Limb','        // 核对','')
bind=bind.replace('        public float Permission => ClipPermission > 0f ? Mathf.Min(ClipPermission, 1f) : 0f;', '''        // 读取当前骨链末端的世界姿态，供各修正来源共同使用。
        public Pose ReadTip(AnimationStream stream) => new Pose(Tip.GetPosition(stream), Tip.GetRotation(stream));

        // 读取本次动画流中标记的世界姿态。
        public Pose ReadMarker(AnimationStream stream) => new Pose(AnimationTarget.GetPosition(stream), AnimationTarget.GetRotation(stream));

        // 用修正后的当前姿态测量链长，供目标可达性判定使用。
        public float Measure(AnimationStream stream, Vector3 tip, out Vector3 root)
        {
            root = Root.GetPosition(stream);
            Vector3 mid = Mid.GetPosition(stream);
            return Vector3.Distance(root, mid) + Vector3.Distance(mid, tip);
        }''').replace('        public bool CanReadTarget','\n        // 检查当前流中的动画标记是否有效。\n        public bool CanReadTarget').replace('    internal struct CharacterIkPlan','    // 描述最终 IK 对单条骨链的世界目标及位置、旋转权重。\n    internal struct CharacterIkPlan').replace('及当前片段曲线许可','和可选目标标记')
solve=job[job.index('        // 使用基础动画'):job.rindex('    }')]
rig='''    // 绑定模型骨链，持有各链的求解计划，并集中把计划写回当前动画流。
    internal struct CharacterIkSkeleton
    {
        [ReadOnly] public NativeArray<CharacterIkBinding> Bindings;
        public NativeArray<CharacterIkPlan> Plans;

        // 为模型的全部通道分配固定索引，未装配的通道保留空绑定。
        public void Allocate(int count)
        {
            Bindings = new NativeArray<CharacterIkBinding>(count, Allocator.Persistent);
            Plans = new NativeArray<CharacterIkPlan>(count, Allocator.Persistent);
        }

        // 在 Animator 下绑定层级合法的三关节链，目标标记不能位于任何受驱动骨链中。
        public bool Bind(int index, Animator animator, in CharacterIkBones bones, CharacterBoundIkChannel[] channels)
        {
            if (!bones.Root || !bones.Mid || !bones.Tip || bones.Root == animator.transform ||
                bones.Root == bones.Mid || bones.Mid == bones.Tip || !bones.Root.IsChildOf(animator.transform) ||
                !bones.Mid.IsChildOf(bones.Root) || !bones.Tip.IsChildOf(bones.Mid)) return false;
            bool marker = bones.AnimationTarget && bones.AnimationTarget.IsChildOf(animator.transform);
            if (marker)
                foreach (var channel in channels)
                    if (channel.Bones.Root && bones.AnimationTarget.IsChildOf(channel.Bones.Root)) { marker = false; break; }
            Bindings[index] = new CharacterIkBinding { Valid = true, HasAnimationTarget = marker,
                Root = animator.BindStreamTransform(bones.Root), Mid = animator.BindStreamTransform(bones.Mid),
                Tip = animator.BindStreamTransform(bones.Tip),
                AnimationTarget = marker ? animator.BindStreamTransform(bones.AnimationTarget) : default };
            return true;
        }

        // 全部目标计划就绪后，按原绑定顺序求解并写回骨骼。
        public void Apply(AnimationStream stream)
        {
            for (int i = 0; i < Bindings.Length; i++)
            {
                var plan = Plans[i];
                if (plan.PositionWeight > 0f || plan.RotationWeight > 0f)
                    Solve(stream, Bindings[i], plan);
            }
        }

        // 播放图断开后释放骨链句柄与计划缓冲。
        public void Dispose()
        {
            if (Plans.IsCreated) Plans.Dispose();
            if (Bindings.IsCreated) Bindings.Dispose();
        }

'''+solve+'    }'
put('CharacterIkSkeleton',wrap('using Unity.Collections;\nusing UnityEngine;\nusing UnityEngine.Animations;',bind+rig))
state=part(job,'    // 保存各骨链','    /// <summary>只根据')
state=section(state,'        public float Gate','    }','''        public float Envelope, Transition;
        public byte WasVisibleGrip;
        public Pose Target, From;
        public CharacterGroundIkState Ground;
''')
state=state.replace('保存各骨链的目标过渡、平台锁点和当帧求解计划','保存各通道的目标身份、过渡进度以及地面与握持的历史')
decision=part(job,'        public static float BlendStep','    /// <summary>先采集').replace('in CharacterIkBinding binding','in CharacterIkWeights binding')
decision=decision.replace('        public static float BlendStep','        // 将本段持续时间换算成目标过渡进度。\n        public static float BlendStep').replace('        private static bool IsCurrent','        // 同一来源、实例、对象和提供者才是持续中的同一目标。\n        internal static bool IsCurrent')
arb='''    // 保存每个通道的配置权重与本次片段的实际许可。
    internal struct CharacterIkWeights
    {
        public CharacterIkLimb Limb;
        public float PositionWeight, RotationWeight, BlendSeconds, ClipPermission;
        public bool IsFoot => Limb == CharacterIkLimb.LeftFoot || Limb == CharacterIkLimb.RightFoot;
        public float Permission => ClipPermission > 0f ? Mathf.Min(ClipPermission, 1f) : 0f;
    }

'''+state+'''    // 选出唯一目标，维护同目标的连续过渡，最后发布骨架可直接使用的权重和位姿。
    internal static class CharacterIkDecision
    {
        // 进入新目标或淡出旧目标，返回本次候选是否改变。
        public static bool Advance(in CharacterIkCandidate best, float step, float gate, bool releaseNow,
            bool directSwitch, ref CharacterIkState state)
        {
            if (best.Source == CharacterIkSource.None)
            {
                state.Envelope = releaseNow ? 0f : Mathf.MoveTowards(state.Envelope, 0f, step);
                if (gate <= 0f || state.Envelope <= 0f)
                {
                    state.Source = CharacterIkSource.None;
                    state.Envelope = 0f;
                }
                return false;
            }
            bool changed = !IsCurrent(best, state);
            if (changed)
            {
                state.From = state.Target;
                state.Transition = state.Envelope > 0f && !directSwitch ? 0f : 1f;
            }
            state.Source = best.Source;
            state.Instance = best.Instance;
            state.ObjectId = best.ObjectId;
            state.Slot = best.Slot;
            state.Envelope = Mathf.MoveTowards(state.Envelope, 1f, step);
            return changed;
        }

        // 将已修正的目标从切换起点过渡到当前位置。
        public static void BlendTarget(Pose target, float step, ref CharacterIkState state)
        {
            state.Transition = Mathf.MoveTowards(state.Transition, 1f, step);
            state.Target = state.Transition >= 1f ? target : new Pose(
                Vector3.Lerp(state.From.position, target.position, state.Transition),
                Quaternion.Slerp(state.From.rotation, target.rotation, state.Transition));
        }

        // 把目标和两种实际权重写成不依赖目标来源的计划。
        public static CharacterIkPlan Plan(in CharacterIkWeights weights, float gate, in CharacterIkState state)
        {
            float weight = gate * state.Envelope;
            return new CharacterIkPlan { Target = state.Target, PositionWeight = weights.PositionWeight * weight,
                RotationWeight = weights.RotationWeight * weight };
        }

'''+decision
put('CharacterIkDecision',wrap('using Unity.Collections;\nusing UnityEngine;',arb))
ground='''    // 保存单通道的地面锚点、踏步历史、探测速度与下次模型降高需求。
    internal struct CharacterGroundIkState
    {
        private byte locked;
        public bool Locked { get => locked != 0; set => locked = (byte)(value ? 1 : 0); }
        public Pose LocalAnchor;
        public CharacterFootProbeMotion ProbeMotion;
        public CharacterFootPlant FootPlant;
        public float RequiredModelDrop;

        // 放弃当前平台上的锁点和踏步历史，后续探测仍使用连续运动采样。
        public void Release()
        {
            Locked = false;
            FootPlant = default;
        }
    }

    // 将物理探测与本段动画流中的肢体姿态对齐，处理平台锁点、足底可达范围和模型降高。
    internal static class CharacterGroundIk
    {
        private const float LockGate = 0.7f;
        private const float ReleaseGate = 0.3f;

        // 在主线程采集物理探线，足部等当前踝姿就绪后再选踏面。
        internal static void Sample(CharacterIkTuning channel, Transform owner, Transform skeleton, float slope,
            in CharacterFootProbeMotion motion, float delta, in CharacterFootWarp warp, ref RaycastHit[] hits, ref CharacterIkInput input)
        {
            input.Probes.Cast(owner.gameObject.scene.GetPhysicsScene(), channel, owner, skeleton, slope, motion, delta, warp, ref hits);
            if (channel.Limb != CharacterIkLimb.LeftFoot && channel.Limb != CharacterIkLimb.RightFoot)
                input.Probes.AcceptCenter(ref input.Ground);
        }

        // 在最终 IK 前记录真实踝位并选择其下方的支撑面。
        internal static CharacterGroundFootHit Observe(AnimationStream stream, TransformSceneHandle basis, Vector3 ankle,
            in CharacterFootWarp warp, Vector3 rootPivot, Vector3 legPivot, bool isFoot,
            ref CharacterIkInput input, ref CharacterGroundIkState state)
        {
            CharacterGroundFootHit hit = default;
            if (isFoot)
            {
                state.ProbeMotion.Observe(stream, basis, ankle, warp, rootPivot, legPivot);
                if (input.Ground.Source == CharacterIkSource.Ground)
                    input.Probes.Resolve(ankle, ref input.Ground, out hit);
            }
            else if (input.Ground.Source == CharacterIkSource.Ground) hit = input.Probes.Center;
            return hit;
        }

        // 按最新末端和踏面高度对齐目标，然后更新脚或其他肢体的平台局部锁点。
        internal static Pose Place(in CharacterIkCandidate best, in CharacterGroundFootHit ground, Vector3 up,
            in Pose tip, in CharacterIkWeights weights, in CharacterFootPlantFrame frame, float gate, float envelope,
            float dt, in CharacterFootReach reach, ref CharacterGroundIkState state)
        {
            var target = best.Target;
            float rise = Vector3.Dot(target.position - tip.position, ground.Normal) / Vector3.Dot(up, ground.Normal);
            target.position = tip.position + up * rise;
            target.rotation *= tip.rotation;
            if (weights.IsFoot)
            {
                float positionWeight = weights.PositionWeight * gate * envelope;
                CharacterFootPlant.Place(ref state, frame, weights.Limb, ground, tip.position, gate, positionWeight, dt, reach, ref target);
            }
            else
            {
'''+''.join(line[4:] for line in part(job,'                    // 非足部通道','                }\n                state.Transition').splitlines(keepends=True))+'''            }
            return target;
        }
    }'''
put('CharacterGroundIk',wrap('using UnityEngine;\nusing UnityEngine.Animations;\nusing UnityEngine.SceneManagement;',ground))
put('CharacterFootPlant',read('CharacterFootPlant').replace('ref CharacterIkState state','ref CharacterGroundIkState state'))
height=read('CharacterModelHeight').replace('states[i].RequiredModelDrop','states[i].Ground.RequiredModelDrop').replace('using UnityEngine.Animations;\n','')
height=section(height,'        public static float RequiredDrop','            Vector3 gap','''        public static float RequiredDrop(Vector3 hip, float length, CharacterIkLimb limb, in CharacterGroundIkState state,
            in CharacterIkPlan plan, in CharacterGroundFootHit ground, in CharacterFootObservation feet, float gate, float appliedDrop)
        {
            if (!state.Locked || !AllowsDrop(ground) || plan.PositionWeight <= 0f) return 0f;
            float support = feet.SupportWeight(limb, gate) * plan.PositionWeight;
            if (support <= 0f) return 0f;
''').replace('state.Target.position','plan.Target.position').replace('        public void Advance','        // 根据上次求值发布的各腿需求推进本段模型高度。\n        public void Advance')
put('CharacterModelHeight',height)
