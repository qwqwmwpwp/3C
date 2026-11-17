using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>Adapts legacy input and one fixed-step behavior result to a CharacterController.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    [AddComponentMenu("3C/Character Behavior Driver")]
    public sealed class CharacterBehaviorDriver : MonoBehaviour
    {
        [Header("Legacy Input")]
        [SerializeField, Tooltip("Input Manager axis mapped to world X. Default: A/D or left/right arrows.")]
        private string horizontalAxis = "Horizontal";
        [SerializeField, Tooltip("Input Manager axis mapped to world Z. Default: S/W or down/up arrows.")]
        private string verticalAxis = "Vertical";
        [SerializeField, Tooltip("A press requests one dash. Holding this key does not request repeated dashes.")]
        private KeyCode dashKey = KeyCode.LeftShift;

        [Header("Movement")]
        [SerializeField, Tooltip("Planar world-space movement only; gravity and rotation are outside this driver.")]
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

            // Every decision uses the input committed at the end of the previous fixed step.
            Vector3 forward = transform.forward;
            Vector2 facing = new Vector2(forward.x, forward.z);

            // Input direction takes priority for a dash. At rest, dash along transform.forward
            // projected onto XZ; the driver never rotates the character automatically.
            var frame = behaviorFlow.Tick(committedInput, facing, Time.fixedDeltaTime, settings);
            Vector2 displacement = frame.Displacement;
            characterController.Move(new Vector3(displacement.x, 0f, displacement.y));

            // Move can disable the driver or controller through collision callbacks.
            if (!CanSimulate())
            {
                ClearInput();
                return;
            }

            // Capture buffered input only after this step completes, for the next step.
            committedInput = inputBlackboard.Consume();
        }

        private bool CanSimulate()
        {
            return isActiveAndEnabled && hasInputFocus && !isPaused && characterController != null
                && characterController.enabled;
        }

        private void ClearInput()
        {
            // Clear held input and queued presses at lifecycle boundaries. The flow is retained:
            // dash progress, cooldown, and resources freeze while this driver cannot simulate.
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
