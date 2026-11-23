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

    /// <summary>只读执行黑板，在固定步末尾复制生成，下一步所有行为共享；x/y 分别对应世界 X/Z 轴。</summary>
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

    /// <summary>保存方向、移动档和持续请求，在固定步末尾转交短突进及有序攻击按键。</summary>
    public sealed class CharacterInputBlackboard
    {
        private Vector2 _direction;
        private CharacterMoveGait _gait;
        private bool _dashPressed;
        private bool _guardHeld;
        private AttackButtonPresses _attackPresses;

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

        internal CharacterInputSnapshot Peek() =>
            new CharacterInputSnapshot(_direction, _dashPressed, _guardHeld, CharacterBehaviorId.None, _gait, _attackPresses);

        /// <summary>保留方向和持续请求，只清除已转交的按下事件。每个固定步末尾调用一次。</summary>
        public CharacterInputSnapshot Consume()
        {
            var snapshot = Peek();
            _attackPresses = default;
            _dashPressed = false;
            return snapshot;
        }

        /// <summary>清空全部输入，用于停用、失焦或暂停等生命周期边界。</summary>
        public void Clear()
        {
            _direction = Vector2.zero;
            _gait = CharacterMoveGait.Run;
            _dashPressed = false;
            _guardHeld = false;
            _attackPresses = default;
        }
    }

}
