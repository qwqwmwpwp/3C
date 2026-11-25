using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 标识造成动作、足部和渲染姿态历史失效的真实运动边界。
    public enum CharacterMotionBoundaryKind { Teleport, Knockback, Takeover, Recovery }

    // 选择瞬移或交还控制时的世界线速度；指定线速度时同时清除自旋。
    public enum CharacterVelocityPolicy { Clear, Preserve, Set }

    // 每次接管签发独立凭据，实例引用区分不同角色与同一角色的多轮接管。
    public sealed class CharacterExternalControlToken
    {
        public int StateVersion { get; }

        // 将凭据绑定到签发时的角色重生代次。
        internal CharacterExternalControlToken(int version) => StateVersion = version;
    }

    // 冻结本次交接的参数，使旧物理步回调结束后才一次应用已验证的请求。
    internal struct CharacterMotionBoundary
    {
        internal CharacterMotionBoundaryKind Kind;
        internal CharacterVelocityPolicy VelocityPolicy;
        internal CharacterExternalControlToken Token;
        internal Vector3 Position, Velocity;
        internal Quaternion Rotation;
        internal float Duration, Braking;

        // 拒绝非有限坐标、无效旋转及未知速度策略，验证期间不改变模拟状态。
        internal readonly bool Valid()
        {
            if (VelocityPolicy < CharacterVelocityPolicy.Clear || VelocityPolicy > CharacterVelocityPolicy.Set) return false;
            if ((VelocityPolicy == CharacterVelocityPolicy.Set || Kind == CharacterMotionBoundaryKind.Knockback) &&
                !(Velocity.sqrMagnitude < float.PositiveInfinity)) return false;
            if (Kind == CharacterMotionBoundaryKind.Knockback)
                return Duration > 0f && Duration < float.PositiveInfinity && Braking >= 0f && Braking < float.PositiveInfinity;
            if (Kind != CharacterMotionBoundaryKind.Teleport) return true;
            float length = Quaternion.Dot(Rotation, Rotation);
            return Position.sqrMagnitude < float.PositiveInfinity && length > 0.000001f && length < float.PositiveInfinity;
        }
    }

    // 管理角色马达的唯一租约，并在物理归属改变时清除旧行为和未提交运动。
    internal sealed class CharacterMotionHandoff
    {
        private CharacterExternalControlToken holder;
        internal bool HasHolder => holder != null;
        internal event Action<CharacterMotionBoundaryKind, int> Discontinuity;

        // 检查交接通知后该凭据是否仍为当前持有者。
        internal bool Owns(CharacterExternalControlToken token) => holder == token;

        // 验证接管冲突和凭据所属代次，错误请求不结算或改写当前刚体。
        internal bool CanApply(in CharacterMotionBoundary request, int version) =>
            request.Kind == CharacterMotionBoundaryKind.Recovery
                ? request.Token != null && request.Token == holder && holder.StateVersion == version
                : !HasHolder;

        // 重生使所有旧凭据失效并返回原控制权是否曾经交由外部。
        internal bool Revoke()
        {
            bool held = HasHolder;
            holder = null;
            return held;
        }

        // 在已结算或撤回待求解步后切换唯一写入者，再处理相应的位姿与速度。
        internal void ApplyBody(in CharacterMotionBoundary request, CharacterRigidbodyMotor motor,
            ref CharacterMotorSuspension suspension, bool characterRunning)
        {
            switch (request.Kind)
            {
                case CharacterMotionBoundaryKind.Takeover:
                    motor.SetSuspended(false, ref suspension);
                    holder = request.Token;
                    break;
                case CharacterMotionBoundaryKind.Recovery:
                    holder = null;
                    motor.ResumeOwnership(request.VelocityPolicy, request.Velocity, ref suspension, characterRunning);
                    break;
                case CharacterMotionBoundaryKind.Teleport:
                    motor.Teleport(request.Position, request.Rotation.normalized);
                    motor.SetBoundaryVelocity(request.VelocityPolicy, request.Velocity, ref suspension);
                    break;
                case CharacterMotionBoundaryKind.Knockback:
                    motor.SetBoundaryVelocity(CharacterVelocityPolicy.Set, request.Velocity, ref suspension);
                    break;
            }
            if (!HasHolder) motor.SetSuspended(!characterRunning, ref suspension);
            motor.ForgetMotionHistory(clearContacts: request.Kind != CharacterMotionBoundaryKind.Knockback);
        }

        // 内置消费者先清理旧动作，随后才允许公开预测及运动观察者重入下一轮交接。
        internal void Publish(CharacterMotionBoundaryKind kind, int previous, int current, Behaviour owner,
            CharacterSimulationBlackboard simulation, CharacterMovementForecast output, CharacterForecastRevision change,
            Action<CharacterMotionBoundaryKind, int> internalListeners)
        {
            Deliver(internalListeners, kind, previous, current, owner, simulation);
            if (owner == null || current != simulation.LifecycleVersion) return;
            output.Notify(change);
            if (owner != null && current == simulation.LifecycleVersion)
                Deliver(Discontinuity, kind, previous, current, owner, simulation);
        }

        // 单个监听者失败不影响其余监听者，内层重生或交接生效时停止旧代通知。
        private static void Deliver(Action<CharacterMotionBoundaryKind, int> listeners, CharacterMotionBoundaryKind kind,
            int previous, int current, Behaviour owner, CharacterSimulationBlackboard simulation)
        {
            if (listeners == null) return;
            foreach (Action<CharacterMotionBoundaryKind, int> listener in listeners.GetInvocationList())
            {
                try { listener(kind, previous); }
                catch (Exception error) { Debug.LogException(error, owner); }
                if (owner == null || current != simulation.LifecycleVersion) return;
            }
        }

        // 外部边界保留资源与角色时钟，撤销前一代输入、对齐、命令和持续许可。
        internal static void Interrupt(in CharacterMotionBoundary request, CharacterBlackboard board,
            CharacterBehaviorTimeline timeline, CharacterBehaviorFlow flow, CharacterMovementSettings movement,
            CharacterCommittedBehaviorGrants grants, CharacterActionAlignmentBinding binding, bool receivesInput)
        {
            timeline.AdvanceExternal(flow, movement, board.Rigidbody.SubmittedThrough);
            if (flow.DispatchCancelled) return;
            var reason = request.Kind == CharacterMotionBoundaryKind.Teleport ? CharacterBehaviorEndReason.Teleport :
                request.Kind == CharacterMotionBoundaryKind.Knockback ? CharacterBehaviorEndReason.Knockback : CharacterBehaviorEndReason.ExternalControl;
            var result = flow.InterruptAll(reason, movement);
            if (flow.DispatchCancelled) return;
            binding.Set(null, default, 0f);
            flow.Alignment = default;
            CharacterPlayerInputAdapter.EndContinuity(board.Input, receivesInput);
            board.Simulation.AdvanceContinuity();
            int version = board.Simulation.LifecycleVersion;
            double deviceNow = Time.realtimeSinceStartupAsDouble;
            timeline.EndContinuity(deviceNow);
            timeline.PublishExternal(result, version, deviceNow);
            grants.Observe(result, version);
            board.Rigidbody.ClearCommands();
            if (request.Kind == CharacterMotionBoundaryKind.Knockback)
                board.Rigidbody.BeginKnockback(board.Rigidbody.SubmittedThrough, request.Duration, request.Braking);
        }
    }
}
