using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 按业务提交顺序翻译每条行为边沿，渲染帧只补交持续许可的后续姿势。
    internal sealed class CharacterBehaviorAnimationBindings
    {
        private readonly List<CharacterBehaviorAnimationBinding> bindings = new List<CharacterBehaviorAnimationBinding>();
        internal readonly CharacterBehaviorAnimator Host;
        internal int StateVersion { get; set; }
        private long lastFrame;
        private int? simulationVersion;
#if UNITY_EDITOR
        internal CharacterActionTrace Trace { get; set; }
#endif

        // 每个角色独立持有许可和单次实例，共享只读的映射定义。
        internal CharacterBehaviorAnimationBindings(CharacterBehaviorAnimator host, CharacterBehaviorAnimationProfile profile)
        {
            Host = host;
            if (!profile) return;
            foreach (var row in profile.Mappings)
                if (row?.HasValidTrigger == true) bindings.Add(new CharacterBehaviorAnimationBinding(row.Snapshot(), this));
        }

        // 在每次真实裁决时直接处理开始、结束和接招，先结束旧实例再申请下一次。
        internal void Observe(in CharacterBehaviorObservation frame, bool seed = false)
        {
            if (frame.FrameNumber == 0 || simulationVersion == frame.SimulationVersion && frame.FrameNumber <= lastFrame) return;
            simulationVersion = frame.SimulationVersion;
            lastFrame = frame.FrameNumber;
            foreach (var binding in bindings) binding.Observe(frame, seed);
        }

        // 从进入动作的真实终态决定是否提交持盾姿势。
        internal void Tick(long guardGrant)
        {
            foreach (var binding in bindings) binding.Tick(guardGrant);
#if UNITY_EDITOR
            Trace?.ReadPlaybacks();
#endif
        }

        // 判断当前物理步是否应保留已显示的进入步态。
        internal bool PreservesEntryGait(in CharacterControllerFrameResult frame)
        {
            foreach (var binding in bindings)
                if (binding.Rule.PreservesEntryGait(frame, binding.Rule.Handoff)) return true;
            return false;
        }

        // 通过宿主提交本次请求，编辑器归属单独附着在本次句柄上。
        internal CharacterActionPlayback Play(in CharacterOneShotRequest request, double? at,
            in CharacterBehaviorObservation frame, CharacterBehaviorAnimationMapping rule, CharacterPlaybackStartSource source)
        {
            var receipt = Host.PlayOnce(request, at);
#if UNITY_EDITOR
            if (receipt != null) Trace?.Track(receipt, StateVersion, frame, rule.BehaviorId, source);
#endif
            return receipt;
        }

        // 中断本代全部许可并允许新代次从同一帧号开始。
        internal void Clear(double at)
        {
            foreach (var binding in bindings) binding.Clear(at);
            lastFrame = 0;
            simulationVersion = null;
        }
    }

    // 游戏调试记录区分真实许可的进入、重装配续播和仲裁恢复。
    internal enum CharacterPlaybackStartSource { BehaviorGrant, Rebind, ArbitrationRecovery }
}
