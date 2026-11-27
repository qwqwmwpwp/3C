using System;

namespace ThreeC.CharacterBehavior
{
    // 保存最近已提交的行为许可及对应的模拟代次。
    [Serializable]
    internal sealed class CharacterCommittedBehaviorGrants
    {
        private long guardSequence;
        private bool guardActive;
        private int simulationVersion;

        // 逐帧裁决时记录该时刻的行为许可，持物每次重新获准时增加实例编号。
        internal void Observe(in CharacterBehaviorFrameResult behavior, int version)
        {
            if ((behavior.StartedBehaviors & CharacterBehaviorId.Guard) != CharacterBehaviorId.None) guardSequence++;
            guardActive = (behavior.ActiveBehaviors & CharacterBehaviorId.Guard) != CharacterBehaviorId.None;
            simulationVersion = version;
        }

        // 当前仍请求持物且模拟连续时才对外提供已提交的许可。
        internal long ReadGuardApproval(bool requested, bool simulating, int version) =>
            requested && simulating && guardActive && simulationVersion == version ? guardSequence : 0;

        // 重置角色时撤回当前许可，后续获准仍沿用递增实例编号。
        internal void Reset() => guardActive = false;
    }
}
