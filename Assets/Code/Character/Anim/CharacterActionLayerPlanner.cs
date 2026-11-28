using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 从通用动作绑定与真实接管时钟计算覆盖源的播放指令。
    internal sealed class CharacterActionLayerPlanner
    {
        private readonly CharacterAnimationResourceCatalog resources;

        // 使用本角色的已绑定动作目录。
        internal CharacterActionLayerPlanner(CharacterAnimationResourceCatalog resources) => this.resources = resources;

        // 解析持续或单次动作的姿势变体、入点及交接权重。
        internal CharacterAnimationLayerPlan Select(CharacterAnimationDemand demand, in CharacterAnimationLayerPlan previous,
            bool moving)
        {
            if (!demand.IsValid)
                return new CharacterAnimationLayerPlan(new CharacterAnimationLayerPlan(previous.LayerBlendSeconds), previous, resources.Transitions);
            var binding = resources.ResolveBinding(demand, moving);
            if (demand.HasHandoff)
            {
                float applied = previous.HasState || previous.LayerBlendSeconds > 0f ? resources.LayerWeight(demand.Source) : 0f;
                float coverage = demand.Timing.StartAtZero || demand.MaximumWeight <= 0f ? 0f : applied / demand.MaximumWeight;
                if (demand.Playback == previous.Demand.Playback && demand.Playback != null) coverage = previous.Demand.EntryCoverage;
                demand = demand.WithEntryCoverage(demand.Playback?.EntryCoverage ?? coverage);
            }
            float phase = demand.StartNormalizedTime ?? demand.Timing.StartClipPhase ?? binding.Start;
            var entry = CharacterClipStart.Normalized(phase);
            var blend = demand.Timing.Sample.Valid ? CharacterBlendDuration.Seconds(demand.Timing.Sample.RemainingEntry)
                : CharacterBlendDuration.SourceFraction(previous.HasState ? Mathf.Min(binding.Transition, previous.Binding.ExitTransition) : binding.Transition);
            bool same = previous.HasState && previous.StateHash == binding.StateHash &&
                previous.Demand.Key == demand.Key && previous.Demand.InstanceId == demand.InstanceId;
            var command = same ? CharacterAnimationPlaybackCommand.Keep : previous.HasState && blend.Value > 0f
                ? CharacterAnimationPlaybackCommand.CrossFade : CharacterAnimationPlaybackCommand.Play;
            var reason = same ? CharacterAnimationPlaybackReason.KeepInstance : !previous.HasState ? CharacterAnimationPlaybackReason.Enter
                : previous.StateHash == binding.StateHash ? CharacterAnimationPlaybackReason.ReplayInstance : CharacterAnimationPlaybackReason.SwitchState;
            if (command == CharacterAnimationPlaybackCommand.CrossFade && demand.HasHandoff &&
                resources.StateSeconds(binding.StateHash) is float length) entry = CharacterClipStart.Fixed(phase * length, phase);
            var selected = new CharacterAnimationLayerPlan(demand, binding, command, reason, demand.Weight, entry, blend,
                demand.HasHandoff ? 0f : binding.LayerBlend);
            return new CharacterAnimationLayerPlan(selected, previous, resources.Transitions);
        }
    }
}
