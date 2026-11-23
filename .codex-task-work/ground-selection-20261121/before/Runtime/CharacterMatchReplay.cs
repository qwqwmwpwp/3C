#if UNITY_EDITOR
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 只在 Inspector 主动触发。输入按固定步生成，两种选姿模式走完全相同的移动入口。
    internal sealed class CharacterMatchReplay
    {
        private struct Foot
        {
            public int Surface;
            public Vector3 Local;
        }

        private readonly Vector2 forward;
        private readonly bool reverse;
        private readonly double releaseTime;
        private readonly CharacterMatchMode mode;
        private Foot left, right;
        private double elapsed, reverseAt;
        private float brakeTravel, slip, latency;
        private int measurements, phase;
        private bool timedReverse, acceptedReverse, observedRestart, cancelled;
        public bool Started => elapsed > 0d;
        internal CharacterMatchMode Mode => mode;
        public bool Active { get; private set; } = true;

        public CharacterMatchReplay(Vector3 heading, bool reverse, CharacterMatchMode mode, float extraRunSeconds)
        {
            forward = new Vector2(heading.x, heading.z).normalized;
            this.reverse = reverse;
            releaseTime = 1.35 + extraRunSeconds;
            this.mode = mode;
        }

        public bool TryInput(float dt, out CharacterInputSnapshot input)
        {
            input = default;
            if (!Active) return false;
            double brakeEnd = releaseTime + (reverse ? 0.12 : 0.85); // 起跑前留出落稳时间，松键后在两段制动交接时反向。
            int next = elapsed < 0.45 ? 0 : elapsed < releaseTime ? 1 : elapsed < brakeEnd ? 2 :
                reverse && elapsed < brakeEnd + 0.70 ? 3 : 4;
            if (next == 3 && phase != 3) { reverseAt = Time.fixedTimeAsDouble; timedReverse = true; }
            phase = next;
            elapsed += dt;
            if (elapsed > brakeEnd + (reverse ? 1.35 : 0.35))
            {
                Active = false;
                return true;
            }
            Vector2 direction = phase == 1 ? forward : phase == 3 ? -forward : Vector2.zero;
            input = new CharacterInputSnapshot(direction, false);
            return true;
        }

        public void OnStep(in CharacterControllerFrameResult frame)
        {
            if (Active && timedReverse && Vector2.Dot(frame.Input.Direction, forward) < -0.9f)
                acceptedReverse = true;
            if (Active && phase == 2)
                brakeTravel += Vector3.ProjectOnPlane(frame.ActualDisplacement, Vector3.up).magnitude;
        }

        public void Observe(CharacterAnimationPipeline pipeline)
        {
            if (!Active || !Started) return;
            if (phase == 2 || phase == 3)
            {
                Measure(pipeline.Ik, CharacterIkLimb.LeftFoot, ref left);
                Measure(pipeline.Ik, CharacterIkLimb.RightFoot, ref right);
            }
            else left = right = default;
            var layer = pipeline.DisplayedBase;
            if (phase == 3 && acceptedReverse && !observedRestart && layer.Demand.IsLocomotion && !layer.Binding.IsStop &&
                layer.Command != CharacterAnimationPlaybackCommand.Keep)
            {
                latency = Mathf.Max(0f, (float)(Time.timeAsDouble - reverseAt));
                observedRestart = true;
            }
        }

        private void Measure(CharacterAnimationIk ik, CharacterIkLimb limb, ref Foot last)
        {
            if (ik.TryReadSupport(limb, out int surface, out Vector3 local, out var toWorld, out var normal))
            {
                if (last.Surface == surface)
                {
                    var delta = Vector3.ProjectOnPlane(toWorld.MultiplyVector(local - last.Local), normal);
                    slip += delta.magnitude;
                    measurements++;
                }
                last = new Foot { Surface = surface, Local = local };
            }
            else last = default;
        }

        public void Cancel()
        {
            if (Active) { Active = false; cancelled = true; }
        }

        public string Describe()
        {
            string state = cancelled ? "中断" : !Started ? "等待模拟固定步" : Active ? "回放中" : "完成";
            string modeName = mode == CharacterMatchMode.Apply ? "应用匹配" :
                mode == CharacterMatchMode.Observe ? "旁路观察" : "原规则";
            string drift = measurements > 0 ? $"{slip * 100f:0.0} cm（{measurements} 个足部采样间隔）" : "无连续承重样本";
            string response = !timedReverse ? "" : observedRestart ? $"，反向至起步指令 {latency * 1000f:0} ms" : "，未观测到反向起步指令";
            return $"{modeName} · {(reverse ? "制动中反向" : "直线急停")} · {state}，松键阶段位移 {brakeTravel:0.000} m，双脚累计滑移 {drift}{response}";
        }
    }
}
#endif
