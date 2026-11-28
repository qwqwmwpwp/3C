using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 保存尚未生效的单次请求，按生效时刻和同刻提交次序交付。
    internal sealed class CharacterActionSchedule
    {
        private readonly List<CharacterAnimationDemand> pending = new List<CharacterAnimationDemand>();
        private readonly Dictionary<CharacterActionPlayback, double> earlyStops = new Dictionary<CharacterActionPlayback, double>();

        // 预约开始前的撤销独立于片段时钟，可在起播之前发布终态。
        private double Due(in CharacterAnimationDemand demand) =>
            earlyStops.TryGetValue(demand.Playback, out double stop) ? stop : demand.Playback.Clock.Start;

        // 按有序时间线插入或重新安排一个请求。
        internal void Add(in CharacterAnimationDemand demand)
        {
            double at = Due(demand);
            int index = pending.Count;
            while (index > 0)
            {
                var before = pending[index - 1];
                double time = Due(before);
                if (time < at || time == at && before.SubmissionOrder <= demand.SubmissionOrder) break;
                index--;
            }
            pending.Insert(index, demand);
        }

        // 查找外部句柄所属的预约，仅本宿主接受该句柄的定时命令。
        internal bool Contains(CharacterActionPlayback playback) =>
            pending.Exists(demand => demand.Playback == playback);

        // 撤销未开始的预约时保留真实撤销时点，此预约从此不占用任何部位。
        internal bool StopBeforeStart(CharacterActionPlayback playback, double at)
        {
            int index = pending.FindIndex(demand => demand.Playback == playback);
            if (index < 0 || at > playback.Clock.Start ||
                earlyStops.TryGetValue(playback, out double previous) && at >= previous) return false;
            var demand = pending[index];
            pending.RemoveAt(index);
            earlyStops[playback] = at;
            Add(demand);
            return true;
        }

        // 返回最早未消费的起播或撤销边沿。
        internal double Next => pending.Count == 0 ? double.PositiveInfinity : Due(pending[0]);

        // 消费到达边界的首个预约，取消早于开始的请求仅发布撤销结果。
        internal bool TryTake(double through, out CharacterAnimationDemand demand, out bool cancelled)
        {
            demand = default;
            cancelled = false;
            if (pending.Count == 0 || Next > through) return false;
            demand = pending[0];
            pending.RemoveAt(0);
            cancelled = earlyStops.Remove(demand.Playback);
            return true;
        }

        // 清除卸载、失去资源或角色边界涉及的预约并逐一发布真实终态。
        internal void RemoveWhere(Predicate<CharacterAnimationDemand> matches, Action<CharacterActionPlayback> removed)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var demand = pending[i];
                if (!matches(demand)) continue;
                pending.RemoveAt(i);
                earlyStops.Remove(demand.Playback);
                removed(demand.Playback);
            }
        }
    }
}
