using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 在主线程计算当前行进方向相对混合素材参考路径的剩余偏角。
    internal sealed class CharacterOrientationWarp
    {
        private readonly OrientationWarpTuning tuning;
        private float smoothed;

        // 保存本角色的平滑时间与校正范围。
        internal CharacterOrientationWarp(OrientationWarpTuning tuning) => this.tuning = tuning;

        // 在同一世界水平面比较路径方向，按真实速度与未被动作覆盖的循环份额衰减。
        internal float Advance(CharacterClipPlayback clips, in CharacterAnimationMotion motion, Transform model, float dt, float visualYaw, float nearSeconds)
        {
            var path = clips.CycleDirection(nearSeconds, out float coverage);
            if (!motion.IsValid || motion.IsBlocked || (motion.CollisionFlags & CollisionFlags.Below) == 0 ||
                coverage <= 0f || path.sqrMagnitude < 0.000001f)
            {
                Reset();
                return 0f;
            }
            Vector3 forward = Vector3.ProjectOnPlane(model.forward, Vector3.up).normalized;
            Vector3 reference = Vector3.Cross(Vector3.up, forward) * path.x + forward * path.y;
            Vector3 travel = motion.ActualVelocity;
            var forecast = motion.Trajectory.Match;
            if (forecast.Valid)
                travel = Vector3.Lerp(travel, forecast.NearWorld / forecast.Windows.x, 0.5f);
            float angle = Vector3.SignedAngle(reference, travel, Vector3.up);
            float limit = Mathf.Clamp(tuning.MaxAngle, 0f, 45f);
            float target = Mathf.Sign(angle) * Mathf.Max(0f, Mathf.Abs(angle) - tuning.DeadZone);
            float response = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, tuning.ResponseSeconds));
            smoothed = Mathf.DeltaAngle(0f, Mathf.LerpAngle(smoothed, target, response));
            float speedWeight = Mathf.InverseLerp(tuning.MinimumSpeed,
                Mathf.Max(tuning.MinimumSpeed + 0.01f, tuning.FullSpeed), motion.ActualSpeed);
            return Mathf.Clamp(Mathf.DeltaAngle(visualYaw, smoothed), -limit, limit) * Mathf.Clamp01(coverage) * speedWeight;
        }

        // 丢弃暂停、瞬移或重绑前的方向响应。
        internal void Reset() => smoothed = 0f;
    }

    // 将校正一次施加到骨盆并在脊柱补偿，上腿自然继承骨盆变换。
    internal struct CharacterOrientationBones
    {
        private TransformStreamHandle hips, spine;
        private byte bound;
        private float compensation;
        internal float Angle;
        internal bool Bound => bound != 0;

        // 仅为具有有效人形骨盆和脊柱层级的模型绑定骨骼。
        internal static CharacterOrientationBones Bind(Animator animator, OrientationWarpTuning tuning)
        {
            if (tuning == null || !animator.isHuman) return default;
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            if (!hips || !spine || hips == animator.transform || !spine.IsChildOf(hips)) return default;
            return new CharacterOrientationBones { hips = animator.BindStreamTransform(hips), spine = animator.BindStreamTransform(spine),
                bound = 1, compensation = Mathf.Clamp01(tuning.SpineCompensation) };
        }

        // 返回实际写入的旋转及骨盆中心，供同一帧的踝部探测保持坐标一致。
        internal Quaternion Apply(AnimationStream stream, out Vector3 pivot)
        {
            pivot = default;
            if (!Bound || !hips.IsValid(stream) || !spine.IsValid(stream)) return Quaternion.identity;
            pivot = hips.GetPosition(stream);
            if (Angle == 0f) return Quaternion.identity;
            var yaw = Quaternion.AngleAxis(Angle, Vector3.up);
            var upper = spine.GetRotation(stream);
            hips.SetRotation(stream, yaw * hips.GetRotation(stream));
            spine.SetRotation(stream, Quaternion.AngleAxis(Angle * (1f - compensation), Vector3.up) * upper);
            return yaw;
        }
    }
}
