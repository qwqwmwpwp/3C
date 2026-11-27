using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>记录已通过自身准入检查的行为请求及其体力报价。</summary>
    internal class CharacterBehaviorStart
    {
        internal readonly CharacterBehaviorContext Context;
        internal readonly float Cost;

        // 保存同一次判断准备好的输入和体力报价，供启动使用。
        internal CharacterBehaviorStart(CharacterBehaviorContext context, float cost)
        {
            Context = context;
            Cost = cost;
        }
    }

    /// <summary>维护单个流程的共享体力，负责初值、扣费及预测复制。</summary>
    internal sealed class CharacterStaminaBlackboard
    {
        private bool initialized;
        internal float Remaining { get; private set; }

        // 检查消耗报价有效且当前可用体力足够。
        internal static bool CanAfford(float available, float cost) =>
            cost >= 0f && !float.IsInfinity(cost) && available >= cost;

        // 返回体力快照，未初始化时只读取配置初值。
        internal float Read(CharacterMovementSettings settings) => initialized ? Remaining :
            InputMath.NonNegative(settings.InitialStamina, CharacterMovementSettings.DefaultInitialStamina);

        // 首个有效步提交资源初值。
        internal void Initialize(CharacterMovementSettings settings)
        {
            if (!initialized) Reset(settings);
        }

        // 按本次角色配置恢复体力初值。
        internal void Reset(CharacterMovementSettings settings)
        {
            Remaining = InputMath.NonNegative(settings.InitialStamina, CharacterMovementSettings.DefaultInitialStamina);
            initialized = true;
        }

        // 唯一扣费入口，使用本次获准请求已经校验的报价。
        internal void Spend(CharacterBehaviorStart request) => Remaining -= request.Cost;

        // 把体力值和初始化状态拷贝到独立黑板。
        internal void CopyStateTo(CharacterStaminaBlackboard target)
        {
            target.Remaining = Remaining;
            target.initialized = initialized;
        }
    }

    /// <summary>把下层持有的状态投影为行为输入快照和帧结果。</summary>
    internal sealed class CharacterBehaviorRuntimeState
    {
        private readonly CharacterStaminaBlackboard stamina = new CharacterStaminaBlackboard();
        private readonly CharacterDashBehavior dash;
        internal bool IsPrediction;
        internal float StaminaRemaining => stamina.Remaining;
        internal float DashCooldownRemaining => dash.Cooldown;

        // 绑定本流程实际执行的冲刺实例。
        internal CharacterBehaviorRuntimeState(CharacterDashBehavior dash) => this.dash = dash;

        // 恢复流程的共享资源。
        internal void Reset(CharacterMovementSettings settings) => stamina.Reset(settings);

        // 首次推进时提交共享资源的初值。
        internal void BeginStep(CharacterMovementSettings settings) => stamina.Initialize(settings);

        // 读取本次申请和实际执行所需的状态快照。
        internal CharacterBehaviorContext CreateContext(CharacterInputSnapshot input, Vector2 facing, float seconds,
            CharacterMovementSettings settings, double simulationTime, Vector2 groundVelocity, bool separateBoundaries = false) =>
            new CharacterBehaviorContext(input, facing, seconds, settings, stamina.Read(settings), dash.Cooldown,
                simulationTime, groundVelocity, separateBoundaries, IsPrediction);

        // 提交获准请求并刷新后续申请读取的上下文。
        internal CharacterBehaviorContext CommitStart(CharacterBehaviorStart request, CharacterBehaviorContext context)
        {
            stamina.Spend(request);
            return CreateContext(context.Input, context.Facing, context.DeltaTime,
                context.Settings, context.SimulationTime, context.GroundVelocity, context.SeparateBoundaries);
        }

        // 将保留的资源值和冲刺状态填入兼容的帧输出。
        internal void ReadOutcome(in CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            step.StaminaRemaining = context.StaminaRemaining;
            step.DashCooldownRemaining = dash.Cooldown;
            step.DashDirection = dash.Direction;
        }

        // 预测状态复制时同步共享资源，动作各自复制运行状态。
        internal void CopyStateTo(CharacterBehaviorRuntimeState target) => stamina.CopyStateTo(target.stamina);
    }
}
