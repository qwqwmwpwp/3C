using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在片段混合之后求解惯性和 IK，兼容已有控制器输出。</summary>
    internal sealed class CharacterAnimationIk : IDisposable
    {
        private readonly Animator animator;
        private readonly CharacterAnimationPlayer player;
        private CharacterClipPlayback clipGraph;
        private readonly CharacterBoundIkChannel[] channels;
        private readonly CharacterIkInputSource[] sources;
        private readonly string[] issues;
        private readonly Transform owner;
        private readonly CharacterModelHeight modelHeight;
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
        private readonly bool hasFootChannels;
        private NativeArray<CharacterPoseBoneBinding> poseBindings;
        private NativeArray<CharacterPoseBoneHistory> poseHistory;
        private CharacterInertiaObservation inertiaObservation;
        private bool hasMotionVersion;
        private int motionVersion;
        private RuntimeAnimatorController controller;
        private Avatar avatar;
        private bool triedBinding, frozen;
#if UNITY_EDITOR
        private CharacterAnimationEvaluationHistory Evaluations { get; set; }
#endif

        public CharacterAnimationIk(Animator animator, CharacterAnimationPlayer player, Transform owner, CharacterBoundIkChannel[] channels,
            float walkableSlope, CharacterInertiaTuning poseInertia, CharacterInertiaBones inertiaBones, bool sampleFeet,
            float modelDropLimit, CharacterAnimationIk previous = null)
        {
            this.animator = animator;
            this.player = player;
            this.owner = owner;
            this.channels = channels ?? Array.Empty<CharacterBoundIkChannel>();
            hasFootChannels = IndexOf(CharacterIkLimb.LeftFoot) >= 0 || IndexOf(CharacterIkLimb.RightFoot) >= 0;
            if (hasFootChannels)
                modelHeight = CharacterModelHeight.Bind(animator, owner, modelDropLimit);
            this.poseInertia = poseInertia;
            this.inertiaBones = inertiaBones;
            this.sampleFeet = sampleFeet;
            sources = new CharacterIkInputSource[this.channels.Length];
            issues = new string[sources.Length];
            for (int i = 0; i < sources.Length; i++)
                if (this.channels[i].Tuning != null && IndexOf(this.channels[i].Tuning.Limb) == i)
                {
                    var tuning = this.channels[i].Tuning;
                    int old = previous?.IndexOf(tuning.Limb) ?? -1;
                    sources[i] = old >= 0 && ReferenceEquals(previous.channels[old].Tuning, tuning)
                        ? previous.sources[old] : new CharacterIkInputSource(this.channels[i], owner, animator ? animator.transform : null, walkableSlope);
                }
        }

        // 改装一个肢体只替换它自己的输入；为新作业保留其他通道的世界锚点、脚姿和惯性历史。
        internal void TakeOver(CharacterAnimationIk previous)
        {
            bool live = previous.graph.IsValid() && previous.avatar == animator.avatar &&
                previous.controller == player.Controller && !player.NeedsRebind;
            if (!live) { previous.Dispose(); return; }
            var saved = previous.states.ToArray();
            var foot = previous.feet.Length > 0 ? previous.feet[0] : default;
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
                if (old >= 0 && ReferenceEquals(channels[i].Tuning, previous.channels[old].Tuning)) states[i] = saved[old];
            }
            modelHeight?.SetDrop(previousDrop);
            if (feet.Length > 0) feet[0] = foot;
            if (history != null && poseHistory.Length == history.Length)
            {
                poseHistory.CopyFrom(history);
                inertiaObservation = observation;
            }
            hasMotionVersion = hasVersion;
            motionVersion = version;
        }

        public CharacterFootObservation ReadFeet(CharacterAnimationMotion motion)
        {
            if (!feet.IsCreated || feet.Length == 0 || !hasMotionVersion || motionVersion != motion.SimulationVersion ||
                frozen || avatar != animator.avatar || controller != player.Controller)
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
                if (channels[i].Tuning != null && channels[i].Tuning.Limb == limb) return i;
            return -1;
        }

        public void Prepare(in CharacterAnimationComposition selection, CharacterAnimationMotion motion,
            in CharacterAnimationPlaybackPlan plan, float deltaTime)
        {
            if (sources.Length == 0 && poseInertia == null && !sampleFeet) return;
            if (!triedBinding || player.Controller != controller || animator.avatar != avatar)
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
            float renderDelta = animator.updateMode == AnimatorUpdateMode.UnscaledTime ? Time.unscaledDeltaTime : deltaTime;
            float animationDelta = clipGraph?.LayerBlendDelta(deltaTime) ?? Mathf.Max(0f, animator.speed) * renderDelta;
            modelHeight?.Advance(states, motion.IsValid && (motion.CollisionFlags & CollisionFlags.Below) != 0, animationDelta);
            bool directFeet = clipGraph != null && feet.Length > 0;
            Vector2 contacts = default;
            bool prepareJob = poseBindings.Length > 0 || hasFootChannels || directFeet;
#if UNITY_EDITOR
            prepareJob |= Evaluations != null;
#endif
            if (prepareJob)
            {
                var job = playable.GetJobData<CharacterIkJob>();
                job.AppliedModelDrop = modelHeight?.Drop ?? 0f;
                if (hasFootChannels) job.FootPlant = CharacterFootPlantFrame.Read(player, plan);
                if (poseBindings.Length > 0)
                {
                    job.Inertia = inertiaObservation.Prepare(selection, motion, plan, owner, poseInertia,
                        clipGraph?.InertiaAllowance ?? selection.InertiaAllowance, resumed);
                }
                if (directFeet)
                {
                    var left = clipGraph.Permission(CharacterIkLimb.LeftFoot, CharacterFootSampler.LeftCurve);
                    var right = clipGraph.Permission(CharacterIkLimb.RightFoot, CharacterFootSampler.RightCurve);
                    contacts = new Vector2(left.Value, right.Value);
                    var leftContact = clipGraph.Permission(CharacterIkLimb.LeftFoot, CharacterFootSampler.LeftContactCurve);
                    var rightContact = clipGraph.Permission(CharacterIkLimb.RightFoot, CharacterFootSampler.RightContactCurve);
                    job.Feet.ClipContacts = new Vector2(leftContact.CoveredValue, rightContact.CoveredValue);
                    job.Feet.ClipConfidence = new Vector2(leftContact.Coverage, rightContact.Coverage);
                }
#if UNITY_EDITOR
                job.DebugCapture.Prepared = 1;
                job.DebugCapture.PreparedRenderFrame = Time.frameCount;
                job.DebugCapture.FixedStep = motion.IsValid ? motion.FrameNumber : 0;
#endif
                playable.SetJobData(job);
            }
            EnsureCandidateCapacity();
            float probeDelta = clipGraph == null && animator.updateMode == AnimatorUpdateMode.AnimatePhysics
                ? Mathf.Max(0f, animator.speed) * Time.fixedDeltaTime : animationDelta;
            int cursor = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                var binding = bindings[i];
                if (!binding.Valid) continue;
                if (clipGraph != null)
                {
                    var tuning = channels[i].Tuning;
                    if (directFeet && tuning.Limb == CharacterIkLimb.LeftFoot && tuning.WeightCurve == CharacterFootSampler.LeftCurve)
                        binding.ClipPermission = contacts.x;
                    else if (directFeet && tuning.Limb == CharacterIkLimb.RightFoot && tuning.WeightCurve == CharacterFootSampler.RightCurve)
                        binding.ClipPermission = contacts.y;
                    else binding.ClipPermission = clipGraph.Permission(tuning.Limb, tuning.WeightCurve).Value;
                    bindings[i] = binding;
                }
                var state = states[i];
                inputs[i] = sources[i].Sample(selection, sceneCandidates, ref cursor,
                    state.ProbeMotion, probeDelta, clipGraph);
            }
        }

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

        private void Bind(bool keepTargets = false)
        {
            Invalidate(keepTargets);
            triedBinding = true;
            controller = player.Controller;
            avatar = animator.avatar;
            try
            {
                bindings = new NativeArray<CharacterIkBinding>(channels.Length, Allocator.Persistent);
                clipGraph = player.Direct;
                var parameters = clipGraph != null ? null : animator.parameters;
                bool any = false;
                for (int i = 0; i < channels.Length; i++)
                {
                    var channel = channels[i];
                    issues[i] = BindingIssue(i, parameters);
                    if (issues[i] != null) continue;
                    bool hasGoal = channel.Bones.AnimationTarget != null && channel.Bones.AnimationTarget.IsChildOf(animator.transform)
                        && !IsDriven(channel.Bones.AnimationTarget);
                    if (channel.Bones.AnimationTarget != null && !hasGoal)
                        issues[i] = "动画目标骨不独立或不属于此骨架，已跳过该来源";
                    bindings[i] = new CharacterIkBinding { Valid = true, HasAnimationTarget = hasGoal,
                        Limb = channel.Tuning.Limb,
                        Root = animator.BindStreamTransform(channel.Bones.Root), Mid = animator.BindStreamTransform(channel.Bones.Mid),
                        Tip = animator.BindStreamTransform(channel.Bones.Tip),
                        AnimationTarget = hasGoal ? animator.BindStreamTransform(channel.Bones.AnimationTarget) : default,
                        ClipDriven = (byte)(clipGraph != null ? 1 : 0),
                        Curve = clipGraph != null ? default : animator.BindStreamProperty(animator.transform, typeof(Animator), channel.Tuning.WeightCurve),
                        PositionWeight = Mathf.Clamp01(channel.Tuning.PositionWeight), RotationWeight = Mathf.Clamp01(channel.Tuning.RotationWeight),
                        BlendSeconds = InputMath.NonNegative(channel.Tuning.BlendSeconds, 0f) };
                    any = true;
                }
                var bones = poseInertia != null ? inertiaBones.Bind(animator) : Array.Empty<CharacterPoseBoneBinding>();
                CharacterFootSampler sampler = default;
                bool validFeet = sampleFeet && CharacterFootSampler.TryBind(animator, clipGraph != null, parameters, out sampler);
                if (!any && bones.Length == 0 && !validFeet) { DisposeBuffers(); return; }
                poseBindings = new NativeArray<CharacterPoseBoneBinding>(bones, Allocator.Persistent);
                poseHistory = new NativeArray<CharacterPoseBoneHistory>(bones.Length, Allocator.Persistent);
                inputs = new NativeArray<CharacterIkInput>(channels.Length, Allocator.Persistent);
                states = new NativeArray<CharacterIkState>(channels.Length, Allocator.Persistent);
                sceneCandidates = new NativeArray<CharacterIkCandidate>(1, Allocator.Persistent);
                feet = new NativeArray<CharacterFootObservation>(validFeet ? 1 : 0, Allocator.Persistent);
                sampler.History = feet;
#if UNITY_EDITOR
                Evaluations = new CharacterAnimationEvaluationHistory(animator, channels);
#endif
                graph = clipGraph != null ? clipGraph.Graph : PlayableGraph.Create("角色动画 IK");
                if (clipGraph == null) graph.SetTimeUpdateMode(animator.updateMode == AnimatorUpdateMode.UnscaledTime
                    ? DirectorUpdateMode.UnscaledGameTime : DirectorUpdateMode.GameTime);
                playable = AnimationScriptPlayable.Create(graph, new CharacterIkJob
                    { Bindings = bindings, Inputs = inputs, States = states, Scenes = sceneCandidates, Feet = sampler,
#if UNITY_EDITOR
                        DebugCapture = Evaluations.Capture,
#endif
                        ProbeBasis = animator.BindSceneTransform(owner), AdjustModelHeight = (byte)(modelHeight != null ? 1 : 0),
                        PoseBindings = poseBindings, PoseHistory = poseHistory,
                        LeftHand = IndexOf(CharacterIkLimb.LeftHand), RightHand = IndexOf(CharacterIkLimb.RightHand) });
                if (clipGraph != null)
                {
                    clipGraph.ConnectIk(playable);
                    return;
                }
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
                controller = player.Controller;
                avatar = animator.avatar;
                string reason = $"IK 绑定失败：{error.Message}";
                for (int i = 0; i < issues.Length; i++) issues[i] = reason;
                Debug.LogWarning(reason, animator);
            }
        }

        private string BindingIssue(int i, AnimatorControllerParameter[] parameters)
        {
            var channel = channels[i];
            var bones = channel.Bones;
            if (sources[i] == null) return "配置为空或部位重复";
            if (!bones.Root || !bones.Mid || !bones.Tip || bones.Root == animator.transform || bones.Root == bones.Mid || bones.Mid == bones.Tip ||
                !bones.Root.IsChildOf(animator.transform) || !bones.Mid.IsChildOf(bones.Root) || !bones.Tip.IsChildOf(bones.Mid))
                return "缺少骨骼或骨链层级不匹配";
            if (clipGraph != null) return string.IsNullOrWhiteSpace(channel.Tuning.WeightCurve) ? "未配置素材许可曲线" : null;
            if (!string.IsNullOrWhiteSpace(channel.Tuning.WeightCurve))
                foreach (var p in parameters)
                    if (p.name == channel.Tuning.WeightCurve && p.type == AnimatorControllerParameterType.Float && p.defaultFloat == 0f)
                        return null;
            return "许可曲线缺少同名且默认值为零的浮点参数";
        }

        private bool IsDriven(Transform goal)
        {
            foreach (var channel in channels)
                if (channel.Bones.Root && goal.IsChildOf(channel.Bones.Root)) return true;
            return false;
        }

        public void SetFrozen(bool value)
        {
            if (!graph.IsValid()) return;
            if (controller != player.Controller || avatar != animator.avatar)
            {
                Invalidate();
                return;
            }
            if (frozen == value) return;
            if (feet.IsCreated && feet.Length > 0) feet[0] = default;
            var job = playable.GetJobData<CharacterIkJob>();
            if (value)
            {
                hasMotionVersion = false;
#if UNITY_EDITOR
                Evaluations?.Clear();
                job.DebugCapture.Prepared = 0;
#endif
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
            frozen = job.Frozen = value;
            playable.SetJobData(job);
        }

        public void ClearPose()
        {
            modelHeight?.SetDrop(0f);
            foreach (var source in sources) source?.ForgetGrip();
#if UNITY_EDITOR
            Evaluations?.Clear();
#endif
            hasMotionVersion = false;
            if (feet.IsCreated && feet.Length > 0) feet[0] = default;
            inertiaObservation.ClearSelection();
            if (poseHistory.IsCreated)
                for (int i = 0; i < poseHistory.Length; i++) poseHistory[i] = default;
            if (graph.IsValid())
            {
                var job = playable.GetJobData<CharacterIkJob>();
                job.Inertia = default;
                job.AppliedModelDrop = 0f;
#if UNITY_EDITOR
                job.DebugCapture.Prepared = 0;
#endif
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

        public void Invalidate(bool keepTargets = false)
        {
            modelHeight?.SetDrop(0f);
            if (!keepTargets) foreach (var source in sources) source?.ForgetGrip();
            if (clipGraph != null) clipGraph.DisconnectIk(playable);
            else if (graph.IsValid()) graph.Destroy();
            clipGraph = null;
            graph = default;
            playable = default;
            DisposeBuffers();
            Array.Clear(issues, 0, issues.Length);
            triedBinding = hasMotionVersion = frozen = false;
            inertiaObservation = default;
            controller = null;
            avatar = null;
        }

        private void DisposeBuffers()
        {
#if UNITY_EDITOR
            Evaluations?.Dispose();
            Evaluations = null;
#endif
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
        public CharacterAnimationEvaluationHistory ReadEvaluations() =>
            frozen || !animator || avatar != animator.avatar || controller != player.Controller ? null : Evaluations;

        public string DescribeBindings()
        {
            if (!triedBinding) return "等待有效动画资源";
            if (channels.Length == 0) return "未配置 IK 通道";
            System.Text.StringBuilder text = null;
            for (int i = 0; i < channels.Length; i++)
            {
                if (issues[i] == null) continue;
                if (text == null) text = new System.Text.StringBuilder();
                else text.AppendLine();
                string limb = channels[i].Tuning?.Limb switch { CharacterIkLimb.LeftHand => "左手", CharacterIkLimb.RightHand => "右手",
                    CharacterIkLimb.LeftFoot => "左脚", CharacterIkLimb.RightFoot => "右脚", _ => "未知部位" };
                text.Append(limb).Append(" · ").Append(issues[i]);
            }
            return text?.ToString();
        }
#endif
    }
}
