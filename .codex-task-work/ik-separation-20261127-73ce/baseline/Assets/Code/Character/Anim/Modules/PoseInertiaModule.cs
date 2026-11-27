using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/动画模块/姿势惯性", fileName = "姿势惯性")]
    public sealed class PoseInertiaModule : AnimationModule
    {
        [SerializeField, InspectorName("惯性调校")] internal CharacterInertiaTuning Tuning = new CharacterInertiaTuning();

        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);
    }
}
