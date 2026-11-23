using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

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

        public void Advance(NativeArray<CharacterIkState> states, bool grounded, float deltaTime)
        {
            float target = 0f;
            if (grounded)
                for (int i = 0; i < states.Length; i++)
                    target = Mathf.Max(target, states[i].RequiredModelDrop);
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

        public static float RequiredDrop(AnimationStream stream, in CharacterIkBinding leg, in CharacterIkState state,
            in CharacterGroundFootHit ground, in CharacterFootObservation feet, float appliedDrop)
        {
            const float flatNormalY = 0.99985f; // 约 1° 以内按平地处理。
            if (!state.Locked || state.Source != CharacterIkSource.Ground ||
                ground.Platform == 0 || ground.Normal.y >= flatNormalY || state.Plan.PositionWeight <= 0f) return 0f;

            float support = feet.SupportWeight(leg.Limb, state.Gate) * state.Plan.PositionWeight;
            if (support <= 0f) return 0f;

            Vector3 hip = leg.Root.GetPosition(stream);
            Vector3 knee = leg.Mid.GetPosition(stream);
            float length = Vector3.Distance(hip, knee) + Vector3.Distance(knee, leg.Tip.GetPosition(stream));
            Vector3 gap = hip + Vector3.up * appliedDrop - state.Target.position; // 还原未补偿髋高，避免反馈累加。
            float verticalReachSquared = length * length - gap.x * gap.x - gap.z * gap.z;
            if (verticalReachSquared <= 0f) return 0f; // 纯竖直补偿无法接住水平范围外的锁点。
            return support * Mathf.Max(0f, gap.y - Mathf.Sqrt(verticalReachSquared));
        }
    }
}
