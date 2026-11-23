#if UNITY_EDITOR
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum GroundReplayCase { Stop, ReverseAfterRelease, DirectLeft, DirectRight, LateRecoveryMove, RecoveryCombo, ShieldCombo, ShieldDash, GuardStress, DashTurn }

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
        private bool Shield => scenario == GroundReplayCase.ShieldCombo || scenario == GroundReplayCase.ShieldDash || scenario == GroundReplayCase.GuardStress;
        private bool Combo => scenario == GroundReplayCase.RecoveryCombo || scenario == GroundReplayCase.ShieldCombo || scenario == GroundReplayCase.GuardStress;
        private bool IsAttackReplay => Shield || scenario == GroundReplayCase.LateRecoveryMove || scenario == GroundReplayCase.RecoveryCombo;
        public CharacterReplayTrace Trace { get; }
        internal CharacterTurnReplay Turn { get; }
        private bool sentDash, interruptedByDash;
        private int dashStarts, exitStep, rightStep;
        private float startStamina, endStamina;
        private uint dashBehaviorHash = 2166136261u;
        private Vector2 replayTravel;
        private string finalSummary, comparison;
        private uint inputHash = 2166136261u;
        private int inputSteps;
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

        public CharacterMatchReplay(Vector3 heading, GroundReplayCase scenario, CharacterMatchMode mode, float extraRunSeconds, bool reuseMixers)
        {
            forward = new Vector2(heading.x, heading.z).normalized;
            this.scenario = scenario;
            var right = new Vector2(forward.y, -forward.x);
            newDirection = scenario == GroundReplayCase.DirectLeft ? -right :
                scenario == GroundReplayCase.DirectRight || scenario == GroundReplayCase.DashTurn ? right : -forward;
            releaseTime = (scenario == GroundReplayCase.GuardStress ? 7.5 : 1.35) + extraRunSeconds;
            if (IsAttackReplay) Trace = new CharacterReplayTrace(reuseMixers);
            if (Direct) Turn = new CharacterTurnReplay(forward);
            this.mode = mode;
        }

        public bool TryInput(float dt, out CharacterInputSnapshot input)
        {
            input = default;
            if (!Active) return false;
            if (scenario == GroundReplayCase.DashTurn)
            {
                elapsed += dt;
                bool dash = !sentDash && elapsed >= 0.5;
                if (dash) sentDash = true;
                Vector2 move = !sentDash || dash ? forward : newDirection;
                input = new CharacterInputSnapshot(move, dash);
                if (elapsed >= 3d) { Active = false; input = default; }
                return true;
            }
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
            bool combo = Combo;
            bool firstPress = false, dash = false;
            bool ready = !Shield || scenario == GroundReplayCase.GuardStress || Trace.HasSeenGrip;
            if (!sentFirst && ready && elapsed >= releaseTime)
            {
                firstPress = sentFirst = press = true;
            }
            else if (lastAttack.Move == CharacterAttackMove.LightOne)
            {
                float release = lastAttack.Handoff.ReleaseFrom;
                if (scenario == GroundReplayCase.ShieldDash && !sentDash && lastAttack.Progress >= release) dash = sentDash = true;
                if (!combo && lastAttack.Progress >= Mathf.Lerp(release, 1f, 0.5f))
                    moveAfterRelease = true;
                if (combo && !sentSecond && moveApproved &&
                    lastAttack.Progress >= release + 0.04f / lastAttack.Duration)
                    sentSecond = press = true;
            }

            bool move = elapsed >= 0.45 && (!sentFirst || combo || moveAfterRelease);
            bool guard = Shield && elapsed >= 0.45 && (!sentFirst || firstPress);
            if (scenario == GroundReplayCase.GuardStress && elapsed < 5.25) guard &= (elapsed - 0.45) % 0.6 < 0.32;
            input = new CharacterInputSnapshot(move || dash ? forward : Vector2.zero, dash, guard,
                press ? CharacterBehaviorId.Attack : CharacterBehaviorId.None);
            elapsed += dt;
            if (finishAfter > 0d && elapsed >= finishAfter || sentFirst && !sawFirst && elapsed > releaseTime + 0.75 || elapsed > releaseTime + 8d)
            {
                if (!sawFirst) replayIssue = "轻击一未获准";
                else if (combo && !sawSecond) replayIssue = "未接入轻击二，请核对接续窗口";
                else if (scenario == GroundReplayCase.ShieldDash && !interruptedByDash) replayIssue = "冲刺未在允许阶段打断攻击";
                else if (scenario == GroundReplayCase.LateRecoveryMove && !moveAfterRelease) replayIssue = "未到达后摇中段";
                if (Shield && !Trace.HasSeenGrip) replayIssue = "未观测到持盾实例的可见握持贡献";
                Active = false;
                input = default;
            }
            return true;
        }

        public void OnStep(in CharacterControllerFrameResult frame, in CharacterForecastSample solved,
            double stepTime, CharacterMovementForecast forecast)
        {
            if (Active && Started && scenario == GroundReplayCase.DashTurn)
            {
                inputSteps++;
                if (inputSteps == 1) startStamina = frame.Behavior.StaminaRemaining;
                endStamina = frame.Behavior.StaminaRemaining;
                if (frame.Behavior.DashStarted) dashStarts++;
                if (frame.Behavior.DashEnded) exitStep = inputSteps;
                if (rightStep == 0 && exitStep > 0 && frame.Behavior.MoveApproved &&
                    Vector2.Dot(frame.Input.Direction, newDirection) > 0.9f) rightStep = inputSteps;
                Vector2 delta = new Vector2(frame.ActualDisplacement.x, frame.ActualDisplacement.z);
                replayTravel += new Vector2(Vector2.Dot(delta, newDirection), Vector2.Dot(delta, forward));
                unchecked
                {
                    dashBehaviorHash = (dashBehaviorHash ^ (uint)frame.Behavior.ActiveBehaviors) * 16777619u;
                    dashBehaviorHash = (dashBehaviorHash ^ (uint)frame.Behavior.StartedBehaviors) * 16777619u;
                    dashBehaviorHash = (dashBehaviorHash ^ (uint)frame.Behavior.EndedBehaviors) * 16777619u;
                    dashBehaviorHash = (dashBehaviorHash ^ (uint)Mathf.RoundToInt(frame.Behavior.DashCooldownRemaining * 1000f)) * 16777619u;
                }
                return;
            }
            if (Active && IsAttackReplay)
            {
                if (!Started) return;
                inputSteps++;
                unchecked
                {
                    inputHash = (inputHash ^ (uint)frame.Input.Requests) * 16777619u;
                    inputHash = (inputHash ^ (uint)Mathf.RoundToInt(frame.Input.Direction.magnitude * 1000f)) * 16777619u;
                    inputHash = (inputHash ^ (uint)Mathf.RoundToInt(frame.DeltaTime * 1000000f)) * 16777619u;
                }
                if (!sentFirst) return;
                lastAttack = frame.Behavior.Attack;
                moveApproved = frame.Behavior.MoveApproved;
                interruptedByDash |= frame.Behavior.DashStarted && (frame.Behavior.InterruptedBehaviors & CharacterBehaviorId.Attack) != 0;
                sawFirst |= lastAttack.Move == CharacterAttackMove.LightOne;
                sawSecond |= lastAttack.Move == CharacterAttackMove.LightTwo;
                if (sawFirst && (frame.Behavior.ActiveBehaviors & CharacterBehaviorId.Attack) == 0 && finishAfter == 0d)
                    finishAfter = elapsed + 0.35;
                return;
            }
            if (Active && timedReverse && Vector2.Dot(frame.Input.Direction, newDirection) > 0.9f)
            {
                acceptedReverse = true;
                Turn?.Record(frame, solved, stepTime, forecast);
            }
            if (Active && phase == 2)
                brakeTravel += Vector3.ProjectOnPlane(frame.ActualDisplacement, Vector3.up).magnitude;
        }

        public void Observe(CharacterAnimationPipeline pipeline)
        {
            if (!Active || !Started) return;
            if (IsAttackReplay)
            {
                float? leftSlip = Measure(pipeline.Ik, CharacterIkLimb.LeftFoot, ref left);
                float? rightSlip = Measure(pipeline.Ik, CharacterIkLimb.RightFoot, ref right);
                Trace.Observe(pipeline, leftSlip, rightSlip);
                return;
            }
            if (phase == 2 || phase == 3 || Direct && phase == 1)
            {
                var leftSlip = Measure(pipeline.Ik, CharacterIkLimb.LeftFoot, ref left, phase != 1);
                var rightSlip = Measure(pipeline.Ik, CharacterIkLimb.RightFoot, ref right, phase != 1);
                if (phase == 3) Turn?.Observe(pipeline.ReplaySnapshot, left.Surface != 0, right.Surface != 0, leftSlip, rightSlip);
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
                entry = $"{layer.StateName} @ {layer.Start.Phase:F3}（归一化）";
                query = pipeline.Matcher.Describe();
            }
        }

        private float? Measure(CharacterAnimationIk ik, CharacterIkLimb limb, ref Foot last, bool accumulate = true)
        {
            float? distance = null;
            if (ik.TryReadSupport(limb, out int surface, out Vector3 local, out var toWorld, out var normal))
            {
                if (last.Surface == surface)
                {
                    var delta = Vector3.ProjectOnPlane(toWorld.MultiplyVector(local - last.Local), normal);
                    distance = delta.magnitude;
                    if (accumulate)
                    {
                        slip += distance.Value;
                        measurements++;
                    }
                }
                last = new Foot { Surface = surface, Local = local };
            }
            else last = default;
            return distance;
        }

        public string Compare(CharacterMatchReplay other)
        {
            if (!Active && other != null && !other.Active && !cancelled && !other.cancelled &&
                scenario == GroundReplayCase.DashTurn && other.scenario == scenario)
            {
                bool same = dashStarts == 1 && other.dashStarts == 1 && rightStep > 0 && rightStep == other.rightStep &&
                    inputSteps == other.inputSteps && dashBehaviorHash == other.dashBehaviorHash &&
                    Mathf.Approximately(startStamina, other.startStamina) && Mathf.Approximately(endStamina, other.endStamina);
                return $"前冲右移对照 · 行为/体力/冷却序列{(same ? "一致" : "不一致或起点不同")}，局部累计位移差 {Vector2.Distance(replayTravel, other.replayTravel):F4} m";
            }
            if (Active || other == null || other.Active || scenario != GroundReplayCase.GuardStress || other.scenario != scenario ||
                Trace.ReuseMixers == other.Trace.ReuseMixers) return null;
            if (comparison != null) return comparison;
            Describe(); other.Describe();
            if (cancelled || other.cancelled || replayIssue != null || other.replayIssue != null ||
                mode != other.mode || inputSteps != other.inputSteps || inputHash != other.inputHash || !Trace.CompleteSamples || !other.Trace.CompleteSamples)
                return comparison = "两次记录未完成同一固定输入，不比较性能。";
            if (!Trace.Events.SameReceipts(other.Trace.Events))
                return comparison = "事件未通过逐标记核验或两次实际回调顺序不同，不能判定复用等效。";
            return comparison = $"固定输入和实际事件序列一致（{inputSteps} 步）。" + Trace.Difference(other.Trace);
        }

        public void Cancel()
        {
            if (Active) { Active = false; cancelled = true; }
        }

        public string Describe()
        {
            string state = cancelled ? "中断" : !Started ? "等待模拟固定步" : Active ? "回放中" : "完成";
            if (scenario == GroundReplayCase.DashTurn)
                return $"前冲后持续右移 · {state} · 突进启动 {dashStarts} 次，第 {exitStep} 步退出，第 {rightStep} 步接右移。" +
                    $"体力 {startStamina:F1} → {endStamina:F1}，局部累计位移 {replayTravel:F3} m";
            if (IsAttackReplay)
            {
                string action = scenario switch
                {
                    GroundReplayCase.ShieldDash => "持盾走路 → 轻击 → 冲刺",
                    GroundReplayCase.ShieldCombo => "持盾走路 → 两段轻击",
                    GroundReplayCase.GuardStress => "连续举盾与取消 → 持盾连击",
                    GroundReplayCase.RecoveryCombo => "后摇接轻击二",
                    _ => "后摇转移动"
                };
                string result = $"{action} · {state} · {replayIssue ?? (sawSecond ? "已接轻击二" : interruptedByDash ? "冲刺已打断" : sawFirst ? "已进入轻击一" : "等待轻击一")}";
                if (Active) return result + $"，已记录 {Trace.Count} 帧";
                return finalSummary ??= result + $"\n承重滑移 {slip * 100f:F2} cm（{measurements} 个采样间隔）\n" +
                    Trace.Events.Describe() + "\n" + Trace.Summary();
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
