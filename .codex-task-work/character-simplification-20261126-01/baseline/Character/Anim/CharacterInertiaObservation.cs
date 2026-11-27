using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>保留上一帧身体归属和收步时钟，生成这次姿态惯性的输入。</summary>
    internal struct CharacterInertiaObservation
    {
        private CharacterAnimationDemand previousFull, previousUpper, previousOverlay;
        private int previousFullState, previousUpperState, poseRevision;
        private bool hasSelection;
        private long skipAccelerationFrame, discardedStopInstance;

        internal void ClearSelection() => hasSelection = false;

        internal CharacterPoseInertiaFrame Prepare(in CharacterAnimationComposition selection,
            CharacterAnimationMotion motion, in CharacterAnimationPlaybackPlan plan, Transform owner,
            CharacterInertiaTuning poseInertia, Vector3 allowance, bool resumed)
        {
            if (!hasSelection || resumed) skipAccelerationFrame = motion.FrameNumber;
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
            if (hasSelection && (fullChanged || upperChanged || Different(selection.Overlay, previousOverlay))) poseRevision++;
            hasSelection = true;
            previousFullState = full;
            previousUpperState = upper;
            previousFull = selection.FullBody;
            previousUpper = selection.UpperBody;
            previousOverlay = selection.Overlay;

            var frame = new CharacterPoseInertiaFrame
            {
                Revision = poseRevision, Allowance = allowance,
                Strength = Mathf.Clamp01(InputMath.NonNegative(poseInertia.Strength, 0f)),
                ChestSeconds = InputMath.NonNegative(poseInertia.ChestResponseSeconds, 0.18f),
                ArmSeconds = InputMath.NonNegative(poseInertia.ArmResponseSeconds, 0.24f),
                ChestLimit = Mathf.Min(60f, InputMath.NonNegative(poseInertia.ChestMaxDegrees, 0f)),
                ArmLimit = Mathf.Min(90f, InputMath.NonNegative(poseInertia.ArmMaxDegrees, 0f))
            };
            if (plan.StopPose.IsValid)
            {
                if (plan.StopPose.InstanceId != discardedStopInstance) // 瞬移或重绑后舍弃旧收步的入场动量。
                    PrepareStop(ref frame, plan.StopPose, owner, poseInertia);
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

        private static void PrepareStop(ref CharacterPoseInertiaFrame frame, in CharacterStopPoseSample stop,
            Transform owner, CharacterInertiaTuning poseInertia)
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
    }
}
