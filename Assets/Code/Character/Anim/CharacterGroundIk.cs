using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;

namespace ThreeC.CharacterBehavior
{
    // 保存单通道的地面锚点、踏步历史、探测速度与下次模型降高需求。
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
                // 非足部通道沿用完整的地面位置和旋转锁定。
                if (!state.Locked && gate >= LockGate)
                {
                    state.LocalAnchor = new Pose(ground.ToLocal.MultiplyPoint3x4(target.position),
                        Quaternion.Inverse(ground.Rotation) * target.rotation);
                    state.Locked = true;
                }
                if (gate <= ReleaseGate) state.Locked = false;
                if (state.Locked)
                {
                    float grip = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ReleaseGate, LockGate, gate));
                    target = new Pose(Vector3.Lerp(target.position,
                        ground.ToWorld.MultiplyPoint3x4(state.LocalAnchor.position), grip),
                        Quaternion.Slerp(target.rotation, ground.Rotation * state.LocalAnchor.rotation, grip));
                }
            }
            return target;
        }
    }
}
