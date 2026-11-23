#if UNITY_EDITOR
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum GroundReplayCase { Stop, ReverseAfterRelease, DirectLeft, DirectRight, LateRecoveryMove, RecoveryCombo }

    // 只在 Inspector 主动触发。输入按固定步生成，两种选姿模式走完全相同的移动入口。
    internal sealed class CharacterMatchReplay
    {
        private struct Foot
        {
            public int Surface;
            public Vector3 Local;
        }

        private readonly Vector2 forward;
        private readonly GroundReplayCase scenario;
        private readonly Vector2 newDirection;
        private bool IsAttackReplay => scenario == GroundReplayCase.LateRecoveryMove || scenario == GroundReplayCase.RecoveryCombo;
        private CharacterAttackState lastAttack;
        private bool sentFirst, sentSecond, moveAfterRelease, sawFirst, sawSecond, moveApproved;
        private double finishAfter;
        private string replayIssue;
        private bool Direct => scenario == GroundReplayCase.DirectLeft || scenario == GroundReplayCase.DirectRight;
        private bool ChangesDirection => scenario != GroundReplayCase.Stop;
        private string entry, query;
        private int emitted;
        private float directionSlip;
        private int directionSamples;
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

        public CharacterMatchReplay(Vector3 heading, GroundReplayCase scenario, CharacterMatchMode mode, float extraRunSeconds)
        {
            forward = new Vector2(heading.x, heading.z).normalized;
            this.scenario = scenario;
            var right = new Vector2(forward.y, -forward.x);
            newDirection = scenario == GroundReplayCase.DirectLeft ? -right :
                scenario == GroundReplayCase.DirectRight ? right : -forward;
            releaseTime = 1.35 + extraRunSeconds;
            this.mode = mode;
        }

        public bool TryInput(float dt, out CharacterInputSnapshot input)
        {
            input = default;
            if (!Active) return false;
            if (IsAttackReplay) return AttackInput(dt, out input);
            double brakeEnd = releaseTime + (Direct ? 0d : ChangesDirection ? 0.12 : 0.85); // 起跑前留出落稳时间，松键后在两段制动交接时反向。
            int next = elapsed < 0.45 ? 0 : elapsed < releaseTime ? 1 : elapsed < brakeEnd ? 2 :
                ChangesDirection && elapsed < brakeEnd + 0.70 ? 3 : 4;
            if (next == 3 && phase != 3) { reverseAt = Time.fixedTimeAsDouble; timedReverse = true; }
            phase = next;
            elapsed += dt;
            if (elapsed > brakeEnd + (ChangesDirection ? 1.35 : 0.35))
            {
                Active = false;
                return true;
            }
            Vector2 direction = phase == 1 ? forward : phase == 3 ? newDirection : Vector2.zero;
            input = new CharacterInputSnapshot(direction, false);
            return true;
        }

        private bool AttackInput(float dt, out CharacterInputSnapshot input)
        {
            bool press = false;
            bool combo = scenario == GroundReplayCase.RecoveryCombo;
            if (!sentFirst && elapsed >= releaseTime)
            {
                sentFirst = press = true;
            }
            else if (lastAttack.Move == CharacterAttackMove.LightOne)
            {
                float release = lastAttack.Handoff.ReleaseFrom;
                if (!combo && lastAttack.Progress >= Mathf.Lerp(release, 1f, 0.5f))
                    moveAfterRelease = true;
                if (combo && !sentSecond && moveApproved &&
                    lastAttack.Progress >= release + 0.04f / lastAttack.Duration)
                    sentSecond = press = true;
            }

            bool move = elapsed >= 0.45 && (!sentFirst || combo || moveAfterRelease);
            input = new CharacterInputSnapshot(move ? forward : Vector2.zero, false, false,
                press ? CharacterBehaviorId.Attack : CharacterBehaviorId.None);
            elapsed += dt;
            if (finishAfter > 0d && elapsed >= finishAfter || sentFirst && !sawFirst && elapsed > releaseTime + 0.75)
            {
                if (!sawFirst) replayIssue = "轻击一未获准";
                else if (combo && !sawSecond) replayIssue = "未接入轻击二，请核对接续窗口";
                else if (!combo && !moveAfterRelease) replayIssue = "未到达后摇中段";
                Active = false;
                input = default;
            }
            return true;
        }

        public void OnStep(in CharacterControllerFrameResult frame)
        {
            if (Active && IsAttackReplay)
            {
                if (!sentFirst) return;
                lastAttack = frame.Behavior.Attack;
                moveApproved = frame.Behavior.MoveApproved;
                sawFirst |= lastAttack.Move == CharacterAttackMove.LightOne;
                sawSecond |= lastAttack.Move == CharacterAttackMove.LightTwo;
                if (sawFirst && (frame.Behavior.ActiveBehaviors & CharacterBehaviorId.Attack) == 0 && finishAfter == 0d)
                    finishAfter = elapsed + 0.35;
                return;
            }
            if (Active && timedReverse && Vector2.Dot(frame.Input.Direction, newDirection) > 0.9f)
                acceptedReverse = true;
            if (Active && phase == 2)
                brakeTravel += Vector3.ProjectOnPlane(frame.ActualDisplacement, Vector3.up).magnitude;
        }

        public void Observe(CharacterAnimationPipeline pipeline)
        {
            if (!Active || !Started || IsAttackReplay) return;
            if (phase == 2 || phase == 3)
            {
                Measure(pipeline.Ik, CharacterIkLimb.LeftFoot, ref left);
                Measure(pipeline.Ik, CharacterIkLimb.RightFoot, ref right);
            }
            else left = right = default;
            if (phase == 3)
            {
                directionSlip = slip;
                directionSamples = measurements;
            }
            var layer = pipeline.DisplayedBase;
            if (acceptedReverse && phase == 3 && query == null)
                query = pipeline.Matcher.Describe();
            bool isTurn = layer.Binding.Variant == CharacterLocomotionVariant.TurnLeft ||
                layer.Binding.Variant == CharacterLocomotionVariant.TurnRight;
            bool relevant = phase == 3 && acceptedReverse && layer.Demand.IsLocomotion && !layer.Binding.IsStop &&
                layer.Command != CharacterAnimationPlaybackCommand.Keep && (!Direct || isTurn);
            if (relevant) emitted++;
            if (relevant && !observedRestart)
            {
                latency = Mathf.Max(0f, (float)(Time.timeAsDouble - reverseAt));
                observedRestart = true;
                entry = $"{layer.StateName} @ {layer.StartNormalizedTime:F3}（归一化）";
                query = pipeline.Matcher.Describe();
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
            if (IsAttackReplay)
            {
                string action = scenario == GroundReplayCase.RecoveryCombo ? "刚恢复移动时接轻击二" : "后摇过半再移动";
                return $"{action} · {state} · {(replayIssue ?? (sawSecond ? "已接入轻击二" : sawFirst ? "已进入轻击一" : "等待轻击一"))}";
            }
            string modeName = mode == CharacterMatchMode.Apply ? "应用匹配" :
                mode == CharacterMatchMode.Observe ? "旁路观察" : mode == CharacterMatchMode.LegacyBaseline ? "旧版跑停匹配" : "原规则";
            string drift = measurements > 0 ? $"{slip * 100f:0.0} cm（{measurements} 个足部采样间隔）" : "无连续承重样本";
            string response = !timedReverse ? "" : observedRestart ? $"，改向至播放指令 {latency * 1000f:0} ms · {entry} · {emitted} 次指令" : "，尚无对应改向播放指令";
            string label = scenario == GroundReplayCase.DirectLeft ? "连续左转" :
                scenario == GroundReplayCase.DirectRight ? "连续右转" : ChangesDirection ? "制动中反向" : "直线急停";
            string travel = Direct ? "" : $"松键位移 {brakeTravel:0.000} m，";
            string samples = Direct ? directionSamples == 0 ? "无连续承重样本" :
                $"{directionSlip * 100f:0.0} cm（{directionSamples} 个采样间隔）" : drift;
            return $"{modeName} · {label} · {state}，{travel}双脚累计滑移 {samples}{response}" +
                (query == null ? "" : "\n" + query);

        }
    }
}
#endif
