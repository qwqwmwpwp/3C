using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>计算单招攻击的起手制动、出招前移和收招交还曲线，并保留当前航向。</summary>
    internal struct CharacterAttackMotion
    {
        private float startupTurnSpeed, braking, strikeTravel;
        private readonly float duration, strike, recovery;
        private Vector2 requestedFacing, strikeDirection;
        private readonly float? openingDistance; // 仅起手有值，零米也由攻击接管前摇。
        private readonly Vector2 openingDirection;
        internal bool HasOpening => openingDistance.HasValue;

        // 锁定当前招式运动参数及起手方向。
        internal CharacterAttackMotion(CharacterAttackMoveSettings settings, CharacterBehaviorContext context,
            float duration, float strike, float recovery, float? openingDistance = null, bool movingOpening = false)
        {
            this = default;
            BindSettings(settings);
            this.duration = duration;
            this.openingDistance = openingDistance;
            openingDirection = movingOpening ? context.GroundVelocity.normalized : Vector2.zero;
            this.strike = strike;
            this.recovery = recovery;
            requestedFacing = context.Input.Direction == Vector2.zero ? context.Facing : context.Input.Direction.normalized;
            strikeDirection = context.Facing.normalized;
        }

        // 读取配置中的运动数值，保留动作时钟和已锁定的航向。
        private void BindSettings(CharacterAttackMoveSettings settings)
        {
            startupTurnSpeed = InputMath.NonNegative(settings.StartupTurnSpeed, 180f);
            braking = Mathf.Max(0.01f, InputMath.NonNegative(settings.Braking, 28f));
            strikeTravel = InputMath.NonNegative(settings.StrikeTravel, 0f);
        }

        // 在当前动作时刻冻结尚可变化的前摇目标和出招方向。
        internal readonly CharacterAttackMotion Capture(Vector2 direction, Vector2 facing, double elapsed)
        {
            var copy = this;
            if (elapsed < strike) copy.FollowStartupInput(direction);
            if (elapsed <= strike) copy.strikeDirection = facing.normalized;
            return copy;
        }

        // 实走推进时仅在实际经过前摇或首次进入出招段时更新航向。
        internal Vector2 Advance(CharacterBehaviorContext context, double before, double elapsed,
            out CharacterActionMotion stepMotion, out float movementSeconds)
        {
            if (before < strike && elapsed > before) FollowStartupInput(context.Input.Direction);
            if (before <= strike && elapsed > strike) strikeDirection = context.Facing.normalized;
            return Sample(context.Input.Direction, context.Settings.MoveTurnSpeed, before, elapsed, out stepMotion, out movementSeconds);
        }

        // 在用户提供有效方向时更新前摇目标。
        private void FollowStartupInput(Vector2 direction)
        {
            if (direction != Vector2.zero) requestedFacing = direction.normalized;
        }

        // 对限定在招式时钟内的区间积分，每阶段使用自身交集端点的平均权重。
        internal readonly Vector2 Sample(Vector2 direction, float turnSpeed, double before, double elapsed,
            out CharacterActionMotion stepMotion, out float movementSeconds)
        {
            double startupStart = Math.Min(before, strike);
            double startupEnd = Math.Min(elapsed, strike);
            double recoveryStart = Math.Max(before, recovery);
            double recoveryEnd = Math.Max(elapsed, recovery);
            float startupTime = (float)(startupEnd - startupStart);
            float strikeTime = (float)Math.Max(0d, Math.Min(elapsed, recovery) - Math.Max(before, strike));
            float recoveryTime = (float)(recoveryEnd - recoveryStart);
            float meanStartup = startupTime > 0f ? (float)((startupStart + startupEnd) / (2d * strike)) : 0f;
            float meanRecovery = recoveryTime > 0f
                ? (float)(((recoveryStart - recovery) + (recoveryEnd - recovery)) / (2d * ((double)duration - recovery))) : 0f;
            bool openingStep = openingDistance.HasValue && startupTime > 0f;
            movementSeconds = (openingStep ? 0f : startupTime * (1f - meanStartup)) + recoveryTime * meanRecovery;
            stepMotion = new CharacterActionMotion
            {
                OpeningStep = openingStep,
                StartPhase = (float)(before / duration),
                EndPhase = (float)(elapsed / duration),
                Duration = duration,
                Facing = recoveryTime > 0f ? direction : requestedFacing,
                ControlsFacing = true,
                MaxTurn = startupTime * startupTurnSpeed + recoveryTime * meanRecovery * Mathf.Max(0f, turnSpeed),
                Braking = startupTime > 0f ? braking : 0f,
                RecoversMovement = recoveryTime > 0f
            };
            float stride = openingStep ? openingDistance.GetValueOrDefault() * 2f * startupTime / strike * (1f - meanStartup) : 0f;
            float travel = strikeTime > 0f ? strikeTravel * strikeTime / (recovery - strike) : 0f;
            return openingDirection * stride + strikeDirection * travel;
        }
    }

    /// <summary>按捕获时的攻击曲线积分位移，无片段或超过招式终点的部分交还给普通移动。</summary>
    internal sealed class CharacterFrozenAttackMotion : CharacterFrozenMotion
    {
        private readonly CharacterAttackMotion motion;
        private readonly bool hasMotion;
        private readonly double elapsed;
        private readonly float strike, recovery, duration, turnSpeed;
        private readonly Vector2 direction;
        internal override CharacterAlignmentWindow AlignmentWindow { get; }

        // 只读取当前招式时钟和持续输入，复制可反复积分的运动值。
        internal CharacterFrozenAttackMotion(in CharacterAttackRuntimeState state, in CharacterMotionCapture capture)
        {
            hasMotion = state.HasAttackMotion;
            elapsed = state.Elapsed;
            strike = state.Strike;
            recovery = state.Recovery;
            duration = state.Duration;
            direction = capture.Direction;
            turnSpeed = capture.TurnSpeed;
            if (hasMotion) motion = state.AttackMotion.Capture(direction, capture.Facing, elapsed);
            // 只接管原攻击已有的可控路段，后续招式的普通移动前摇与收招仍按原规则交还。
            var settings = hasMotion ? state.Alignment : default;
            AlignmentWindow = new CharacterAlignmentWindow(state.InstanceId, settings,
                Math.Max(settings.Window.x * duration, motion.HasOpening ? 0f : strike) - elapsed,
                Math.Min(settings.Window.y * duration, recovery) - elapsed);
        }

        // 依次查找出招、收招和招式结束的相对时刻。
        public override double NextBoundary(double after, double limit)
        {
            double hit = strike - elapsed;
            if (hit > after) return Math.Min(limit, hit);
            double recover = recovery - elapsed;
            if (recover > after) return Math.Min(limit, recover);
            double finish = duration - elapsed;
            return finish > after ? Math.Min(limit, finish) : limit;
        }

        // 按捕获后的相对秒数求攻击和普通移动的贡献，重复采样不改变航向。
        public override CharacterMotionSample Sample(double start, double end)
        {
            if (end <= start) return default;

            double actionStart = Math.Max(start, 0d);
            double actionEnd = Math.Min(end, duration - elapsed);
            if (!hasMotion || actionEnd <= actionStart)
                return new CharacterMotionSample(new CharacterBehaviorMotion(Vector2.zero, (float)(end - start), controlsDisplacement: false));

            double before = actionStart + elapsed;
            double after = actionEnd + elapsed;
            Vector2 displacement = motion.Sample(direction, turnSpeed, before, after, out var action, out float ordinarySeconds);
            ordinarySeconds += (float)((actionStart - start) + (end - actionEnd));
            bool controlsDisplacement = action.OpeningStep || before < recovery && after > strike;
            return new CharacterMotionSample(new CharacterBehaviorMotion(displacement, ordinarySeconds, controlsDisplacement, action));
        }
    }
}
