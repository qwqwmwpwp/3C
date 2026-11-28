using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 只读接管双腿的目标片段。混合后的 IK 许可仍决定整条腿的地形修正权重。
    internal readonly struct CharacterFootPlantFrame
    {
        public readonly int State;
        public readonly long Instance;
        public readonly Vector2 Contact;
        public readonly byte Handoff;

        public CharacterFootPlantFrame(in CharacterAnimationLayerPlan layer, float phase, bool blending,
            CharacterMotionPlayable motion = null, double clipTime = 0d)
        {
            State = layer.StateHash;
            Instance = layer.Demand.InstanceId;
            Handoff = (byte)(blending || layer.Binding.IsStop || layer.Binding.Variant == CharacterLocomotionVariant.Start ? 1 : 0);
            Contact = new Vector2(Read(layer, true, phase, blending, motion, clipTime),
                Read(layer, false, phase, blending, motion, clipTime));
        }

        private static float Read(in CharacterAnimationLayerPlan layer, bool left, float phase, bool blending,
            CharacterMotionPlayable motion, double time)
        {
            string curve = left ? CharacterFootSampler.LeftContactCurve : CharacterFootSampler.RightContactCurve;
            if (motion != null && motion.TryRead(curve, time, out float value)) return value;
            if (layer.Binding.Gait.IsValid) return layer.Binding.Gait.IsPlanted(left, phase) ? 1f : 0f;
            if (layer.Binding.IsStop && phase < CharacterStopPoseSample.SupportTransferPhase)
                return left == (layer.Binding.Variant == CharacterLocomotionVariant.StopLeft) ? 1f : 0f;
            if (layer.Demand.Key == CharacterLocomotionKeys.IdleKey) return 1f;
            return blending ? 0f : -1f; // 老资源缺少接触数据时，过渡结束后再使用动画流的许可。
        }

        // 按双腿的实际归属读取直接混合器中本次求值使用的脚姿。
        public static CharacterFootPlantFrame Read(CharacterClipPlayback clips, in CharacterAnimationPlaybackPlan plan)
        {
            var layer = plan.LowerOwner.Weight > 0f ? plan.LowerOwner : plan.FullBody;
            if (!layer.HasState) return default;
            return clips.ReadFootPlant(layer);
        }
    }

    // 用真实骨链长度及允许的竖直降模判断世界目标是否可达。
    internal readonly struct CharacterFootReach
    {
        private readonly Vector3 hip;
        private readonly float length, remainingDrop;

        // 剩余降模以世界米计，平地和未启用降模时传零。
        internal CharacterFootReach(Vector3 hip, float length, float remainingDrop)
        {
            this.hip = hip;
            this.length = length;
            this.remainingDrop = remainingDrop;
        }

        // 在可用竖直行程中取距离最近的髋位置，水平伸展始终受实际腿长约束。
        internal bool Contains(Vector3 target)
        {
            float down = Mathf.Clamp(hip.y - target.y, 0f, remainingDrop);
            Vector3 gap = hip - Vector3.up * down - target;
            float radius = length + 0.0001f; // 仅吸收浮点舍入，单位米。
            return gap.sqrMagnitude <= radius * radius;
        }
    }

    // 每只脚独立保存同一平台上的水平修正；释放期间仅衰减已显示的偏移，不再追逐旧脚印。
    internal struct CharacterFootPlant
    {
        private const float PlantContact = 0.7f, UnloadedContact = 0.3f;
        private const float HandoffDistance = 0.06f, StanceDistance = 0.14f; // 地面切向距离，米。
        private const float SurfaceTolerance = 0.025f, LandingHeight = 0.06f; // 世界米。
        private const float SettleSpeed = 0.25f, SettleSeconds = 0.05f;
        private const float ReleaseSpeed = 2f; // 水平偏移每秒回收的米数。
        private Vector3 lastTargetLocal, offsetLocal;
        private float settled, lastPositionWeight;
        private int lastState;
        private long lastInstance;
        private byte sampled, waitForLanding;

        // 在平台局部坐标中建立足底锁点，跨台阶或水平锚点超出腿长时受控释放。
        public static void Place(ref CharacterGroundIkState state, in CharacterFootPlantFrame frame, CharacterIkLimb limb,
            in CharacterGroundFootHit ground, Vector3 ankle, float gate, float positionWeight, float dt, in CharacterFootReach reach, ref Pose target)
        {
            ref var history = ref state.FootPlant;
            Vector3 normal = ground.Normal;
            Vector3 point = target.position;
            Vector3 local = ground.ToLocal.MultiplyPoint3x4(point);
            float contact = limb == CharacterIkLimb.LeftFoot ? frame.Contact.x : frame.Contact.y;
            if (frame.State == 0 || contact < 0f) contact = gate;
            bool changed = frame.State != history.lastState || frame.Instance != history.lastInstance;
            bool nearGround = Vector3.Dot(ankle - point, normal) <= LandingHeight;
            Vector3 movement = ground.ToWorld.MultiplyVector(local - history.lastTargetLocal);
            bool crossedStep = history.sampled != 0 && Mathf.Abs(Vector3.Dot(movement, normal)) > SurfaceTolerance;
            Vector3 travel = Vector3.ProjectOnPlane(movement, normal);
            bool still = history.sampled != 0 && dt > 0f && travel.magnitude <= SettleSpeed * dt;
            bool settling = !changed && !crossedStep && still && nearGround && contact >= PlantContact;
            history.settled = settling ? Mathf.Min(SettleSeconds, history.settled + dt) : 0f;
            if (!state.Locked && (changed || history.sampled == 0 || crossedStep) && contact >= PlantContact)
                history.waitForLanding = 1; // 入场已经满权重时，先确认动画脚在新平台落稳。
            history.lastTargetLocal = local;
            history.lastState = frame.State;
            history.lastInstance = frame.Instance;
            history.sampled = 1;

            Vector3 offset = crossedStep ? Vector3.zero : Vector3.ProjectOnPlane(ground.ToWorld.MultiplyVector(history.offsetLocal), normal);
            if (state.Locked)
            {
                Vector3 toAnchor = ground.ToWorld.MultiplyPoint3x4(state.LocalAnchor.position) - point;
                Vector3 tangent = Vector3.ProjectOnPlane(toAnchor, normal);
                float limit = changed || frame.Handoff != 0 ? HandoffDistance : StanceDistance;
                bool otherSurface = Mathf.Abs(Vector3.Dot(toAnchor, normal)) > SurfaceTolerance;
                bool fits = reach.Contains(point + tangent) && !crossedStep && !otherSurface && tangent.sqrMagnitude <= limit * limit;
                if (contact < PlantContact || gate <= UnloadedContact || !nearGround || !fits)
                {
                    state.Locked = false;
                    history.waitForLanding = 1;
                    history.settled = 0f;
                    if (otherSurface) offset = Vector3.zero;
                }
                else offset = tangent;
            }
            if (!state.Locked)
            {
                if (contact <= UnloadedContact) history.waitForLanding = 0;
                offset = Vector3.MoveTowards(offset, Vector3.zero, ReleaseSpeed * Mathf.Max(0f, dt));
                if (positionWeight > history.lastPositionWeight)
                    offset *= history.lastPositionWeight / positionWeight; // 淡入许可上升也不能放大旧修正。
                bool landed = history.waitForLanding == 0 || history.settled >= SettleSeconds;
                if (nearGround && landed && contact >= PlantContact && gate >= PlantContact &&
                    offset.sqrMagnitude < 0.000001f && reach.Contains(point))
                {
                    state.LocalAnchor.position = local;
                    state.Locked = true;
                    history.waitForLanding = 0;
                    offset = Vector3.zero;
                }
            }
            history.offsetLocal = ground.ToLocal.MultiplyVector(offset);
            history.lastPositionWeight = positionWeight;
            target.position = point + offset;
        }
    }
}
