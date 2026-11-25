using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/动画模块/肢体 IK", fileName = "肢体 IK")]
    public sealed class LimbIkModule : AnimationModule
    {
        [SerializeField, InspectorName("手脚通道（按求解顺序）")] private CharacterIkTuning[] channels;

        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(channels);
    }
}
