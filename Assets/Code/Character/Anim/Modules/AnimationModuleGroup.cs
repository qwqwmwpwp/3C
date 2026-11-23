using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/动画模块/组合", fileName = "动画组合")]
    public sealed class AnimationModuleGroup : AnimationModule
    {
        [SerializeField, InspectorName("模块或子组合")] private AnimationModule[] modules = Array.Empty<AnimationModule>();

        internal AnimationModule[] Children => modules;

        internal override void Include(AnimationModuleAssembly assembly)
        {
            if (modules == null) return;
            foreach (var module in modules) assembly.Visit(module);
        }
    }
}
