exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterAnimationPlayer')
sub('CharacterAnimationIk ik, CharacterAnimationMotion motion, System.Func<bool> current)', 'CharacterAnimationIk ik, CharacterAnimationMotion motion, System.Func<bool> current, ref CharacterAnimationPlaybackPlan committed)')
sub('if (!current()) break;\n                // 新请求','if (!current()) break;\n                committed = plan;\n                // 新请求')
write()
use('CharacterAnimationPipeline')
sub('Ik, batch.Input.Motion, () => generation == graphGeneration);', 'Ik, batch.Input.Motion, () => generation == graphGeneration, ref committedPlan);')
sub('if (generation != graphGeneration) return;\n            var preview', '''if (generation != graphGeneration) return;
            if (drained) batch.Observed = modules.Playback.Capture(committedPlan.FullBody, batch.Input.Feet).ForPhase(modules.Intent.Phase);
            var preview''')
sub('            modules.Intent.ObserveDash(frame.Behavior);','''            var before = modules.Intent.Current;
            modules.Intent.ObserveDash(frame.Behavior);
            var after = modules.Intent.Current;
            if (before.SemanticKey != after.SemanticKey) modules.Composer.ChangeBaseline(after, frame.Time);''')
write()
use('CharacterPlaybackArbitration')
sub('private readonly List<Interval> ranked = new List<Interval>();','''private readonly List<Interval> ranked = new List<Interval>();
        private readonly List<(double at, CharacterAnimationDemand demand)> baselines = new();

        // 保存本帧已观察到的基础动作切换，包括两次提交之间开始又结束的突进。
        internal void ChangeBaseline(in CharacterAnimationDemand demand, double at) => baselines.Add((at, demand));''')
sub('in CharacterAnimationDemand nextBase, double now, Predicate<string> hasBinding)', 'in CharacterAnimationDemand nextBase, in CharacterAnimationDemand idle, double now, Predicate<string> hasBinding)')
sub('double next = at < now ? now : double.PositiveInfinity;','''double next = at < now ? now : double.PositiveInfinity;
                foreach (var change in baselines)
                {
                    if (change.at <= at && at < now) baseline = change.demand;
                    if (change.at > at) next = Math.Min(next, change.at);
                }''')
sub('                        baseline = default;', '                        baseline = idle;')
sub('internal void Prune() => ranked.RemoveAll(entry => !double.IsPositiveInfinity(entry.End));','''internal void Prune()
        {
            ranked.RemoveAll(entry => !double.IsPositiveInfinity(entry.End));
            baselines.Clear();
        }''')
sub('internal void Clear() => ranked.Clear();','''internal void Clear()
        {
            ranked.Clear();
            baselines.Clear();
        }''')
write()
use('CharacterAnimationComposer')
sub('        // 在摘下到期请求之前', '''        // 以观察到的角色时刻登记基础动作的优先级切换。
        internal void ChangeBaseline(CharacterAnimationRequest behavior, double at) => ownership.ChangeBaseline(Resolve(behavior), at);

        // 在摘下到期请求之前''')
sub('var at = ownership.FirstLoss(previous, previousSelection.FullBody, baseline, now, hasBinding);','''var idle = Resolve(new CharacterAnimationRequest(CharacterAnimationIntentConverter.IdleKey, baseline.InstanceId));
            var at = ownership.FirstLoss(previous, previousSelection.FullBody, baseline, idle, now, hasBinding);''')
write()

