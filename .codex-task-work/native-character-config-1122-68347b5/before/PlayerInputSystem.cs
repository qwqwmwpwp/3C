using UnityEngine;
using UnityEngine.InputSystem;

namespace ThreeC.Player
{
    /// <summary>在首个场景加载前创建，跨场景接收本地玩家输入。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PlayerInputSystem : MonoBehaviour
    {
        private static PlayerInputSystem instance;
        private static ulong nextDashPressId;
        private static ulong nextAttackPressId;
        private GameInput gameInput;
        private InputActionAsset combatInput;
        private InputAction lightAttack;
        private InputAction heavyAttack;
        private ulong latestDashPressId;
        private ulong latestAttackPressId;

        public static ulong LatestDashPressId => instance != null ? instance.latestDashPressId : 0;
        public static ulong LatestAttackPressId => instance != null ? instance.latestAttackPressId : 0;
        internal static event System.Action<AttackButton> AttackPressed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            instance = null;
            nextDashPressId = 0;
            nextAttackPressId = 0;
            AttackPressed = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CreateInstance()
        {
            if (instance == null)
                new GameObject("玩家输入系统").AddComponent<PlayerInputSystem>();
        }

        private void OnEnable()
        {
            if (instance != null && instance != this)
            {
                Destroy(this);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);
            gameInput ??= new GameInput();
            gameInput.Player.Dash.performed += RecordDashPress;
            gameInput.Player.Enable();
            if (!combatInput)
            {
                var asset = Resources.Load<InputActionAsset>("PlayerCombat");
                if (!asset)
                {
                    Debug.LogError("缺少玩家战斗输入资源 PlayerCombat。", this);
                    return;
                }
                combatInput = Instantiate(asset);
                lightAttack = combatInput.FindAction("Combat/LightAttack", true);
                heavyAttack = combatInput.FindAction("Combat/HeavyAttack", true);
            }
            lightAttack.performed += RecordAttackPress;
            lightAttack.Enable();
            heavyAttack.performed += RecordAttackPress;
            heavyAttack.Enable();
        }

        private void OnDisable()
        {
            if (gameInput == null)
                return;

            gameInput.Player.Dash.performed -= RecordDashPress;
            gameInput.Player.Disable();
            latestDashPressId = 0;
            if (lightAttack != null)
            {
                lightAttack.performed -= RecordAttackPress;
                lightAttack.Disable();
                heavyAttack.performed -= RecordAttackPress;
                heavyAttack.Disable();
            }
            latestAttackPressId = 0;
        }

        private void OnDestroy()
        {
            if (instance == this)
                instance = null;
            gameInput?.Dispose();
            if (combatInput) Destroy(combatInput);
        }

        private void RecordDashPress(InputAction.CallbackContext context) => latestDashPressId = ++nextDashPressId;
        private void RecordAttackPress(InputAction.CallbackContext context)
        {
            bool light = context.action == lightAttack;
            if (light) latestAttackPressId = ++nextAttackPressId; // 保留原有轻击采样接口。
            AttackPressed?.Invoke(light ? AttackButton.Light : AttackButton.Heavy);
        }

        /// <summary>读取当前动作值。短突进编号可重复读取，每个使用方自行记录已处理的编号。</summary>
        public static PlayerInputSample Read()
        {
            if (instance == null || !instance.isActiveAndEnabled)
                return default;

            var player = instance.gameInput.Player;
            return new PlayerInputSample(player.Move.ReadValue<Vector2>(), player.Walk.IsPressed(),
                player.Sprint.IsPressed(), player.Guard.IsPressed(), instance.latestDashPressId, instance.latestAttackPressId);
        }
    }

    public enum AttackButton { None, Light, Heavy }

    /// <summary>保存本固定步每种按键的首次按下顺序，一次裁决最多使用一个有效输入。</summary>
    public readonly struct AttackButtonPresses
    {
        public AttackButton First { get; }
        public AttackButton Second { get; }

        private AttackButtonPresses(AttackButton first, AttackButton second)
        {
            First = first;
            Second = second;
        }

        public AttackButtonPresses With(AttackButton button)
        {
            if (button == AttackButton.None || button == First || Second != AttackButton.None) return this;
            return First == AttackButton.None ? new AttackButtonPresses(button, AttackButton.None)
                : new AttackButtonPresses(First, button);
        }
    }

    /// <summary>玩家的原始操作，移动保留摇杆幅值，方向尚未按相机转换。</summary>
    public readonly struct PlayerInputSample
    {
        public Vector2 Move { get; }
        public bool WalkHeld { get; }
        public bool SprintHeld { get; }
        public bool GuardHeld { get; }
        public ulong DashPressId { get; } // 最近一次短突进按下的编号，零表示没有请求。
        public ulong AttackPressId { get; }

        internal PlayerInputSample(Vector2 move, bool walkHeld, bool sprintHeld, bool guardHeld, ulong dashPressId,
            ulong attackPressId = 0)
        {
            Move = move;
            WalkHeld = walkHeld;
            SprintHeld = sprintHeld;
            GuardHeld = guardHeld;
            DashPressId = dashPressId;
            AttackPressId = attackPressId;
        }
    }
}
