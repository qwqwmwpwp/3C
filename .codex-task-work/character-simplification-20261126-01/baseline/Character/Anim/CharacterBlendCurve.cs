using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在原过渡时限内衰减上一段权重速度，并以零速度抵达目标。</summary>
    internal readonly struct CharacterBlendCurve
    {
        private readonly float blend, blendSpeed, carry, carrySpeed;
        public bool Complete { get; }

        private CharacterBlendCurve(float blend, float blendSpeed, float carry, float carrySpeed, bool complete = false)
        {
            this.blend = blend;
            this.blendSpeed = blendSpeed;
            this.carry = carry;
            this.carrySpeed = carrySpeed;
            Complete = complete;
        }

        public static float SmoothSlope(float progress) => 6f * progress * (1f - progress);

        public static float LimitCarry(float seconds, float weight, float velocity)
        {
            if (velocity == 0f) return seconds;
            float available = Mathf.Max(0f, velocity < 0f ? weight : 1f - weight);
            // 切线承接只用一半余量，避免在时限前触及 0 或 1。
            return Mathf.Min(seconds, available / Mathf.Abs(velocity));
        }

        public static CharacterBlendCurve Sample(float elapsed, float duration, bool smooth, float carrySeconds)
        {
            float progress = duration > 0f ? Mathf.Clamp01(elapsed / duration) : 1f;
            if (progress >= 1f) return new CharacterBlendCurve(1f, 0f, 0f, 0f, complete: true);
            if (!smooth) return new CharacterBlendCurve(progress, 1f / duration, 0f, 0f);

            float carriedTime = Mathf.Min(elapsed, carrySeconds);
            float carrySpeed = carrySeconds > 0f ? 1f - carriedTime / carrySeconds : 0f;
            float carry = carriedTime * (1f + carrySpeed) * 0.5f;
            return new CharacterBlendCurve(Mathf.SmoothStep(0f, 1f, progress), SmoothSlope(progress) / duration,
                carry, carrySpeed);
        }

        // 共享层本身也在从旧覆盖率 C 缓入。层内目标权重取 s / (C * (1-s) + s)，
        // 使最终可见的旧姿势只剩 C * (1-s)，已淡出的动作不会重新变重。
        internal CharacterBlendCurve WithinLayer(float entryCoverage)
        {
            if (Complete || entryCoverage <= 0f) return new CharacterBlendCurve(1f, 0f, 0f, 0f, complete: true);
            float coverage = Mathf.Lerp(entryCoverage, 1f, blend);
            float slope = (float)((double)blendSpeed * entryCoverage / ((double)coverage * coverage));
            return new CharacterBlendCurve(blend / coverage, slope, 0f, 0f);
        }

        public float Apply(float from, float previousSpeed, float to, out float speed)
        {
            if (Complete)
            {
                speed = 0f;
                return to;
            }
            float advanced = from + previousSpeed * carry;
            speed = (1f - blend) * previousSpeed * carrySpeed + (to - advanced) * blendSpeed;
            return Mathf.Lerp(advanced, to, blend);
        }
    }
}
