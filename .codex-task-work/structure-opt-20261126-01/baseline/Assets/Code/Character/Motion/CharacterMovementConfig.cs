using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/角色数据/03 移动与物理", fileName = "03 移动与物理")]
    public sealed class CharacterMovementConfig : ScriptableObject
    {
        [Header("移动速度与转向")]
        [SerializeField, InspectorName("移动参数")] internal CharacterLocomotionSettings Movement = new CharacterLocomotionSettings();
        [Header("刚体运动与地形")]
        [SerializeField, InspectorName("物理参数")] internal CharacterMotorSettings Motor = new CharacterMotorSettings();
    }
}
