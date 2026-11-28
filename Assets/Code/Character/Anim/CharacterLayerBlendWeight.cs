using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>可见上身和全身接管层的覆盖权重。仅在有限时长的过渡中衔接速度。</summary>
    internal sealed class CharacterLayerBlendWeight
    {
        private float velocity;
        private float startWeight, startSpeed, carrySeconds, duration, elapsed;
        private float timedTarget, timedSeconds;
        private float? heldWeight;

        public void Reset()
        {
            velocity = 0f;
            timedSeconds = 0f;
            heldWeight = null;
        }

        public float Hold(float current, float dt, float secondsUntilCovered)
        {
            if (!heldWeight.HasValue)
            {
                heldWeight = current;
                float remaining = timedSeconds > 0f ? Mathf.Max(0f, duration - elapsed) : 0f;
                Begin(current, velocity != 0f ? Mathf.Min(remaining, secondsUntilCovered) : 0f);
                timedSeconds = 0f;
            }
            return AdvanceTimed(dt, heldWeight.Value);
        }

        public float Advance(float current, in CharacterAnimationLayerPlan plan, float dt)
        {
            heldWeight = null;
            if (plan.HasState && plan.Demand.HasHandoff) return AdvanceAction(plan.Demand);
            float target = plan.HasState ? plan.Weight : 0f;
            float seconds = plan.LayerBlendSeconds;
            if (seconds <= 0f)
            {
                Reset();
                return target;
            }

            if (timedTarget != target || timedSeconds != seconds)
            {
                timedTarget = target;
                timedSeconds = seconds;
                Begin(current, Mathf.Abs(target - current) * seconds);
            }
            return AdvanceTimed(dt, target);
        }

        private float AdvanceTimed(float dt, float target)
        {
            elapsed += dt;
            return CharacterBlendCurve.Sample(elapsed, duration, true, carrySeconds)
                .Apply(startWeight, startSpeed, target, out velocity);
        }

        // 从本层实际覆盖量按动作时钟推进权重，并记录下一帧承接的速度。
        private float AdvanceAction(in CharacterAnimationDemand demand)
        {
            duration = timedSeconds = 0f;
            float weight = demand.Timing.Sample.Weight(demand.EntryCoverage, out float slope);
            velocity = slope * demand.MaximumWeight;
            return weight * demand.MaximumWeight;
        }

        private void Begin(float weight, float seconds)
        {
            startWeight = weight;
            startSpeed = velocity;
            elapsed = 0f;
            duration = seconds;
            carrySeconds = CharacterBlendCurve.LimitCarry(seconds / 3f, weight, velocity);
        }
    }
}
