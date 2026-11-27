using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 为指定角色替换运动意图、收步和匹配共用的动画判定程序。
    [CreateAssetMenu(menuName = "3C/动画模块/判定程序", fileName = "判定程序")]
    public sealed class AnimationDecisionModule : AnimationModule
    {
        [SerializeField, InspectorName("程序"), Tooltip("可复制 Resources/ThreeC/DefaultAnimationDecisions 后编辑；留空使用默认程序。")]
        internal AnimationDecisionProgram Program;

        // 在角色的模块组合中安装唯一的判定程序提供者。
        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);
    }
}
