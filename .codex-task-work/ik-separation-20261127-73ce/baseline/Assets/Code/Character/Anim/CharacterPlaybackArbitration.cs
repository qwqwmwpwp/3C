using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 在两次真实提交之间保留请求的可选区间，以相同的部位规则找出已播实例首次失去所有权的时刻。
    internal sealed class CharacterPlaybackArbitration
    {
        // 只保存仲裁所需的不可变需求和请求在角色时间线上的有效区间。
        private sealed class Interval
        {
            internal CharacterAnimationDemand Demand;
            internal double Start, End = double.PositiveInfinity;
            internal double Until => Demand.Playback != null && Demand.Playback.Clock.Valid ?
                Math.Min(End, Demand.Playback.Clock.End) : End;
        }

        private readonly List<Interval> ranked = new List<Interval>();
        private readonly List<(double at, CharacterAnimationDemand demand)> baselines = new();

        // 保存本帧已观察到的基础动作切换，包括两次提交之间开始又结束的突进。
        internal void ChangeBaseline(in CharacterAnimationDemand demand, double at) => baselines.Add((at, demand));

        // 按正常仲裁的相同优先顺序插入新请求。
        internal void Add(in CharacterAnimationDemand demand, double start)
        {
            int i = ranked.Count;
            while (i > 0 && demand.Precedes(ranked[i - 1].Demand)) i--;
            ranked.Insert(i, new Interval { Demand = demand, Start = start });
        }

        // 关闭请求的活动区间，保留它对本次提交前其他声部的真实影响。
        internal void Close(in CharacterAnimationDemand demand, double at)
        {
            foreach (var entry in ranked)
                if (entry.Demand.SubmissionOrder == demand.SubmissionOrder) { entry.End = Math.Min(entry.End, at); break; }
        }

        // 逐个检查所有可能改变部位归属的起止边界；只有真正赢得仲裁的竞争者才会截断旧实例。
        internal double? FirstLoss(in CharacterAnimationDemand old, in CharacterAnimationDemand previousBase,
            in CharacterAnimationDemand nextBase, in CharacterAnimationDemand idle, double now, Predicate<string> hasBinding)
        {
            var handle = old.Playback;
            double at = handle.LastEvaluatedAt;
            while (at < handle.Clock.End && at <= now)
            {
                var baseline = at < now ? previousBase : nextBase;
                var claims = new CharacterBodyClaims();
                double next = at < now ? now : double.PositiveInfinity;
                foreach (var change in baselines)
                {
                    if (change.at <= at && at < now) baseline = change.demand;
                    if (change.at > at) next = Math.Min(next, change.at);
                }
                foreach (var interval in ranked)
                {
                    double until = interval.Until;
                    if (interval.Start > at) next = Math.Min(next, interval.Start);
                    if (until > at) next = Math.Min(next, until);
                    if (at < interval.Start || at >= until || !hasBinding(interval.Demand.Key)) continue;
                    if (claims.TryClaim(interval.Demand, baseline) && baseline.Key == CharacterAnimationIntentConverter.DashKey)
                        baseline = idle;
                }
                var owners = claims.Compose(baseline);
                if (old.Source != CharacterAnimationSource.LowerBody && owners.UpperOwner.Playback != handle ||
                    old.Source != CharacterAnimationSource.UpperBody && owners.LowerOwner.Playback != handle) return at;
                at = next;
            }
            return null;
        }

        // 提交完成后只保留下次仍可能参与部位仲裁的请求。
        internal void Prune()
        {
            ranked.RemoveAll(entry => !double.IsPositiveInfinity(entry.End));
            baselines.Clear();
        }

        // 硬重置丢弃旧角色的所有请求区间。
        internal void Clear()
        {
            ranked.Clear();
            baselines.Clear();
        }
    }
}
