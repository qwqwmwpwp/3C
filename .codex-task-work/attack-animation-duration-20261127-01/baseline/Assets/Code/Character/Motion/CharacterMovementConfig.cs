using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 配置行为裁决与刚体物理步持续读取的运动参数。
    [CreateAssetMenu(menuName = "3C/角色数据/03 移动与物理", fileName = "03 移动与物理")]
    public sealed class CharacterMovementConfig : ScriptableObject
    {
        [Header("移动速度与转向")]
        [SerializeField, InspectorName("移动参数"), Tooltip("各档速度在下一次行为裁决生效，普通转速由下一次运动计算读取。")]
        internal CharacterLocomotionSettings Movement = new CharacterLocomotionSettings();
        [Header("刚体运动与地形")]
        [SerializeField, InspectorName("物理参数"), Tooltip("加减速、转向响应与地形参数在下一物理步生效，并使依赖旧数值的预测失效。")]
        internal CharacterMotorSettings Motor = new CharacterMotorSettings();
    }
}
