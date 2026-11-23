using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterMatchChoice { Rules, Continue, Stop, Restart }

    internal struct CharacterMatchResult
    {
        public CharacterMatchChoice Choice;
        public CharacterAnimationBinding Binding;
        public float Time, Length, Cost, ContinueCost;
        public bool HasEntry => Choice == CharacterMatchChoice.Stop || Choice == CharacterMatchChoice.Restart;
    }

    internal sealed class CharacterMotionMatcher
    {
        private readonly CharacterMotionDatabase database;
        private readonly Animator animator;
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly Dictionary<int, CharacterMotionTrack> tracks = new Dictionary<int, CharacterMotionTrack>();
        private long completedReverse;
        private readonly float switchMargin;
        private readonly float maxReverseHold;
        private float reverseHold;
        public CharacterMatchMode Mode;
#if UNITY_EDITOR
        private CharacterMatchResult latest;
        private string rule, situation, fallbackReason;
        private long lastQuery;
        private Vector2 contacts;
        private CharacterMatchTrajectory forecast;
        public string Describe()
        {
            if (Mode == CharacterMatchMode.Rules) return "使用原规则";
            if (!Compatible) return "数据库、控制器或 Avatar 不匹配，使用原规则";
            if (lastQuery == 0) return "等待地面跑停或反向起步";
            if (fallbackReason != null) return $"固定步 {lastQuery} · {situation} · {fallbackReason}，使用原规则";
            string costs = latest.Choice == CharacterMatchChoice.Rules ? "" :
                $"，候选 {latest.Cost:0.00} / 续播 {latest.ContinueCost:0.00}";
            string pick = latest.Choice == CharacterMatchChoice.Rules ? "回退原规则" :
                latest.Choice == CharacterMatchChoice.Continue ? "续播" : $"{latest.Binding.StateName} @ {latest.Time:0.000}s";
            return $"固定步 {lastQuery} · {situation} · 原规则 {rule} · 匹配 {pick}{costs}\n" +
                $"左 / 右脚接触 {contacts.x:0.00} / {contacts.y:0.00}，预测 0.15s {forecast.Near:F2} / {forecast.NearYaw:0}°，" +
                $"0.30s {forecast.Far:F2} / {forecast.FarYaw:0}°（右、前，米）";
        }
#endif
        private bool Compatible => database && animator && animator.avatar == database.Avatar &&
            animator.runtimeAnimatorController == database.Controller && tracks.Count > 0;

        public CharacterMotionMatcher(Animator animator, CharacterAnimationResourceCatalog resources,
            CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            this.resources = resources;
            database = config.MatchDatabase;
            switchMargin = InputMath.NonNegative(config.MatchMargin, 0.12f);
            maxReverseHold = InputMath.NonNegative(config.MatchReverseHold, 0.08f);
            if (!database || database.Tracks == null) return;
            foreach (var track in database.Tracks)
            {
                if (track == null || !track.IsReady || track.States == null) continue;
                foreach (string state in track.States)
                    if (!string.IsNullOrWhiteSpace(state)) tracks[Animator.StringToHash(state)] = track;
            }
        }

        public void Reset()
        {
            reverseHold = 0f;
            completedReverse = 0;
#if UNITY_EDITOR
            lastQuery = 0;
#endif
        }

        public CharacterMatchResult Evaluate(CharacterAnimationIntentConverter intent, in CharacterAnimationComposition owners,
            in CharacterPlaybackObservation seen, in CharacterAnimationPlaybackPlan last, CharacterReleaseDecision ruleRelease,
            CharacterAnimationMotion motion, CharacterFootObservation feet, float dt)
        {
            if (intent.ReverseReleaseStep == 0) reverseHold = 0f;
            bool stopping = intent.Phase == CharacterLocomotionPhase.BrakeLeadIn &&
                intent.StopGait == CharacterAnimationIntentConverter.MoveKey;
            bool reversing = intent.ReverseReleaseStep > 0 && intent.ReverseReleaseStep != completedReverse;
            if ((!stopping && !reversing) || Mode == CharacterMatchMode.Rules) return default;
            var goal = motion.Trajectory.Match;
            var owner = owners.FullBody;
            bool crossingZero = reversing && owner.Key == CharacterAnimationIntentConverter.IdleKey;
#if UNITY_EDITOR
            situation = stopping ? "松键收步" : "反向起步";
            lastQuery = motion.FrameNumber;
            fallbackReason = null;
#endif
            if ((!owner.IsLocomotion && !crossingZero) || owners.LowerBody.IsValid || !last.FullBody.Demand.IsLocomotion)
            {
                if (reversing) completedReverse = intent.ReverseReleaseStep;
                return Fallback("下身归属已改变");
            }
            if (!Compatible || !goal.Valid || !motion.IsValid || motion.IsBlocked || motion.Trajectory.IsBrakeObstructed)
                return Fallback("运动预测或数据库不可用");
            if (feet.Count < 2 || !seen.Full.Ready) return Fallback("等待动画求值");

            var current = seen.Full.Destination;
            if (!tracks.TryGetValue(current.fullPathHash, out var continuing)) return Fallback("当前姿势不在库中");
            var best = new CharacterMatchResult { Cost = float.PositiveInfinity };
            CharacterAnimationBinding baseline;
            if (stopping)
            {
                const string run = CharacterAnimationIntentConverter.MoveKey;
                baseline = resources.ResolveStop(run, seen.LeftSupport, out _);
                Search(resources.ResolveStop(run, true, out _), feet.Pose, goal, motion.ActualSpeed, true, ref best);
                Search(resources.ResolveStop(run, false, out _), feet.Pose, goal, motion.ActualSpeed, true, ref best);
            }
            else
            {
                baseline = resources.ResolveBinding(owner, out _);
                if (!crossingZero) Search(baseline, feet.Pose, goal, motion.ActualSpeed, false, ref best);
            }
            int now = continuing.Index(current.normalizedTime);
            best.ContinueCost = Score(continuing, now, feet.Pose, goal, motion.ActualSpeed);
            bool mayContinue = stopping ? !intent.MainBrakeDue : reverseHold + dt <= maxReverseHold;
            bool preferContinuation = best.ContinueCost <= best.Cost + switchMargin;
            if (mayContinue && (crossingZero || best.HasEntry && preferContinuation))
                best.Choice = CharacterMatchChoice.Continue;
            else if (reversing && best.HasEntry && preferContinuation && best.Binding.StateHash == current.fullPathHash &&
                last.FullBody.StateHash == current.fullPathHash)
                best.Choice = CharacterMatchChoice.Rules;
            if (reversing)
            {
                if (best.Choice == CharacterMatchChoice.Continue) reverseHold += dt;
                else completedReverse = intent.ReverseReleaseStep;
            }
#if UNITY_EDITOR
            latest = best;
            rule = stopping && ruleRelease == CharacterReleaseDecision.ContinueStride ? "续播" : baseline.StateName;
            contacts = feet.Pose.Contact;
            forecast = goal;
#endif
            return Mode == CharacterMatchMode.Apply ? best : default;
        }

        private CharacterMatchResult Fallback(string reason)
        {
#if UNITY_EDITOR
            fallbackReason = reason;
#endif
            return default;
        }

        private void Search(CharacterAnimationBinding binding, in CharacterFootPose pose, in CharacterMatchTrajectory goal,
            float speed, bool stop, ref CharacterMatchResult best)
        {
            if (binding.IsStop != stop || !tracks.TryGetValue(binding.StateHash, out var track) || track.IsStop != stop) return;
            if (stop && (pose.Contact.x >= 0.7f && pose.Contact.y < 0.45f && binding.Variant == CharacterLocomotionVariant.StopRight ||
                         pose.Contact.y >= 0.7f && pose.Contact.x < 0.45f && binding.Variant == CharacterLocomotionVariant.StopLeft)) return;
            int count = stop ? track.Poses.Length : track.Poses.Length - 1; // 循环末帧与首帧相同。
            for (int i = 0; i < count; i++)
            {
                float seconds = track.TimeAt(i);
                if (seconds > track.LastEntrySeconds + 0.00001f || stop && seconds >= binding.BrakingClipSeconds) break;
                float cost = Score(track, i, pose, goal, speed);
                if (!(cost < best.Cost)) continue;
                best = new CharacterMatchResult { Choice = stop ? CharacterMatchChoice.Stop : CharacterMatchChoice.Restart,
                    Binding = binding, Time = seconds, Length = track.Seconds, Cost = cost };
            }
        }

        private static float Score(CharacterMotionTrack track, int index, in CharacterFootPose pose,
            in CharacterMatchTrajectory goal, float speed)
        {
            var candidate = track.Poses[index];
            float rate = track.IsStop ? 1f : speed / Mathf.Max(0.01f, track.ReferenceSpeed);
            float positions = (candidate.Left - pose.Left).sqrMagnitude + (candidate.Right - pose.Right).sqrMagnitude;
            float velocities = (candidate.LeftVelocity * rate - pose.LeftVelocity).sqrMagnitude +
                (candidate.RightVelocity * rate - pose.RightVelocity).sqrMagnitude;
            float t = track.TimeAt(index);
            float near = track.Distance(t, CharacterMatchTrajectory.NearSeconds * rate);
            float far = track.Distance(t, CharacterMatchTrajectory.FarSeconds * rate);
            // 原地循环跟随刚体转向，取区间中点朝向估计弧线；收步按其原有前进轴制动。
            Vector2 nearPath = Step(near, track.IsStop ? 0f : goal.NearYaw);
            Vector2 farPath = nearPath + Step(far - near, track.IsStop ? 0f : goal.NearYaw + goal.FarYaw);
            float trajectory = (nearPath - goal.Near).sqrMagnitude + (farPath - goal.Far).sqrMagnitude;
            return 5f * positions + 0.025f * velocities + (candidate.Contact - pose.Contact).sqrMagnitude +
                4f * trajectory;
        }

        private static Vector2 Step(float distance, float endpointYawSum)
        {
            float angle = endpointYawSum * (0.5f * Mathf.Deg2Rad);
            return new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * distance;
        }
    }
}
