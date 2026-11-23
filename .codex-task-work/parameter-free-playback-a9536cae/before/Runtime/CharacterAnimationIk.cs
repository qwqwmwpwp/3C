using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在已有控制器输出之后追加一次姿态求值，播放状态仍由原控制器持有。</summary>
    internal sealed class CharacterAnimationIk : IDisposable
    {
        private readonly Animator animator;
        private readonly CharacterIkChannel[] channels;
        private readonly CharacterIkInputSource[] sources;
        private readonly string[] issues;
        private readonly Transform owner;
        private readonly float teleportDistance;
        private readonly CharacterPoseInertiaSettings poseInertia;
        private PlayableGraph graph;
        private AnimationScriptPlayable playable;
        private NativeArray<CharacterIkBinding> bindings;
        private NativeArray<CharacterIkInput> inputs;
        private NativeArray<CharacterIkCandidate> sceneCandidates;
        private NativeArray<CharacterIkState> states;
        private NativeArray<CharacterFootObservation> feet;
        private readonly bool sampleFeet;
        private NativeArray<CharacterPoseBoneBinding> poseBindings;
        private NativeArray<CharacterPoseBoneHistory> poseHistory;
        private CharacterAnimationDemand previousFull, previousUpper;
        private int previousFullState, previousUpperState, poseRevision;
        private bool hasSelection;
        private bool hasMotionVersion;
        private int motionVersion;
        private long skipAccelerationFrame;
        private long discardedStopInstance;
        private RuntimeAnimatorController controller;
        private Avatar avatar;
        private bool triedBinding, hasPosition, frozen;
        private Vector3 lastPosition;

        public CharacterAnimationIk(Animator animator, Transform owner, CharacterIkChannel[] channels,
            float teleportDistance, CharacterPoseInertiaSettings poseInertia = null, bool sampleFeet = false)
        {
            this.animator = animator;
            this.owner = owner;
            this.channels = channels ?? Array.Empty<CharacterIkChannel>();
            this.teleportDistance = teleportDistance;
            this.poseInertia = poseInertia;
            this.sampleFeet = sampleFeet;
            sources = new CharacterIkInputSource[this.channels.Length];
            issues = new string[sources.Length];
            for (int i = 0; i < sources.Length; i++)
                if (this.channels[i] != null && IndexOf(this.channels[i].Limb) == i)
                    sources[i] = new CharacterIkInputSource(this.channels[i], owner, animator ? animator.transform : null);
        }

        internal bool HasTeleported => hasPosition && teleportDistance > 0f &&
            (owner.position - lastPosition).sqrMagnitude > teleportDistance * teleportDistance;

        public CharacterFootObservation ReadFeet(CharacterAnimationMotion motion)
        {
            if (!feet.IsCreated || feet.Length == 0 || !hasMotionVersion || motionVersion != motion.SimulationVersion ||
                frozen || avatar != animator.avatar || controller != animator.runtimeAnimatorController ||
                HasTeleported)
                return default;
            return feet[0];
        }

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
                if (channels[i] != null && channels[i].Limb == limb) return i;
            return -1;
        }

        public void Prepare(in CharacterAnimationComposition selection, CharacterAnimationMotion motion,
            in CharacterAnimationPlaybackPlan plan)
        {
            if (sources.Length == 0 && poseInertia == null) return;
            if (!triedBinding || animator.runtimeAnimatorController != controller || animator.avatar != avatar)
                Bind();
            if (!graph.IsValid()) return;
            Vector3 position = owner.position;
            if (HasTeleported || motion.IsValid && hasMotionVersion && motionVersion != motion.SimulationVersion)
                ClearPose();
            if (motion.IsValid)
            {
                hasMotionVersion = true;
                motionVersion = motion.SimulationVersion;
            }
            lastPosition = position;
            hasPosition = true;
            bool resumed = frozen;
            SetFrozen(false);
            if (poseBindings.Length > 0)
            {
                if (!hasSelection || resumed) skipAccelerationFrame = motion.FrameNumber;
                var job = playable.GetJobData<CharacterIkJob>();
                job.Inertia = PrepareInertia(selection, motion, plan);
                playable.SetJobData(job);
            }
            EnsureCandidateCapacity();
            int cursor = 0;
            for (int i = 0; i < sources.Length; i++)
                if (bindings[i].Valid) inputs[i] = sources[i].Sample(selection, sceneCandidates, ref cursor);
        }

        private CharacterPoseInertiaFrame PrepareInertia(in CharacterAnimationComposition selection,
            CharacterAnimationMotion motion, in CharacterAnimationPlaybackPlan plan)
        {
            int full = plan.FullBody.HasState ? plan.FullBody.StateHash : 0;
            int upper = plan.UpperBody.HasState && plan.UpperBody.Weight > 0f ? plan.UpperBody.StateHash : 0;
            bool fullChanged = full != previousFullState ||
                plan.FullBody.Reason != CharacterAnimationPlaybackReason.KeepSharedResource &&
                Different(selection.FullBody, previousFull);
            bool upperChanged = upper != previousUpperState || upper != 0 && Different(selection.UpperBody, previousUpper);
            if (!hasSelection)
                discardedStopInstance = selection.FullBody.LocomotionVariant == CharacterLocomotionVariant.Stop
                    ? selection.FullBody.InstanceId : 0;
            else if (selection.FullBody.LocomotionVariant != CharacterLocomotionVariant.Stop)
                discardedStopInstance = 0;
            if (hasSelection && (fullChanged || upperChanged)) poseRevision++; // 同一播放实例的交接只消费一次。
            hasSelection = true;
            previousFullState = full;
            previousUpperState = upper;
            previousFull = selection.FullBody;
            previousUpper = selection.UpperBody;

            var frame = new CharacterPoseInertiaFrame
            {
                Revision = poseRevision, Allowance = selection.InertiaAllowance,
                Strength = Mathf.Clamp01(InputMath.NonNegative(poseInertia.Strength, 0f)),
                ChestSeconds = InputMath.NonNegative(poseInertia.ChestResponseSeconds, 0.18f),
                ArmSeconds = InputMath.NonNegative(poseInertia.ArmResponseSeconds, 0.24f),
                ChestLimit = Mathf.Min(60f, InputMath.NonNegative(poseInertia.ChestMaxDegrees, 0f)),
                ArmLimit = Mathf.Min(90f, InputMath.NonNegative(poseInertia.ArmMaxDegrees, 0f))
            };
            if (plan.StopPose.IsValid)
            {
                if (plan.StopPose.InstanceId != discardedStopInstance) // 瞬移或重绑后舍弃旧收步的入场动量。
                    PrepareStop(ref frame, plan.StopPose);
            }
            else if (motion.HasAcceleration && motion.FrameNumber != skipAccelerationFrame)
            {
                Vector3 before = motion.PreviousActualVelocity;
                Vector3 accel = motion.ActualAcceleration;
                if (Vector3.Dot(before, accel) < 0f)
                {
                    // 用制动前的速度限制倾斜，短距离轻点只产生小幅倾斜。
                    Vector3 braking = Vector3.ClampMagnitude(accel, before.magnitude / Mathf.Max(0.01f, frame.ChestSeconds));
                    Vector3 local = owner.InverseTransformDirection(braking);
                    frame.BrakeRotation = owner.TransformDirection(new Vector3(-local.z, 0f, local.x)) *
                        InputMath.NonNegative(poseInertia.BrakeDegreesPerAcceleration, 0f);
                }
            }
            return frame;
        }

        private void PrepareStop(ref CharacterPoseInertiaFrame frame, in CharacterStopPoseSample stop)
        {
            frame.StopInstance = stop.InstanceId;
            frame.StopLoad = Mathf.Clamp01(stop.Load);
            // 以松键时的实际平面速度确定摆臂方向和幅度，收步片段控制卸重。
            Vector3 travel = Vector3.ProjectOnPlane(stop.EntryVelocity, owner.up);
            float speed = travel.magnitude;
            if (speed < 0.0001f) return;
            float fullSpeed = Mathf.Max(0.1f, InputMath.NonNegative(poseInertia.StopFullSpeed, 6.5f));
            float speedWeight = Mathf.Clamp01(speed / fullSpeed);
            Vector3 direction = travel / speed;
            Vector3 swingAxis = Vector3.Cross(owner.up, direction);
            float arm = Mathf.Min(90f, InputMath.NonNegative(poseInertia.StopArmDegrees, 0f)) * speedWeight;
            frame.StopLeftArm = swingAxis * (stop.LeftSupport ? 0.5f : -1f) * arm;
            frame.StopRightArm = swingAxis * (stop.LeftSupport ? -1f : 0.5f) * arm;
        }

        private static bool Different(in CharacterAnimationDemand current, in CharacterAnimationDemand previous) =>
            current.InstanceId != previous.InstanceId || current.Key != previous.Key;

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

        private void Bind()
        {
            Invalidate();
            triedBinding = true;
            controller = animator.runtimeAnimatorController;
            avatar = animator.avatar;
            try
            {
                bindings = new NativeArray<CharacterIkBinding>(channels.Length, Allocator.Persistent);
                var parameters = animator.parameters;
                bool any = false;
                for (int i = 0; i < channels.Length; i++)
                {
                    var channel = channels[i];
                    issues[i] = BindingIssue(i, parameters);
                    if (issues[i] != null) continue;
                    bool hasGoal = channel.AnimationTarget != null && channel.AnimationTarget.IsChildOf(animator.transform)
                        && !IsDriven(channel.AnimationTarget);
                    if (channel.AnimationTarget != null && !hasGoal)
                        issues[i] = "动画目标骨不独立或不属于此骨架，已跳过该来源";
                    bindings[i] = new CharacterIkBinding { Valid = true, HasAnimationTarget = hasGoal,
                        Root = animator.BindStreamTransform(channel.Root), Mid = animator.BindStreamTransform(channel.Mid),
                        Tip = animator.BindStreamTransform(channel.Tip),
                        AnimationTarget = hasGoal ? animator.BindStreamTransform(channel.AnimationTarget) : default,
                        Curve = animator.BindStreamProperty(animator.transform, typeof(Animator), channel.WeightCurve),
                        PositionWeight = Mathf.Clamp01(channel.PositionWeight), RotationWeight = Mathf.Clamp01(channel.RotationWeight),
                        BlendSeconds = InputMath.NonNegative(channel.BlendSeconds, 0f) };
                    any = true;
                }
                var bones = poseInertia?.Bind(animator) ?? Array.Empty<CharacterPoseBoneBinding>();
                if (!any && bones.Length == 0) { DisposeBuffers(); return; }
                poseBindings = new NativeArray<CharacterPoseBoneBinding>(bones, Allocator.Persistent);
                poseHistory = new NativeArray<CharacterPoseBoneHistory>(bones.Length, Allocator.Persistent);
                inputs = new NativeArray<CharacterIkInput>(channels.Length, Allocator.Persistent);
                states = new NativeArray<CharacterIkState>(channels.Length, Allocator.Persistent);
                sceneCandidates = new NativeArray<CharacterIkCandidate>(1, Allocator.Persistent);
                int leftFoot = IndexOf(CharacterIkLimb.LeftFoot), rightFoot = IndexOf(CharacterIkLimb.RightFoot);
                var hips = sampleFeet && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
                bool validFeet = hips && leftFoot >= 0 && rightFoot >= 0 && bindings[leftFoot].Valid && bindings[rightFoot].Valid;
                feet = new NativeArray<CharacterFootObservation>(validFeet ? 1 : 0, Allocator.Persistent);
                var sampler = new CharacterFootSampler { History = feet, Left = leftFoot, Right = rightFoot };
                if (validFeet)
                {
                    sampler.Hips = animator.BindStreamTransform(hips);
                    sampler.Basis = animator.BindSceneTransform(animator.transform);
                }
                graph = PlayableGraph.Create("角色动画 IK");
                graph.SetTimeUpdateMode(animator.updateMode == AnimatorUpdateMode.UnscaledTime
                    ? DirectorUpdateMode.UnscaledGameTime : DirectorUpdateMode.GameTime);
                playable = AnimationScriptPlayable.Create(graph, new CharacterIkJob
                    { Bindings = bindings, Inputs = inputs, States = states, Scenes = sceneCandidates, Feet = sampler,
                        PoseBindings = poseBindings, PoseHistory = poseHistory,
                        LeftHand = IndexOf(CharacterIkLimb.LeftHand), RightHand = IndexOf(CharacterIkLimb.RightHand) });
                var output = AnimationPlayableOutput.Create(graph, "最终角色姿态", animator);
                output.SetAnimationStreamSource(AnimationStreamSource.PreviousInputs);
                output.SetSortingOrder(1000);
                output.SetSourcePlayable(playable);
                graph.Play();
            }
            catch (Exception error)
            {
                Invalidate();
                triedBinding = true;
                controller = animator.runtimeAnimatorController;
                avatar = animator.avatar;
                string reason = $"IK 绑定失败：{error.Message}";
                for (int i = 0; i < issues.Length; i++) issues[i] = reason;
                Debug.LogWarning(reason, animator);
            }
        }

        private string BindingIssue(int i, AnimatorControllerParameter[] parameters)
        {
            CharacterIkChannel c = channels[i];
            if (sources[i] == null) return "配置为空或部位重复";
            if (!c.Root || !c.Mid || !c.Tip || c.Root == animator.transform || c.Root == c.Mid || c.Mid == c.Tip ||
                !c.Root.IsChildOf(animator.transform) || !c.Mid.IsChildOf(c.Root) || !c.Tip.IsChildOf(c.Mid))
                return "缺少骨骼或骨链层级不匹配";
            if (!string.IsNullOrWhiteSpace(c.WeightCurve))
                foreach (var p in parameters)
                    if (p.name == c.WeightCurve && p.type == AnimatorControllerParameterType.Float && p.defaultFloat == 0f)
                        return null;
            return "许可曲线缺少同名且默认值为零的浮点参数";
        }

        private bool IsDriven(Transform goal)
        {
            foreach (var channel in channels)
                if (channel != null && channel.Root && goal.IsChildOf(channel.Root)) return true;
            return false;
        }

        public void SetFrozen(bool value)
        {
            if (!graph.IsValid()) return;
            if (controller != animator.runtimeAnimatorController || avatar != animator.avatar)
            {
                Invalidate();
                return;
            }
            if (frozen == value) return;
            if (feet.IsCreated && feet.Length > 0) feet[0] = default;
            var job = playable.GetJobData<CharacterIkJob>();
            if (value) hasMotionVersion = false;
            else
                for (int i = 0; i < poseHistory.Length; i++)
                {
                    var state = poseHistory[i];
                    state.RebaseVelocity = 1;
                    poseHistory[i] = state;
                }
            frozen = job.Frozen = value;
            playable.SetJobData(job);
        }

        public void ClearPose()
        {
            hasMotionVersion = hasPosition = false;
            if (feet.IsCreated && feet.Length > 0) feet[0] = default;
            hasSelection = false;
            if (poseHistory.IsCreated)
                for (int i = 0; i < poseHistory.Length; i++) poseHistory[i] = default;
            if (graph.IsValid())
            {
                var job = playable.GetJobData<CharacterIkJob>();
                job.Inertia = default;
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

        public void Invalidate()
        {
            if (graph.IsValid()) graph.Destroy();
            DisposeBuffers();
            Array.Clear(issues, 0, issues.Length);
            triedBinding = hasMotionVersion = hasPosition = frozen = false;
            hasSelection = false;
            poseRevision = 0;
            controller = null;
            avatar = null;
        }

        private void DisposeBuffers()
        {
            if (feet.IsCreated) feet.Dispose();
            if (poseHistory.IsCreated) poseHistory.Dispose();
            if (poseBindings.IsCreated) poseBindings.Dispose();
            if (states.IsCreated) states.Dispose();
            if (inputs.IsCreated) inputs.Dispose();
            if (sceneCandidates.IsCreated) sceneCandidates.Dispose();
            if (bindings.IsCreated) bindings.Dispose();
        }

        public void Dispose() => Invalidate();

#if UNITY_EDITOR
        internal bool TryReadSupport(CharacterIkLimb limb, out int surface, out Vector3 local,
            out Matrix4x4 toWorld, out Vector3 normal)
        {
            surface = 0;
            local = normal = default;
            toWorld = default;
            int i = IndexOf(limb);
            if (i < 0 || !states.IsCreated || !inputs.IsCreated || !channels[i].Tip) return false;
            var state = states[i];
            var ground = inputs[i];
            if (state.Gate < 0.7f || state.Source != CharacterIkSource.Ground ||
                ground.Ground.ObjectId == 0 || ground.Ground.ObjectId != state.ObjectId) return false;
            surface = ground.Ground.ObjectId;
            local = ground.WorldToGround.MultiplyPoint3x4(channels[i].Tip.position);
            toWorld = ground.GroundToWorld;
            normal = ground.GroundNormal;
            return true;
        }

        public string Describe()
        {
            if (!triedBinding) return "等待有效动画资源";
            var text = new System.Text.StringBuilder();
            if (poseInertia == null || poseInertia.Strength <= 0f)
                text.Append("惯性关闭（强度 0）");
            else if (!poseHistory.IsCreated || poseHistory.Length == 0)
                text.Append("惯性无有效骨骼绑定");
            else
            {
                Vector3 angles = Vector3.zero;
                for (int i = 0; i < poseHistory.Length; i++)
                    angles[(int)poseBindings[i].Region] += poseHistory[i].AppliedDegrees;
                text.AppendFormat("惯性强度 {0:0.00}，本帧附加转角之和：胸 {1:0.0}°，左臂 {2:0.0}°，右臂 {3:0.0}°",
                    poseInertia.Strength, angles.x, angles.y, angles.z);
            }
            if (channels.Length == 0) text.AppendLine().Append("未配置 IK 通道");
            for (int i = 0; i < channels.Length; i++)
            {
                text.AppendLine();
                string limb = channels[i]?.Limb switch { CharacterIkLimb.LeftHand => "左手", CharacterIkLimb.RightHand => "右手",
                    CharacterIkLimb.LeftFoot => "左脚", CharacterIkLimb.RightFoot => "右脚", _ => "未知部位" };
                text.Append(limb).Append("  ");
                if (issues[i] != null) text.Append(issues[i]).Append("  ");
                if (!states.IsCreated || !bindings[i].Valid) continue;
                var s = states[i];
                text.Append(s.Gate <= 0f ? "动画未开放" : s.CandidateMask == 0 ? "无有效目标" : "已开放");
                text.Append("，候选");
                if ((s.CandidateMask & 2) != 0) text.Append(" 动画");
                if ((s.CandidateMask & 4) != 0) text.Append(" 场景×").Append(s.SceneCount);
                if ((s.CandidateMask & 8) != 0) text.Append(" 地面");
                text.Append("，采用").Append(s.Source switch { CharacterIkSource.Animation => "动画目标骨",
                    CharacterIkSource.Scene => "场景目标", CharacterIkSource.Ground => "地面", _ => "无" });
                if (s.Source == CharacterIkSource.Scene) text.Append(" #").Append(s.ObjectId).Append(" / 实例 ").Append(s.Instance);
                text.AppendFormat("，许可 {0:0.00}，位置 {1:0.00}，旋转 {2:0.00}", s.Gate, s.Plan.PositionWeight, s.Plan.RotationWeight);
                if (s.Locked) text.Append("，接触已锁定");
            }
            return text.ToString();
        }
#endif
    }
}
