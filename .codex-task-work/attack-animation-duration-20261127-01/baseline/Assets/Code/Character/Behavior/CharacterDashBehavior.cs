using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>维护定向冲刺的方向、速度、持续时间和跨步冷却。</summary>
    public sealed class CharacterDashBehavior : CharacterBehavior, ICharacterIntervalMotion
    {
        private CharacterDashRuntimeState state;

        public float RemainingDuration => state.RemainingDuration;
        public Vector2 Direction => state.Direction;
        public float Cooldown => state.Cooldown;
        public override bool IsComplete => RemainingDuration == 0f;
        public override double NextBoundaryDelay => IsActive ? RemainingDuration : double.PositiveInfinity;

        // 将冲刺执行状态和冷却复制到独立行为实例。
        protected override void CopyRuntimeStateTo(CharacterBehavior target) =>
            ((CharacterDashBehavior)target).state = state;

        // 冻结本次冲刺的方向、速度、存续区间和起步实例。
        public CharacterFrozenMotion FreezeMotion(in CharacterMotionCapture capture) =>
            new CharacterFrozenDashMotion(state);

        /// <summary>只读判断冲刺请求和体力是否满足启动条件。</summary>
        public override bool Evaluate(CharacterBehaviorContext context) =>
            CanStart(context, context.DashCooldownRemaining) && CharacterStaminaBlackboard.CanAfford(context.StaminaRemaining, GetStaminaCost(context));

        // 在实际准入处区分没有有效按下、冷却和无效的冲刺时长或速度。
        protected override CharacterBehaviorRejectReason CheckRequest(CharacterBehaviorContext context)
        {
            if (!context.Input.DashPressed || IsActive) return CharacterBehaviorRejectReason.StateCondition;
            if (Cooldown > 0f) return CharacterBehaviorRejectReason.Cooldown;
            return HasValidMotion(context.Settings) ? CharacterBehaviorRejectReason.None : CharacterBehaviorRejectReason.InvalidConfiguration;
        }

        // 用指定来源的冷却判断公开查询或本实例的启动请求。
        private bool CanStart(CharacterBehaviorContext context, float cooldown)
        {
            if (IsActive || !context.Input.DashPressed || cooldown > 0f)
                return false;

            return HasValidMotion(context.Settings);
        }

        // 使用实际运动采用的回退数值检查能否产生有效位移。
        private static bool HasValidMotion(CharacterMovementSettings settings)
        {
            float duration = InputMath.NonNegative(settings.DashDuration, CharacterMovementSettings.DefaultDashDuration);
            float speed = InputMath.NonNegative(settings.DashSpeed, CharacterMovementSettings.DefaultDashSpeed);

            return duration > 0f && speed > 0f;
        }

        // 读取本次冲刺的配置体力消耗。
        public override float GetStaminaCost(CharacterBehaviorContext context) =>
            InputMath.NonNegative(context.Settings.DashStaminaCost, CharacterMovementSettings.DefaultDashStaminaCost);

        // 当前区间的准入完成后才流逝冷却，禁止长帧起点的按下提前借用帧末时间。
        public override void AfterStep(float seconds) => state.Cooldown = InputMath.Elapsed(Cooldown, seconds);

        /// <summary>由流程在批准后调用，锁定本次冲刺参数，依次使用输入方向、朝向和世界正 Z 作为方向来源。</summary>
        public override void Start(CharacterBehaviorContext context)
        {
            CharacterMovementSettings settings = context.Settings;
            if (!InputMath.TryNormalizeDirection(context.Input.Direction, out Vector2 direction) &&
                !InputMath.TryNormalizeDirection(context.Facing, out direction))
                direction = Vector2.up;

            state.InstanceId++;
            state.Direction = direction;
            state.RemainingDuration = InputMath.NonNegative(settings.DashDuration, CharacterMovementSettings.DefaultDashDuration);
            state.Speed = InputMath.NonNegative(settings.DashSpeed, CharacterMovementSettings.DefaultDashSpeed);
            state.Cooldown = InputMath.NonNegative(settings.DashCooldown, CharacterMovementSettings.DefaultDashCooldown);
            IsActive = true;
        }

        /// <summary>结束冲刺并保留最近一次方向供流程结果读取。</summary>
        public override void End()
        {
            IsActive = false;
            state.RemainingDuration = 0f;
        }

        /// <summary>重新初始化角色时清除全部冲刺状态，包括上一次保留的方向。</summary>
        internal override void Reset()
        {
            End();
            state = default;
        }

        /// <summary>推进冲刺时长并返回本步的世界 X/Z 位移。</summary>
        public override Vector2 Update(CharacterBehaviorContext context) => Advance(context, out _);

        /// <summary>推进本步冲刺，并把提前结束后剩余的时间交还普通移动。</summary>
        public override CharacterBehaviorMotion AdvanceStep(CharacterBehaviorContext context)
        {
            Vector2 displacement = Advance(context, out float seconds);
            float remaining = seconds > 0f ? InputMath.Elapsed(context.DeltaTime, seconds) : 0f;
            return new CharacterBehaviorMotion(displacement, remaining, motion: state.ActionMotion);
        }

        // 按步长与冲刺余量的较小值计算位移和实际占用时间。
        private Vector2 Advance(CharacterBehaviorContext context, out float seconds)
        {
            seconds = 0f;
            if (!IsActive)
                return Vector2.zero;

            seconds = Math.Min(context.DeltaTime, RemainingDuration);
            Vector2 displacement = state.Displacement(seconds);
            state.RemainingDuration = InputMath.Elapsed(RemainingDuration, context.DeltaTime);
            return displacement;
        }
    }

    /// <summary>保存冲刺实例执行期间锁定的数值及跨活动期冷却。</summary>
    internal struct CharacterDashRuntimeState
    {
        internal long InstanceId;
        internal Vector2 Direction;
        internal float Speed, RemainingDuration, Cooldown;
        internal readonly CharacterActionMotion ActionMotion => new CharacterActionMotion
        {
            UsesBurstVelocity = true,
            SnapsFacingOnStart = true,
            Facing = Direction
        };

        // 对有限冲刺占用时长积分得到世界 X/Z 位移。
        internal readonly Vector2 Displacement(float seconds) => Direction * (Speed * seconds);
    }

    /// <summary>在捕获的冲刺结束前积分恒速位移，结束后交还普通移动。</summary>
    internal sealed class CharacterFrozenDashMotion : CharacterFrozenMotion
    {
        private readonly CharacterDashRuntimeState state;

        // 保存由冲刺实例按值传递的运动数据。
        internal CharacterFrozenDashMotion(CharacterDashRuntimeState state) => this.state = state;

        // 返回仍落在查询范围内的冲刺结束时刻。
        public override double NextBoundary(double after, double limit) =>
            state.RemainingDuration > after ? Math.Min(limit, state.RemainingDuration) : limit;

        // 按有效冲刺和普通移动的交集分别积分，正时长冲刺始终附带同一实例序号。
        public override CharacterMotionSample Sample(double start, double end)
        {
            if (end <= start) return default;

            double activeSeconds = Math.Max(0d, Math.Min(end, state.RemainingDuration) - Math.Max(start, 0d));
            float ordinarySeconds = (float)(end - start - activeSeconds);
            if (activeSeconds == 0d)
                return new CharacterMotionSample(new CharacterBehaviorMotion(Vector2.zero, ordinarySeconds, controlsDisplacement: false));

            var motion = new CharacterBehaviorMotion(state.Displacement((float)activeSeconds), ordinarySeconds, motion: state.ActionMotion);
            return new CharacterMotionSample(motion, state.InstanceId);
        }
    }
}
