using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 将可复用的语义动作与部位仲裁规则加入动画装配。
    [CreateAssetMenu(menuName = "动画模块/动作集", fileName = "动作集")]
    public sealed class ActionSetModule : AnimationModule
    {
        [SerializeField, InspectorName("动作映射")] private CharacterAnimationActionSettings[] actions;


        // 按配置顺序注册动作资源。
        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this, actions);
    }
}
