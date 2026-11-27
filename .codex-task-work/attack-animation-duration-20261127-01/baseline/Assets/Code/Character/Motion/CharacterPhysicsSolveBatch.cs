using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在引擎调用三维求解器之前标记已交给求解器的物理步，供碰撞回调中的暂停结算使用。
    internal static class CharacterPhysicsSolveBatch
    {
        private static readonly CharacterFrameBatch<CharacterBehaviorDriver> batch =
            new CharacterFrameBatch<CharacterBehaviorDriver>("3C.Physics.Solve", actor => actor.MarkSolverEntered());

        // 进入新会话时清除尚未进入求解器的角色。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => batch.Reset();

        // 固定脚本与其他早于物理的引擎阶段全部完成后再切换结算资格。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install() => batch.Install(typeof(UnityEngine.PlayerLoop.FixedUpdate.PhysicsFixedUpdate),
            typeof(CharacterPhysicsSolveBatch), before: true);

        // 自定义播放器没有相应阶段时，由真实碰撞回调和后续帧边界提供求解证据。
        internal static void Queue(CharacterBehaviorDriver actor) => batch.Queue(actor, fallback: false);
    }
}
