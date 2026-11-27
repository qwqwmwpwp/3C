using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/动画模块/收步", fileName = "收步")]
    public sealed class StopModule : AnimationModule
    {
        [InspectorName("收步最短移动距离（米）"), SerializeField, Min(0f), Tooltip("连续主动地面位移至少达到此距离才收步，单位米。")]
        private float minimumStopTravel = 0.09f;
        [SerializeField, InspectorName("步行收步")] private CharacterStopAnimationSettings walkStop;
        [SerializeField, InspectorName("常跑收步")] private CharacterStopAnimationSettings runStop;
        [SerializeField, InspectorName("持续冲刺收步")] private CharacterStopAnimationSettings sprintStop;

        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);

        internal void Apply(CharacterAnimationConfiguration config)
        {
            config.MinimumStopTravel = minimumStopTravel;
            config.WalkStop = walkStop;
            config.RunStop = runStop;
            config.SprintStop = sprintStop;
        }
    }
}
