using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在所有角色与外部系统的 LateUpdate 结束后提交本帧预测黑板。</summary>
    internal static class CharacterForecastFrameBatch
    {
        private static readonly CharacterFrameBatch<CharacterBehaviorDriver> batch =
            new CharacterFrameBatch<CharacterBehaviorDriver>("3C.Forecast.Commit", actor => actor.CommitForecastFrame());

        // 新会话不继承前一次运行中尚未交付的角色。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => batch.Reset();

        // 行为和动画完成提交后，统一取得最终的预测输入。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install() => batch.Install(typeof(UnityEngine.PlayerLoop.PreLateUpdate.ScriptRunBehaviourLateUpdate),
            typeof(CharacterForecastFrameBatch));

        // 由驱动器的 LateUpdate 登记本帧需要更新的角色。
        internal static void Queue(CharacterBehaviorDriver actor) => batch.Queue(actor);
    }
}
