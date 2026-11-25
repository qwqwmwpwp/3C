using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存一个角色从本次采集到最终提交之间的输入、试选结果和未交接固定步。
    internal struct CharacterPresentationWork
    {
        internal CharacterPresentationInput Input { get; private set; }
        internal CharacterPoseCandidate Candidate;
        internal CharacterPlaybackObservation Observed;
        internal bool Pending { get; private set; }
        internal bool AwaitingGaitForecast { get; private set; }
        internal bool NeedsSelection { get; private set; }
        internal int CoveredSteps { get; private set; }
        internal int DeferredSteps { get; private set; }
        private int stateVersion, simulationVersion;
        private Avatar avatar;

        // 固定本批次可消费的固定步前缀与采集时的角色、模型代次。
        internal CharacterPresentationWork(in CharacterPresentationInput input, int steps, bool awaitForecast,
            int state, int simulation, Avatar avatar)
        {
            this = default;
            Input = input;
            CoveredSteps = steps;
            AwaitingGaitForecast = awaitForecast;
            stateVersion = state;
            simulationVersion = simulation;
            this.avatar = avatar;
            Pending = true;
        }

        // 判断批次是否仍属于当前角色状态和模型。
        internal bool Matches(int state, int simulation, Avatar currentAvatar) =>
            Pending && stateVersion == state && simulationVersion == simulation && avatar == currentAvatar;

        // 记录已处理动作绑定、尚待交接步态意图的固定步数。
        internal void DeferGait(int count) => DeferredSteps = count;

        // 标记原固定步前缀已全部交接，后到的固定步继续排队。
        internal void FinishGait()
        {
            DeferredSteps = 0;
            AwaitingGaitForecast = false;
        }

        // 预测改变时仅更新轨迹并使原候选失效。
        internal void RefreshForecast(CharacterMovementForecast published)
        {
            Input = Input.RefreshForecast(published, out bool changed);
            RequestSelection(changed);
        }

        // 批次外的动作提交或撤回使已采集的候选失效。
        internal void RequestSelection(bool changed) => NeedsSelection |= Pending && changed;

        // 记录最近一次选姿已使用最新预测与动作需求。
        internal void Select(CharacterPoseCandidate candidate)
        {
            Candidate = candidate;
            NeedsSelection = false;
        }

        // 关闭外部请求对本次下发的修改，保留输入直至播放和 IK 完成交接。
        internal void BeginApply() => Pending = false;
    }
}
