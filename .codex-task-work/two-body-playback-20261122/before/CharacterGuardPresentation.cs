using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>把当前持物许可映射为测试角色专用的上身动作请求。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterBehaviorDriver), typeof(CharacterBehaviorAnimator))]
    [DefaultExecutionOrder(50)]
    [AddComponentMenu("3C/角色持物表现")]
    public sealed class CharacterGuardPresentation : MonoBehaviour
    {
        private const string GripAction = "upper.grip";

        [HideInInspector, SerializeField, Range(0f, 1f), Tooltip("持物参考姿势在上身层的权重。")]
        private float gripLayerWeight = 0.4f;

        private static long nextInstanceId;
        private CharacterBehaviorDriver driver;
        private CharacterBehaviorAnimator animationDriver;
        private long approval;
        private long instance;
        private bool submitted;

        private void Awake()
        {
            driver = GetComponent<CharacterBehaviorDriver>();
            animationDriver = GetComponent<CharacterBehaviorAnimator>();
        }

        private void Update()
        {
            if (animationDriver.HasBehaviorMapping(CharacterBehaviorId.Guard))
            {
                if (approval != 0) WithdrawGrip();
                return;
            }
            long currentApproval = driver.CommittedGuardApproval;
            if (currentApproval != approval)
            {
                WithdrawGrip();
                approval = currentApproval;
                if (approval != 0)
                    instance = ++nextInstanceId;
            }

            if (instance == 0)
                return;

            // 重交同一实例保留播放进度，也允许表现组件单独重启后恢复持续需求。
            float weight = driver.Profile ? driver.Profile.Presentation.GripWeight : gripLayerWeight;
            if (animationDriver.SubmitAction(new CharacterAnimationRequest(GripAction, instance, weight)))
                submitted = true;
        }

        private void OnDisable() => WithdrawGrip();

        private void WithdrawGrip()
        {
            if (submitted && animationDriver != null)
                animationDriver.WithdrawAction(GripAction, instance);

            approval = 0;
            instance = 0;
            submitted = false;
        }
    }
}
