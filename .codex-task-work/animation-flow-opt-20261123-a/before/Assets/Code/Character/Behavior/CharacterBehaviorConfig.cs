using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/角色数据/01 角色行为", fileName = "01 角色行为")]
    public sealed class CharacterBehaviorConfig : ScriptableObject
    {
        [Header("短突进 · 按下时请求")]
        [SerializeField, InspectorName("短突进参数")] internal CharacterDashSettings Dash = new CharacterDashSettings();

        [Header("攻击 · 输入匹配起手招式时请求")]
        [SerializeField, InspectorName("攻击参数")] internal CharacterLightAttackSettings Attack = new CharacterLightAttackSettings();

        [Header("持物 / 举盾 · 按住维持，松开结束")]
        [SerializeField, InspectorName("持物打断规则")] internal CharacterGuardSettings Guard = new CharacterGuardSettings();

        [Header("基础移动 · 按方向输入移动")]
        [SerializeField, InspectorName("默认步态"), Tooltip("未按步行或持续冲刺键时使用的步态；各档速度由移动与物理资产提供。")]
        internal CharacterMoveGait DefaultGait = CharacterMoveGait.Run;
    }

    [Serializable]
    internal sealed class CharacterGuardSettings
    {
        [InspectorName("允许短突进打断持物")] public bool DashInterrupts = true;
        [InspectorName("允许攻击打断持物")] public bool AttackInterrupts = true;
    }
}
