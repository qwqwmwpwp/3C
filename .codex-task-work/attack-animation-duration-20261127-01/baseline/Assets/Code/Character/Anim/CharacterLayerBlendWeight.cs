using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>可见上身和全身接管层的覆盖权重。仅在有限时长的过渡中衔接速度。</summary>
    internal sealed class CharacterLayerBlendWeight
    {
        private float velocity;
        private float startWeight, startSpeed, carrySeconds, duration, elapsed;
        private float timedTarget, timedSeconds;
        private string actionKey;
        private long actionInstance;
        private float phase, entryPhase;
        private float? heldWeight;

        public void Reset()
        {
            velocity = 0f;
            timedSeconds = 0f;
            actionKey = null;
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
                actionKey = null;
            }
            return AdvanceTimed(dt, heldWeight.Value);
        }

        public float Advance(float current, in CharacterAnimationLayerPlan plan, float dt)
        {
            heldWeight = null;
            if (plan.HasState && plan.Demand.HasHandoff) return AdvanceAction(current, plan.Demand, dt);
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
            actionKey = null;
            return AdvanceTimed(dt, target);
        }

        private float AdvanceTimed(float dt, float target)
        {
            elapsed += dt;
            return CharacterBlendCurve.Sample(elapsed, duration, true, carrySeconds)
                .Apply(startWeight, startSpeed, target, out velocity);
        }

        // 从本层实际覆盖量按动作时钟推进权重，并记录下一帧承接的速度。
        private float AdvanceAction(float current, in CharacterAnimationDemand demand, float dt)
        {
            var timing = demand.Timing;
            var handoff = timing.Handoff;
            float sample = timing.SamplePhase.GetValueOrDefault();
            bool sameAction = actionKey == demand.Key && actionInstance == demand.InstanceId;
            float phaseRate = sameAction && dt > 0f ? Mathf.Max(0f, (sample - phase) / dt) : 1f / handoff.Duration;
            if (!sameAction)
            {
                actionKey = demand.Key;
                actionInstance = demand.InstanceId;
                // 起点在上一帧的播放边界；当前帧的行为相位可能已经走过一小段前摇。
                entryPhase = Mathf.Max(0f, sample - dt * phaseRate);
                bool inherit = !handoff.IsAttack && velocity != 0f && demand.MaximumWeight > 0f && !timing.StartAtZero &&
                    !timing.Resumed && sample < handoff.FullFrom;
                Begin(current, inherit ? (handoff.FullFrom - entryPhase) * handoff.Duration : 0f);
            }
            phase = sample;
            timedSeconds = 0f;
            if (duration > 0f && sample < handoff.FullFrom)
            {
                float seconds = Mathf.Max(0f, sample - entryPhase) * handoff.Duration;
                float weight = CharacterBlendCurve.Sample(seconds, duration, true, carrySeconds)
                    .Apply(startWeight, startSpeed, demand.MaximumWeight, out float weightSpeed);
                velocity = weightSpeed * phaseRate * handoff.Duration;
                return weight;
            }
            float coverage = handoff.WeightAt(sample, demand.EntryCoverage, out float phaseSlope);
            velocity = demand.MaximumWeight * phaseSlope * phaseRate;
            return demand.MaximumWeight * coverage;
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
