using System;
using System.Collections.Generic;
using ThreeC.Player;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>普通移动请求的速度档。默认值对应原有跑步速度。</summary>
    public enum CharacterMoveGait
    {
        [InspectorName("常跑")] Run,
        [InspectorName("步行")] Walk,
        [InspectorName("持续冲刺")] Sprint
    }

    /// <summary>只读执行黑板，按裁决时刻生成，每次按下只交付一次；x/y 分别对应世界 X/Z 轴。</summary>
    public readonly struct CharacterInputSnapshot
    {
        public Vector2 Direction { get; }
        public CharacterMoveGait Gait { get; }
        public CharacterBehaviorId Requests { get; }
        public AttackButtonPresses AttackPresses { get; }
        public bool DashPressed => (Requests & CharacterBehaviorId.Dash) != CharacterBehaviorId.None;
        public bool GuardHeld => (Requests & CharacterBehaviorId.Guard) != CharacterBehaviorId.None;

        public CharacterInputSnapshot(Vector2 direction, bool dashPressed)
            : this(direction, dashPressed, false)
        {
        }

        public CharacterInputSnapshot(Vector2 direction, bool dashPressed, bool guardHeld = false,
            CharacterBehaviorId requests = CharacterBehaviorId.None)
            : this(direction, dashPressed, guardHeld, requests, CharacterMoveGait.Run)
        {
        }

        public CharacterInputSnapshot(Vector2 direction, bool dashPressed, bool guardHeld,
            CharacterBehaviorId requests, CharacterMoveGait gait)
            : this(direction, dashPressed, guardHeld, requests, gait, default)
        {
        }

        public CharacterInputSnapshot(Vector2 direction, bool dashPressed, bool guardHeld,
            CharacterBehaviorId requests, CharacterMoveGait gait, AttackButtonPresses attackPresses)
        {
            Direction = InputMath.ClampDirection(direction);
            Gait = gait;
            AttackPresses = attackPresses.First == AttackButton.None && (requests & CharacterBehaviorId.Attack) != 0
                ? attackPresses.With(AttackButton.Light) : attackPresses;
            Requests = requests | (dashPressed ? CharacterBehaviorId.Dash : CharacterBehaviorId.None)
                | (guardHeld ? CharacterBehaviorId.Guard : CharacterBehaviorId.None)
                | (AttackPresses.First != AttackButton.None ? CharacterBehaviorId.Attack : CharacterBehaviorId.None);
        }
    }

    // 保存同一设备时钟上的输入边沿；同一时刻沿用回调到达顺序。
    internal readonly struct CharacterInputPress
    {
        internal readonly double DeviceTime;
        internal readonly AttackButton Attack;
        internal bool IsDash => Attack == AttackButton.None;

        // 用空攻击键表示短突进。
        internal CharacterInputPress(double deviceTime, AttackButton attack)
        {
            DeviceTime = deviceTime;
            Attack = attack;
        }
    }

    /// <summary>分别保留持续意图和按设备时刻排列的待处理边沿。</summary>
    [Serializable]
    public sealed class CharacterInputBlackboard
    {
        private Vector2 _direction;
        private CharacterMoveGait _gait;
        private bool _dashPressed;
        private bool _guardHeld;
        private ulong pendingBehaviors; // 待消费的行为位掩码，以整数形式供 Unity 热重载序列化。
        private AttackButtonPresses _attackPresses;
        private readonly List<CharacterInputPress> presses = new List<CharacterInputPress>();
        private bool _wasReceivingPlayerInput;
        private ulong _lastDashPressId;
        private bool _suppressGuardUntilRelease;

        internal IReadOnlyList<CharacterInputPress> Presses => presses;
        internal bool WasReceivingPlayerInput => _wasReceivingPlayerInput;
        internal bool GuardRequested => _guardHeld;

        internal bool Collect(PlayerInputSample input, (Vector3 forward, Vector3 right)? basis, CharacterMoveGait defaultGait, bool allowGuard)
        {
            var direction = input.Move;
            if (basis.HasValue)
            {
                var (forward, right) = basis.Value;
                direction = new Vector2(right.x, right.z) * direction.x + new Vector2(forward.x, forward.z) * direction.y;
            }
            bool dashPressed = input.DashPressId != 0 && input.DashPressId != _lastDashPressId;
            _lastDashPressId = input.DashPressId;
            CharacterMoveGait gait = defaultGait;
            if (input.WalkHeld)
                gait = CharacterMoveGait.Walk;
            else if (input.SprintHeld)
                gait = CharacterMoveGait.Sprint;

            if (!input.GuardHeld)
                _suppressGuardUntilRelease = false;
            Write(direction, false, gait, allowGuard && input.GuardHeld && !_suppressGuardUntilRelease);
            if (dashPressed) AddPress(new CharacterInputPress(Time.realtimeSinceStartupAsDouble, AttackButton.None));
            return dashPressed;
        }

        // 累计自定义行为的单次请求，本地持续输入采集不覆盖已提交的边沿。
        public bool PressBehavior(CharacterBehaviorId id)
        {
            ulong bits = (ulong)id;
            if (bits == 0 || (bits & (bits - 1)) != 0 || id == CharacterBehaviorId.Move || (Peek().Requests & id) != 0) return false;
            pendingBehaviors |= bits;
            return true;
        }

        public void PressAttack(AttackButton button) => _attackPresses = _attackPresses.With(button);

        /// <summary>写入最新方向并累计按下事件，多次写入会合并为一次待消费的冲刺请求。</summary>
        public void Write(Vector2 direction, bool dashPressed)
        {
            Write(direction, dashPressed, CharacterMoveGait.Run);
        }

        public void Write(Vector2 direction, bool dashPressed, CharacterMoveGait gait)
        {
            Write(direction, dashPressed, gait, false);
        }

        internal void Write(Vector2 direction, bool dashPressed, CharacterMoveGait gait, bool guardHeld)
        {
            _direction = direction;
            _gait = gait;
            _dashPressed |= dashPressed;
            _guardHeld = guardHeld;
        }

        // 把持续输入与尚未消费的脚本请求合成一份只读快照。
        internal CharacterInputSnapshot Peek() =>
            new CharacterInputSnapshot(_direction, _dashPressed, _guardHeld, (CharacterBehaviorId)pendingBehaviors, _gait, _attackPresses);

        /// <summary>保留方向和持续请求，只清除已转交的按下事件。供显式写入快照的调用者消费。</summary>
        public CharacterInputSnapshot Consume()
        {
            var snapshot = Peek();
            pendingBehaviors = default;
            _attackPresses = default;
            _dashPressed = false;
            return snapshot;
        }

        // 冲刺事件与轮询共享编号，保留同帧多次按下。
        internal bool PressDash(ulong id, double deviceTime)
        {
            if (id == 0 || id == _lastDashPressId) return false;
            _lastDashPressId = id;
            AddPress(new CharacterInputPress(deviceTime, AttackButton.None));
            return true;
        }

        // 设备批次可能晚到，按时间稳定插入；相同时刻保留设备上报顺序。
        internal void AddPress(CharacterInputPress press)
        {
            int i = presses.Count;
            while (i > 0 && presses[i - 1].DeviceTime > press.DeviceTime) i--;
            presses.Insert(i, press);
        }

        // 本渲染帧已完成裁决，移除所有已交付边沿。
        internal void ClearPresses() => presses.Clear();

        internal void ClearForPlayerInput(bool receivePlayerInput, ulong latestDashPressId)
        {
            Clear();
            _wasReceivingPlayerInput = receivePlayerInput;
            _lastDashPressId = latestDashPressId; // 恢复或接手玩家控制时跳过此前记录的按下。
            _suppressGuardUntilRelease = true; // 恢复后先松键，再接受持物请求。
        }

        /// <summary>清空全部输入，用于停用、失焦或暂停等生命周期边界。</summary>
        public void Clear()
        {
            pendingBehaviors = default;
            _direction = Vector2.zero;
            _gait = CharacterMoveGait.Run;
            _dashPressed = false;
            _guardHeld = false;
            _attackPresses = default;
            presses.Clear();
        }
    }

}
