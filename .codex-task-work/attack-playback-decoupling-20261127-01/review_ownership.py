exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterBehaviorFlow')
sub('Vector2 groundVelocity, double start, double end) =>','Vector2 groundVelocity, double? start = null, double? end = null) =>')
write()
use('CharacterAnimationComposer')
sub('private long submissionOrder;', '''private long submissionOrder;
        private readonly CharacterPlaybackArbitration ownership = new CharacterPlaybackArbitration();
        private double? evaluatingAt;''')
sub('internal void SetTime(double now) => presentationTime = now;', '''internal void SetTime(double now) => presentationTime = evaluatingAt ?? now;

        // 动画事件中的请求沿用当前原生求值区间的截止时刻。
        internal void BeginSegment(double at) { evaluatingAt = at; presentationTime = at; }

        // 回到本次提交的角色帧末，各请求仍保留各自生效时刻。
        internal void EndSegment(double now) { evaluatingAt = null; presentationTime = now; }''')
sub('active[key] = demand;\n            return true;', '''active[key] = demand;
            ownership.Add(demand, playback != null && playback.Clock.Valid ? playback.Clock.Start : presentationTime);
            return true;''')
sub('active.Remove(key);\n            var playback', 'active.Remove(key);\n            ownership.Close(demand, at);\n            var playback')
sub('internal void RetireDue(double now)','internal void RetireDue(double now, CharacterAnimationRequest behavior)')
sub('presentationTime = now;\n            for (int i = rankedKeys', '''presentationTime = now;
            var baseline = Resolve(behavior);
            RetireDisplaced(previousSelection.UpperOwner, baseline, now);
            if (previousSelection.LowerOwner.Playback != previousSelection.UpperOwner.Playback)
                RetireDisplaced(previousSelection.LowerOwner, baseline, now);
            for (int i = rankedKeys''')
sub('active.Remove(demand.Key);\n                retiring.Add', 'active.Remove(demand.Key);\n                ownership.Close(demand, playback.Clock.End);\n                retiring.Add')
sub('RetireChangedClips(resources);\n            previousSelection = default;', 'RetireChangedClips(resources);')
cut('        // 在替换图声部前','        // 提交最终获选组合', '''        // 在摘下到期请求之前重放真实部位冲突，已经离开活动集合的句柄也可缩短尾段。
        private void RetireDisplaced(in CharacterAnimationDemand previous, in CharacterAnimationDemand baseline, double now)
        {
            var handle = previous.Playback;
            if (handle == null || !handle.Clock.Valid || !handle.HasStarted || handle.EndSource != default) return;
            var at = ownership.FirstLoss(previous, previousSelection.FullBody, baseline, now, hasBinding);
            if (!at.HasValue) return;
            if (!EndAt(previous.Key, previous.InstanceId, CharacterActionPlaybackStatus.Interrupted,
                CharacterPlaybackEndSource.ArbitrationLost, at.Value))
                handle.Cancel(at.Value, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost);
        }

''')
sub('previousSelection = selected;', 'previousSelection = selected;\n            ownership.Prune();')
sub('previousSelection = default;\n        }\n    }', 'previousSelection = default;\n            ownership.Clear();\n        }\n    }')
write()
new('Anim/CharacterPlaybackArbitration.cs','''using System;
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
            in CharacterAnimationDemand nextBase, double now, Predicate<string> hasBinding)
        {
            var handle = old.Playback;
            double at = handle.LastEvaluatedAt;
            while (at < handle.Clock.End && at <= now)
            {
                var baseline = at < now ? previousBase : nextBase;
                var claims = new CharacterBodyClaims();
                double next = at < now ? now : double.PositiveInfinity;
                foreach (var interval in ranked)
                {
                    double until = interval.Until;
                    if (interval.Start > at) next = Math.Min(next, interval.Start);
                    if (until > at) next = Math.Min(next, until);
                    if (at < interval.Start || at >= until || !hasBinding(interval.Demand.Key)) continue;
                    if (claims.TryClaim(interval.Demand, baseline) && baseline.Key == CharacterAnimationIntentConverter.DashKey)
                        baseline = default;
                }
                var owners = claims.Compose(baseline);
                if (old.Source != CharacterAnimationSource.LowerBody && owners.UpperOwner.Playback != handle ||
                    old.Source != CharacterAnimationSource.UpperBody && owners.LowerOwner.Playback != handle) return at;
                at = next;
            }
            return null;
        }

        // 提交完成后只保留下次仍可能参与部位仲裁的请求。
        internal void Prune() => ranked.RemoveAll(entry => !double.IsPositiveInfinity(entry.End));

        // 硬重置丢弃旧角色的所有请求区间。
        internal void Clear() => ranked.Clear();
    }
}
''','CharacterPlaybackClock')

