#if UNITY_EDITOR
using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    internal struct CharacterEvaluatedLimbPose
    {
        public Pose Tip;
        public Quaternion RootLocal, TipLocal;
        public Vector2 AngularSpeed; // 骨链根与末端相对各自父骨骼的角速度大小，单位度/秒。

        public static CharacterEvaluatedLimbPose Read(AnimationStream stream, TransformStreamHandle root, TransformStreamHandle tip) =>
            new CharacterEvaluatedLimbPose
            {
                Tip = new Pose(tip.GetPosition(stream), tip.GetRotation(stream)),
                RootLocal = root.GetLocalRotation(stream), TipLocal = tip.GetLocalRotation(stream)
            };

        public void MeasureSpeed(in CharacterEvaluatedLimbPose previous, float dt) => AngularSpeed = new Vector2(
            Quaternion.Angle(previous.RootLocal, RootLocal), Quaternion.Angle(previous.TipLocal, TipLocal)) / dt;
    }

    internal struct CharacterEvaluatedProbeRay
    {
        public Vector3 Origin, Point, Normal;
        public int Platform;

        public CharacterEvaluatedProbeRay(in CharacterGroundFootHit hit)
        {
            Origin = hit.Origin;
            Point = hit.Point;
            Normal = hit.Normal;
            Platform = hit.Platform;
        }
    }

    internal struct CharacterEvaluatedFootProbe
    {
        public CharacterEvaluatedProbeRay Center, Behind, Ahead;
        public Vector3 Up;
        public float Length;
        public int Count, AcceptedRay;

        public CharacterEvaluatedFootProbe(in CharacterGroundFootProbes probes, int acceptedRay)
        {
            Up = probes.Up;
            Length = probes.Length;
            Count = probes.Count;
            AcceptedRay = acceptedRay;
            Center = new CharacterEvaluatedProbeRay(probes.Ray(0));
            Behind = new CharacterEvaluatedProbeRay(probes.Ray(1));
            Ahead = new CharacterEvaluatedProbeRay(probes.Ray(2));
        }

        public CharacterEvaluatedProbeRay Ray(int index) => index == 0 ? Center : index == 1 ? Behind : Ahead;
    }

    internal struct CharacterEvaluatedLimb
    {
        public byte Valid, HasIk, HasSpeed, HasTarget, HasGripPoint, GripCoverageKnown, Locked, HasSlip;
        public CharacterEvaluatedLimbPose Fk, Inertial, Final;
        public CharacterEvaluatedFootProbe Probe;
        public CharacterIkSource Source;
        public int CandidateMask, SceneCount, ObjectId;
        public long Instance;
        public float Gate, PositionWeight, RotationWeight, GripCoverage;
        public Pose Target, GripPoint;
        public Vector3 AnchorWorld, AnchorLocal, FootLocal;
        public float LockOffset, Slip;

        public void RecordPlan(in CharacterIkState state, in CharacterIkCandidate chosen)
        {
            Source = state.Source;
            CandidateMask = state.CandidateMask;
            SceneCount = state.SceneCount;
            ObjectId = state.ObjectId;
            Instance = state.Instance;
            Gate = state.Gate;
            PositionWeight = state.Plan.PositionWeight;
            RotationWeight = state.Plan.RotationWeight;
            HasTarget = (byte)(chosen.Source != CharacterIkSource.None ? 1 : 0);
            Target = HasTarget != 0 ? chosen.Target : default;
        }

        public void MeasureFrame(in CharacterEvaluatedLimb previous, in CharacterGroundFootHit terrain, float dt)
        {
            if (Locked != 0)
            {
                FootLocal = terrain.ToLocal.MultiplyPoint3x4(Final.Tip.position);
                Vector3 offset = Final.Tip.position - AnchorWorld;
                LockOffset = Vector3.ProjectOnPlane(offset, terrain.Normal).magnitude;
                if (previous.Locked != 0 && previous.ObjectId == ObjectId && previous.AnchorLocal.Equals(AnchorLocal))
                {
                    HasSlip = 1;
                    Vector3 movement = terrain.ToWorld.MultiplyVector(FootLocal - previous.FootLocal);
                    Slip = Vector3.ProjectOnPlane(movement, terrain.Normal).magnitude;
                }
            }
            if (previous.Valid == 0 || dt <= 0.00001f) return;
            HasSpeed = 1;
            Fk.MeasureSpeed(previous.Fk, dt);
            Inertial.MeasureSpeed(previous.Inertial, dt);
            Final.MeasureSpeed(previous.Final, dt);
        }
    }

    internal struct CharacterEvaluatedAnimationFrame
    {
        public long Sequence, FixedStep;
        public int PreparedRenderFrame;
        public float DeltaTime, InertiaStrength;
        public byte InertiaBound;
        public Vector3 InertiaSuppression, InertiaAppliedDegrees;
        public Vector2 GripInertiaSuppression; // 单独显示握持额外提供的左右手惯性抑制。
        private CharacterEvaluatedLimb leftHand, rightHand, leftFoot, rightFoot;

        public CharacterEvaluatedLimb Read(CharacterIkLimb limb) => limb switch
        {
            CharacterIkLimb.LeftHand => leftHand,
            CharacterIkLimb.RightHand => rightHand,
            CharacterIkLimb.LeftFoot => leftFoot,
            _ => rightFoot
        };

        public static ref CharacterEvaluatedLimb For(ref CharacterEvaluatedAnimationFrame frame, CharacterIkLimb limb)
        {
            switch (limb)
            {
                case CharacterIkLimb.LeftHand: return ref frame.leftHand;
                case CharacterIkLimb.RightHand: return ref frame.rightHand;
                case CharacterIkLimb.LeftFoot: return ref frame.leftFoot;
                default: return ref frame.rightFoot;
            }
        }
    }

    internal struct CharacterAnimationEvaluationCursor
    {
        public long Latest;
        public int Count;
    }

    internal struct CharacterEvaluationLimbBinding
    {
        private byte bound;
        public TransformStreamHandle Root, Tip;

        public bool Ready(AnimationStream stream) => bound != 0 && Root.IsValid(stream) && Tip.IsValid(stream);

        public static CharacterEvaluationLimbBinding Bind(Animator animator, CharacterBoundIkChannel[] channels,
            CharacterIkLimb limb, HumanBodyBones humanRoot, HumanBodyBones humanTip)
        {
            foreach (var channel in channels)
                if (channel.Tuning?.Limb == limb && CanBind(animator, channel.Bones.Root, channel.Bones.Tip))
                    return Create(animator, channel.Bones.Root, channel.Bones.Tip);
            if (!animator.isHuman) return default;
            Transform root = animator.GetBoneTransform(humanRoot);
            Transform tip = animator.GetBoneTransform(humanTip);
            return CanBind(animator, root, tip) ? Create(animator, root, tip) : default;
        }

        private static bool CanBind(Animator animator, Transform root, Transform tip) =>
            root && tip && root != animator.transform && root != tip && root.IsChildOf(animator.transform) && tip.IsChildOf(root);

        private static CharacterEvaluationLimbBinding Create(Animator animator, Transform root, Transform tip) =>
            new CharacterEvaluationLimbBinding { bound = 1, Root = animator.BindStreamTransform(root), Tip = animator.BindStreamTransform(tip) };
    }

    internal struct CharacterAnimationEvaluationCapture
    {
        public NativeArray<CharacterEvaluatedAnimationFrame> Frames;
        public NativeArray<CharacterAnimationEvaluationCursor> Cursor;
        public int PreparedRenderFrame;
        public long FixedStep;
        public byte Prepared;
        public CharacterEvaluationLimbBinding LeftHand, RightHand, LeftFoot, RightFoot;

        public bool Active => Prepared != 0 && Frames.IsCreated;
        public static int Slot(long sequence) => (int)((sequence - 1) % CharacterAnimationEvaluationHistory.Capacity);

        public CharacterEvaluatedAnimationFrame Start(AnimationStream stream, in CharacterPoseInertiaFrame inertia,
            bool inertiaBound, NativeArray<CharacterIkBinding> bindings)
        {
            var frame = new CharacterEvaluatedAnimationFrame { PreparedRenderFrame = PreparedRenderFrame, FixedStep = FixedStep,
                DeltaTime = stream.deltaTime, InertiaBound = (byte)(inertiaBound ? 1 : 0), InertiaStrength = inertia.Strength,
                InertiaSuppression = inertiaBound ? Vector3.one - inertia.Allowance : Vector3.zero };
            for (int i = 0; i < bindings.Length; i++)
            {
                var binding = bindings[i];
                if (!binding.IsReady(stream)) continue;
                ref var limb = ref CharacterEvaluatedAnimationFrame.For(ref frame, binding.Limb);
                limb.Valid = limb.HasIk = 1;
                limb.Fk = CharacterEvaluatedLimbPose.Read(stream, binding.Root, binding.Tip);
            }
            ObserveAdditionalPoses(stream, ref frame, PoseStage.Fk);
            return frame;
        }

        public enum PoseStage { Fk, Inertial, Final }

        public void ObserveAdditionalPoses(AnimationStream stream, ref CharacterEvaluatedAnimationFrame frame, PoseStage stage,
            in CharacterEvaluatedAnimationFrame previous = default)
        {
            for (int i = 0; i < 4; i++)
            {
                var limbId = (CharacterIkLimb)i;
                ref var limb = ref CharacterEvaluatedAnimationFrame.For(ref frame, limbId);
                if (limb.HasIk != 0) continue;
                var binding = limbId switch { CharacterIkLimb.LeftHand => LeftHand, CharacterIkLimb.RightHand => RightHand,
                    CharacterIkLimb.LeftFoot => LeftFoot, _ => RightFoot };
                if (!binding.Ready(stream)) continue;
                var pose = CharacterEvaluatedLimbPose.Read(stream, binding.Root, binding.Tip);
                switch (stage)
                {
                    case PoseStage.Fk: limb.Valid = 1; limb.Fk = pose; break;
                    case PoseStage.Inertial: limb.Inertial = pose; break;
                    case PoseStage.Final:
                        limb.Final = pose;
                        limb.MeasureFrame(previous.Read(limbId), default, frame.DeltaTime);
                        break;
                }
            }
        }

        public static void ObservePlan(AnimationStream stream, ref CharacterEvaluatedAnimationFrame frame,
            in CharacterIkBinding binding, in CharacterIkInput input, in CharacterIkState state,
            in CharacterIkCandidate chosen, in CharacterGroundFootHit ground, int acceptedRay)
        {
            ref var limb = ref CharacterEvaluatedAnimationFrame.For(ref frame, binding.Limb);
            limb.Inertial = CharacterEvaluatedLimbPose.Read(stream, binding.Root, binding.Tip);
            limb.RecordPlan(state, chosen);
            if (binding.IsFoot)
            {
                limb.Probe = new CharacterEvaluatedFootProbe(input.Probes, acceptedRay);
                if (!state.Locked || state.Source != CharacterIkSource.Ground) return;
                limb.Locked = 1;
                limb.AnchorLocal = state.LocalAnchor.position;
                limb.AnchorWorld = ground.ToWorld.MultiplyPoint3x4(limb.AnchorLocal);
                return;
            }
            limb.GripCoverage = input.DebugGripCoverage;
            limb.GripCoverageKnown = input.DebugGripCoverageKnown;
            if ((input.DebugGripCoverage <= 0f && input.DebugGripInstance == 0) || !binding.CanReadTarget(stream)) return;
            limb.HasGripPoint = 1;
            limb.GripPoint = new Pose(binding.AnimationTarget.GetPosition(stream), binding.AnimationTarget.GetRotation(stream));
        }

        public void Complete(AnimationStream stream, ref CharacterEvaluatedAnimationFrame frame,
            NativeArray<CharacterIkBinding> bindings, NativeArray<CharacterIkInput> inputs,
            NativeArray<CharacterPoseBoneBinding> poseBindings, NativeArray<CharacterPoseBoneHistory> poseHistory,
            float leftGrip, float rightGrip)
        {
            frame.GripInertiaSuppression = new Vector2(leftGrip, rightGrip);
            for (int i = 0; i < poseHistory.Length; i++)
                frame.InertiaAppliedDegrees[(int)poseBindings[i].Region] += poseHistory[i].AppliedDegrees;
            var cursor = Cursor[0];
            var previous = cursor.Count > 0 ? Frames[Slot(cursor.Latest)] : default;
            for (int i = 0; i < bindings.Length; i++)
            {
                var binding = bindings[i];
                if (!binding.Valid) continue;
                ref var limb = ref CharacterEvaluatedAnimationFrame.For(ref frame, binding.Limb);
                if (limb.HasIk == 0) continue;
                limb.Final = CharacterEvaluatedLimbPose.Read(stream, binding.Root, binding.Tip);
                var terrain = limb.Locked != 0 ? inputs[i].Probes.Ray(limb.Probe.AcceptedRay) : default;
                limb.MeasureFrame(previous.Read(binding.Limb), terrain, frame.DeltaTime);
            }
            ObserveAdditionalPoses(stream, ref frame, PoseStage.Final, previous);
            frame.Sequence = ++cursor.Latest;
            Frames[Slot(cursor.Latest)] = frame;
            if (cursor.Count < CharacterAnimationEvaluationHistory.Capacity) cursor.Count++;
            Cursor[0] = cursor;
        }
    }

    /// <summary>以实际动画求值顺序保留最近帧。释放或断开连续性后，不再提供旧帧。</summary>
    internal sealed class CharacterAnimationEvaluationHistory : IDisposable
    {
        public const int Capacity = 120;
        private CharacterAnimationEvaluationCapture capture;
        public CharacterAnimationEvaluationCapture Capture => capture;
        public int Revision { get; private set; }

        public CharacterAnimationEvaluationHistory(Animator animator, CharacterBoundIkChannel[] channels)
        {
            capture.LeftHand = CharacterEvaluationLimbBinding.Bind(animator, channels, CharacterIkLimb.LeftHand,
                HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand);
            capture.RightHand = CharacterEvaluationLimbBinding.Bind(animator, channels, CharacterIkLimb.RightHand,
                HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand);
            capture.LeftFoot = CharacterEvaluationLimbBinding.Bind(animator, channels, CharacterIkLimb.LeftFoot,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftFoot);
            capture.RightFoot = CharacterEvaluationLimbBinding.Bind(animator, channels, CharacterIkLimb.RightFoot,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightFoot);
            capture.Frames = new NativeArray<CharacterEvaluatedAnimationFrame>(Capacity, Allocator.Persistent);
            try { capture.Cursor = new NativeArray<CharacterAnimationEvaluationCursor>(1, Allocator.Persistent); }
            catch { capture.Frames.Dispose(); throw; }
        }

        public void Clear()
        {
            var cursor = capture.Cursor[0];
            cursor.Count = 0;
            capture.Cursor[0] = cursor;
            Revision++;
        }

        public bool TryReadLatest(out CharacterEvaluatedAnimationFrame frame)
        {
            if (capture.Cursor.IsCreated && capture.Cursor[0].Count > 0)
            {
                frame = capture.Frames[CharacterAnimationEvaluationCapture.Slot(capture.Cursor[0].Latest)];
                return true;
            }
            frame = default;
            return false;
        }

        public int CopyTo(CharacterEvaluatedAnimationFrame[] buffer)
        {
            if (!capture.Cursor.IsCreated) return 0;
            var cursor = capture.Cursor[0];
            int count = Mathf.Min(buffer.Length, cursor.Count);
            for (int i = 0; i < count; i++)
                buffer[i] = capture.Frames[CharacterAnimationEvaluationCapture.Slot(cursor.Latest - count + 1 + i)];
            return count;
        }

        public void Dispose()
        {
            if (capture.Cursor.IsCreated) capture.Cursor.Dispose();
            if (capture.Frames.IsCreated) capture.Frames.Dispose();
        }
    }
}
#endif
