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
        private GameInput gameInput;
        private InputActionAsset combatInput;
        private InputAction lightAttack;
        private InputAction heavyAttack;
        private InputAction skill;
        private ulong latestDashPressId;

        public static ulong LatestDashPressId => instance != null ? instance.latestDashPressId : 0;
        internal static event System.Action<AttackButton> AttackPressed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            instance = null;
            nextDashPressId = 0;
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
                var combat = Instantiate(asset);
                var light = combat.FindAction("Combat/LightAttack");
                var heavy = combat.FindAction("Combat/HeavyAttack");
                var special = combat.FindAction("Combat/Skill");
                if (light == null || heavy == null || special == null)
                {
                    string missing = light == null ? "LightAttack" : heavy == null ? "HeavyAttack" : "Skill";
                    Debug.LogError($"玩家战斗输入缺少动作 Combat/{missing}。", this);
                    Destroy(combat);
                    return;
                }
                combatInput = combat;
                lightAttack = light;
                heavyAttack = heavy;
                skill = special;
            }
            lightAttack.performed += RecordAttackPress;
            lightAttack.Enable();
            heavyAttack.performed += RecordAttackPress;
            heavyAttack.Enable();
            skill.performed += RecordAttackPress;
            skill.Enable();
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
                skill.performed -= RecordAttackPress;
                skill.Disable();
            }
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
            AttackPressed?.Invoke(context.action == skill ? AttackButton.Skill :
                context.action == lightAttack ? AttackButton.Light : AttackButton.Heavy);
        }

        /// <summary>读取当前动作值。短突进编号可重复读取，每个使用方自行记录已处理的编号。</summary>
        public static PlayerInputSample Read()
        {
            if (instance == null || !instance.isActiveAndEnabled)
                return default;

            var player = instance.gameInput.Player;
            return new PlayerInputSample(player.Move.ReadValue<Vector2>(), player.Walk.IsPressed(),
                player.Sprint.IsPressed(), player.Guard.IsPressed(), instance.latestDashPressId);
        }
    }

    public enum AttackButton
    {
        [InspectorName("无")] None,
        [InspectorName("轻击")] Light,
        [InspectorName("重击")] Heavy,
        [InspectorName("技能（E）")] Skill
    }

    /// <summary>按到达顺序保存按下边沿。普通单击不分配数组，快照可以安全交给预测线程。</summary>
    public readonly struct AttackButtonPresses
    {
        public AttackButton First { get; }
        private readonly AttackButton[] following;
        public int Count => First == AttackButton.None ? 0 : 1 + (following?.Length ?? 0);
        public AttackButton Second => Count > 1 ? following[0] : AttackButton.None;
        public AttackButton this[int index] => index == 0 ? First : following[index - 1];

        private AttackButtonPresses(AttackButton first, AttackButton[] following)
        {
            First = first;
            this.following = following;
        }

        public AttackButtonPresses With(AttackButton button)
        {
            if (button == AttackButton.None) return this;
            if (First == AttackButton.None) return new AttackButtonPresses(button, null);
            var next = new AttackButton[Count];
            following?.CopyTo(next, 0);
            next[next.Length - 1] = button;
            return new AttackButtonPresses(First, next);
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

        internal PlayerInputSample(Vector2 move, bool walkHeld, bool sprintHeld, bool guardHeld, ulong dashPressId)
        {
            Move = move;
            WalkHeld = walkHeld;
            SprintHeld = sprintHeld;
            GuardHeld = guardHeld;
            DashPressId = dashPressId;
        }
    }
}
