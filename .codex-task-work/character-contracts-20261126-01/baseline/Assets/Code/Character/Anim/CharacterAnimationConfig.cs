using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>提供角色模块组合、播放库与适用于整套播放的设置。</summary>
    [CreateAssetMenu(menuName = "3C/角色数据/04 动画表现", fileName = "04 动画表现")]
    public sealed class CharacterAnimationConfig : ScriptableObject
    {
        internal const float DefaultTeleportDistance = 2f;

        [SerializeField, InspectorName("动画模块组合"), Tooltip("留空时只播放默认待机，也可以在运行时为单个角色装配模块。")]
        internal AnimationModule Modules;
        [SerializeField, InspectorName("播放库"), Tooltip("播放、地面匹配与收步采样共用此库。更换库或重新烘焙播放索引、遮罩后，重新启用角色行为动画组件。")] internal CharacterMotionDatabase PlaybackLibrary;
        [SerializeField, InspectorName("默认待机状态"), Tooltip("没有模块提供待机状态时使用。留空时采用播放库的下身默认状态。")]
        internal string DefaultIdleState;
        [SerializeField, InspectorName("瞬移重置距离（米）"), Min(0f), Tooltip("单次表现更新移动超过此距离时清空姿态历史。零表示关闭自动检测。")]
        internal float IkTeleportDistance = DefaultTeleportDistance;
        [SerializeField, InspectorName("复用空闲动作混合器"), Tooltip("每个部位和共享接管各保留至多一个空混合器，片段及事件时钟在退出时释放。")]
        internal bool ReuseActionMixers;

        // 使用角色的模块集合与骨骼绑定创建独立的运行时配置。
        internal CharacterAnimationConfiguration Bind(CharacterModelBindings model, CharacterProfileOverrides overrides,
            CharacterLightAttackSettings attack, AnimationModuleAssembly modules)
        {
            modules ??= ResolveModules();
            var config = new CharacterAnimationConfiguration(modules, PlaybackLibrary)
            {
                AttackSettings = attack,
                IdleState = ResolveIdleState(),
                Transition = overrides.Transition.Resolve(0.12f),
                ReuseActionMixers = ReuseActionMixers,
                IkTeleportDistance = IkTeleportDistance
            };
            modules.Apply(config, model, overrides);
            return config;
        }

        // 展开资源声明的模块组合，留空时得到可用的空装配。
        internal AnimationModuleAssembly ResolveModules() =>
            Modules ? AnimationModuleAssembly.Resolve(Modules) : AnimationModuleAssembly.Resolve();

        // 读取显式待机路径，留空时继承双层播放库的下身默认状态。
        private string ResolveIdleState()
        {
            if (!string.IsNullOrWhiteSpace(DefaultIdleState)) return DefaultIdleState;
            var layers = PlaybackLibrary ? PlaybackLibrary.Playback?.Layers : null;
            return layers is { Length: > 0 } ? layers[0]?.DefaultState : null;
        }

        // 读取当前播放库中匹配循环状态的烘焙参考速度。
        internal static float ReferenceSpeed(CharacterMotionDatabase library, string state, float fallback)
        {
            if (!library || library.Playback?.States == null) return fallback;
            return library.FindBakedCycle(state)?.ReferenceSpeed ?? fallback;
        }
    }
}
