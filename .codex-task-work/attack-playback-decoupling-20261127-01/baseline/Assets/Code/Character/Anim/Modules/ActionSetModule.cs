using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/动画模块/行为与动作集", fileName = "行为与动作集")]
    public sealed class ActionSetModule : AnimationModule
    {
        [SerializeField, InspectorName("动作映射")] private CharacterAnimationActionSettings[] actions;
        [SerializeField, InspectorName("行为触发")] private CharacterBehaviorAnimationMapping[] mappings;

        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this, actions, mappings);
    }
}
