using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>共享的动画装配定义。实例进度和求解历史由每个动画宿主持有。</summary>
    public abstract class AnimationModule : ScriptableObject
    {
        internal abstract void Include(AnimationModuleAssembly assembly);
    }
}
