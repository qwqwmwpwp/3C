using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 为每个角色创建独立的地面步态、选姿与匹配历史。
    [CreateAssetMenu(menuName = "人形动画/基础姿态/地面移动")]
    public sealed class GroundPoseStrategy : CharacterBasePoseStrategy, ICharacterPoseConfiguration
    {
        // 仅在角色装配地面策略时启用水平步态判定、收步与地面搜索库。
        void ICharacterPoseConfiguration.Configure(CharacterAnimationConfiguration config)
        {
            var modules = config.Modules;
            config.Decisions = modules.BindDecisions();
            if (modules.Gait) modules.Gait.Apply(config);
            if (modules.StartTurn) modules.StartTurn.Apply(config);
            if (modules.Stop) modules.Stop.Apply(config);
            if (modules.Match) modules.Match.Apply(config);
        }

        // 创建只属于当前宿主的地面运行实例。
        public override ICharacterBasePoseStrategy CreateInstance(CharacterBasePoseSetup setup) => new GroundLocomotionStrategy();
    }
}
