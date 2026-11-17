using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>采集旧版输入系统的输入，在固定步中通过 CharacterController 应用行为流程输出的平面位移。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    [AddComponentMenu("3C/角色行为驱动器")]
    public sealed class CharacterBehaviorDriver : MonoBehaviour
    {
        [Header("旧版输入")]
        [SerializeField, Tooltip("映射到世界 X 轴的 Input Manager 输入轴，默认使用 A/D 或左右方向键。")]
        private string horizontalAxis = "Horizontal";
        [SerializeField, Tooltip("映射到世界 Z 轴的 Input Manager 输入轴，默认使用 S/W 或上下方向键。")]
        private string verticalAxis = "Vertical";
        [SerializeField, Tooltip("按下一次请求一次冲刺，持续按住时仅响应首次按下。")]
        private KeyCode dashKey = KeyCode.LeftShift;

        [Header("移动配置")]
        [SerializeField, Tooltip("配置世界 XZ 平面的移动与冲刺，重力和角色转向由外部系统负责。")]
        private CharacterMovementSettings settings = new CharacterMovementSettings();

        private CharacterController characterController;
        private readonly CharacterInputBlackboard inputBlackboard = new CharacterInputBlackboard();
        private CharacterInputSnapshot committedInput;
        private readonly CharacterBehaviorFlow behaviorFlow = new CharacterBehaviorFlow();
        private bool hasInputFocus = true;
        private bool isPaused;

        private void Awake()
        {
            characterController = GetComponent<CharacterController>();
        }

        private void OnEnable()
        {
            hasInputFocus = Application.isFocused;
            ClearInput();
        }

        private void OnDisable()
        {
            ClearInput();
        }

        private void Update()
        {
            if (!CanSimulate())
            {
                ClearInput();
                return;
            }

            Vector2 direction = new Vector2(
                Input.GetAxisRaw(horizontalAxis),
                Input.GetAxisRaw(verticalAxis));
            inputBlackboard.Write(direction, Input.GetKeyDown(dashKey));
        }

        private void FixedUpdate()
        {
            if (!CanSimulate())
            {
                ClearInput();
                return;
            }

            // 本步所有判定统一使用上一个固定步结束时提交的输入快照。
            Vector3 forward = transform.forward;
            Vector2 facing = new Vector2(forward.x, forward.z);

            // 冲刺优先使用输入方向，静止时使用 transform.forward 在 XZ 平面的投影。
            // 角色转向由外部系统负责。
            var frame = behaviorFlow.Tick(committedInput, facing, Time.fixedDeltaTime, settings);
            Vector2 displacement = frame.Displacement;
            characterController.Move(new Vector3(displacement.x, 0f, displacement.y));

            // Move 触发的碰撞回调可能禁用驱动器或控制器，因此移动后再次检查可执行状态。
            if (!CanSimulate())
            {
                ClearInput();
                return;
            }

            // 本步完成后再消费缓冲输入，提交给下一个固定步。
            committedInput = inputBlackboard.Consume();
        }

        private bool CanSimulate()
        {
            return isActiveAndEnabled && hasInputFocus && !isPaused && characterController != null
                && characterController.enabled;
        }

        private void ClearInput()
        {
            // 在生命周期边界清空持续输入与待消费按键，并保留行为流程状态。
            // 驱动器暂停模拟期间，冲刺进度、冷却和体力均冻结。
            inputBlackboard.Clear();
            committedInput = default;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            hasInputFocus = hasFocus;
            if (!hasFocus)
                ClearInput();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            isPaused = pauseStatus;
            if (pauseStatus)
                ClearInput();
        }

        private void OnValidate()
        {
            if (settings == null)
                settings = new CharacterMovementSettings();
        }
    }
}
