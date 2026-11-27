using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在同一角色内合并重生与连续性请求，行为回调退出后才执行挂起的生命周期变更。
    internal sealed class CharacterSimulationContinuity
    {
        // 重生包含同批次的同步工作，合并标记时优先执行重生。
        private enum Change { None, Synchronize, Reset }
        private Change pending, applying;
        private CharacterBehaviorFlow flow;
        private Action reset, synchronize;
        private int callbackDepth;
        internal int Revision { get; private set; }
        internal bool Pending => pending != Change.None;
        internal bool InCallback => callbackDepth > 0 || flow?.IsDispatching == true;

        // 驱动器初始化完成后接入可独立执行的重生和连续性切换。
        internal void Bind(CharacterBehaviorFlow behavior, Action resetState, Action sync, CharacterMovementSettings movement)
        {
            flow = behavior;
            reset = resetState;
            synchronize = sync;
            applying = Change.Reset;
            try { flow.Reset(movement); }
            finally { applying = Change.None; }
            Flush();
        }

        // 时间线采样与马达读取冻结运动时同样可能进入扩展代码，直到整个读取返回才能处理挂起操作。
        internal Scope Protect() => new Scope(this);

        // 用值类型作用域保证异常退出时也解除输入和生命周期保护。
        internal readonly struct Scope : IDisposable
        {
            private readonly CharacterSimulationContinuity owner;

            // 记录本层回调尚未交还执行权。
            internal Scope(CharacterSimulationContinuity owner)
            {
                this.owner = owner;
                owner.callbackDepth++;
            }

            // 最外层回调离开时执行期间合并的生命周期请求。
            public void Dispose()
            {
                if (owner == null) return;
                owner.callbackDepth--;
                owner.Flush();
            }
        }

        // 把新请求合并为一次重生或同步；重生涵盖同批次的输入、刚体和预测边界。
        internal void Request(bool respawn)
        {
            var request = respawn ? Change.Reset : Change.Synchronize;
            if (applying == Change.Reset && (respawn || InCallback)) return;
            if (applying == Change.Synchronize && !respawn && InCallback) return;
            if (pending < request)
            {
                pending = request;
                Revision++;
            }
            if (InCallback && applying != Change.Reset) flow?.CancelCurrentDispatch();
            Flush();
        }

        // 每次安全出口各执行至多一次重生与同步，监听者持续发出的新一轮请求保留给下个出口。
        internal void Flush()
        {
            if (flow == null || InCallback || applying != Change.None) return;
            bool resetDone = false, syncDone = false;
            while (Pending)
            {
                var next = pending;
                if (next == Change.Reset ? resetDone : syncDone) return;
                pending = Change.None;
                applying = next;
                flow.ClearDispatchCancellation();
                try
                {
                    if (next == Change.Reset) { resetDone = true; reset(); }
                    else { syncDone = true; synchronize(); }
                }
                finally { applying = Change.None; }
            }
        }

        // 在连续性边界冻结仍活动的运动；回调申请重生时不写入旧代命令或输入。
        internal static void EndInput(CharacterBlackboard board, CharacterBehaviorTimeline timeline,
            CharacterBehaviorFlow flow, CharacterRigidbodyMotor motor, CharacterMovementSettings movement,
            bool ownsBody, bool receivesInput)
        {
            if (ownsBody && flow != null && motor.Available)
            {
                if (flow.Alignment.Plan != null)
                    board.Rigidbody.CancelAlignment(flow.Alignment.Stop(CharacterActionAlignmentStatus.Cancelled));
                var state = motor.ReadStepState();
                var active = flow.Tick(default, state.Facing, 0f, movement, state.GroundVelocity);
                if (flow.DispatchCancelled) return;
                var command = flow.CaptureMotion(timeline.Time, active, default, state.Facing, state.GroundVelocity, movement);
                if (flow.DispatchCancelled) return;
                board.Rigidbody.Write(command);
            }
            flow?.EndInputContinuity();
            if (flow?.DispatchCancelled == true) return;
            timeline.EndContinuity(Time.realtimeSinceStartupAsDouble);
            CharacterPlayerInputAdapter.EndContinuity(board.Input, receivesInput);
            board.Simulation.AdvanceContinuity();
        }
    }
}
