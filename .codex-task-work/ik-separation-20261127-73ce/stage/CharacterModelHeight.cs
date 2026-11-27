using Unity.Collections;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>用上一帧承重腿的可达范围，在本次动画求值之前仅平移模型子节点。</summary>
    internal sealed class CharacterModelHeight
    {
        private const float Speed = 0.4f; // 世界米/秒，下沉与回零共用。
        private readonly Transform visual;
        private readonly float limit;
        private Vector3 restLocalPosition, lastWritten;
        public float Drop { get; private set; } // 相对模型原点的向下距离（世界米）。

        internal float Remaining => Mathf.Max(0f, limit - Drop);

        private CharacterModelHeight(Transform visual, float limit)
        {
            this.visual = visual;
            this.limit = limit;
        }

        public static CharacterModelHeight Bind(Animator animator, Transform owner, float limit)
        {
            limit = InputMath.NonNegative(limit, 0f);
            if (limit == 0f || !animator || animator.applyRootMotion || animator.transform == owner ||
                !animator.transform.IsChildOf(owner)) return null;
            return new CharacterModelHeight(animator.transform, limit);
        }

        // 根据上次求值发布的各腿需求推进本段模型高度。
        public void Advance(NativeArray<CharacterIkState> states, bool grounded, float deltaTime)
        {
            float target = 0f;
            if (grounded)
                for (int i = 0; i < states.Length; i++)
                    target = Mathf.Max(target, states[i].Ground.RequiredModelDrop);
            SetDrop(Mathf.MoveTowards(Drop, Mathf.Min(target, limit), Speed * Mathf.Max(0f, deltaTime)));
        }

        public void SetDrop(float value)
        {
            float next = Mathf.Clamp(value, 0f, limit);
            if (!visual || !visual.parent || (Drop == 0f && next == 0f)) return;
            Vector3 current = visual.localPosition;
            if (Drop == 0f) restLocalPosition = current;
            else restLocalPosition += current - lastWritten; // 保留其他表现逻辑对原点的增量。
            lastWritten = restLocalPosition + visual.parent.InverseTransformVector(Vector3.down * next);
            if (!current.Equals(lastWritten)) visual.localPosition = lastWritten;
            Drop = next;
        }

        // 同一坡面条件同时约束锁脚可用的竖直余量与实际模型降高。
        internal static bool AllowsDrop(in CharacterGroundFootHit ground) => ground.Platform != 0 && ground.Normal.y < 0.99985f;

        // 从未降模的髋位置求承重脚所需竖直行程，交给下一帧的限高处理。
        public static float RequiredDrop(Vector3 hip, float length, CharacterIkLimb limb, in CharacterGroundIkState state,
            in CharacterIkPlan plan, in CharacterGroundFootHit ground, in CharacterFootObservation feet, float gate, float appliedDrop)
        {
            if (!state.Locked || !AllowsDrop(ground) || plan.PositionWeight <= 0f) return 0f;
            float support = feet.SupportWeight(limb, gate) * plan.PositionWeight;
            if (support <= 0f) return 0f;
            Vector3 gap = hip + Vector3.up * appliedDrop - plan.Target.position; // 还原未补偿髋高，避免反馈累加。
            float verticalReachSquared = length * length - gap.x * gap.x - gap.z * gap.z;
            if (verticalReachSquared <= 0f) return 0f; // 纯竖直补偿无法接住水平范围外的锁点。
            return support * Mathf.Max(0f, gap.y - Mathf.Sqrt(verticalReachSquared));
        }
    }
}
