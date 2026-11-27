exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterAnimationPlayer')
sub('        private string oneShotWarning;', '''        private string oneShotWarning;
        private CharacterAnimationPlaybackPlan lastEvaluated;
        private readonly CharacterLayerObservation[] seen = new CharacterLayerObservation[4];''')
cut('        // 在提交新声部之前','        internal bool Evaluating', '''        // 已播声部按时间先后共同推进；每段图事件返回后重新核对仲裁、资源和批次代次。
        internal bool DrainRetired(CharacterAnimationComposer composer, CharacterAnimationRequest behavior, double now,
            CharacterAnimationIk ik, CharacterAnimationMotion motion, System.Func<bool> current)
        {
            bool changed = false;
            while (current())
            {
                composer.RetireDue(now, behavior);
                var retiring = composer.Retiring;
                if (retiring.Count == 0) break;
                changed = true;
                double from = double.PositiveInfinity, to = double.PositiveInfinity;
                for (int i = retiring.Count - 1; i >= 0; i--)
                {
                    var handle = retiring[i];
                    bool available = CanResume(handle);
                    if (!available || handle.Clock.End <= handle.LastEvaluatedAt)
                    {
                        composer.FinishRetired(handle, available || !handle.HasStarted);
                        continue;
                    }
                    from = System.Math.Min(from, handle.LastEvaluatedAt);
                    to = System.Math.Min(to, handle.Clock.End);
                }
                if (retiring.Count == 0) break;
                foreach (var handle in retiring)
                    if (handle.LastEvaluatedAt > from) to = System.Math.Min(to, handle.LastEvaluatedAt);
                var plan = new CharacterAnimationPlaybackPlan(lastEvaluated, seen, clips, from);
                if (!clips.PrepareSegment(plan, to, (float)(to - from)))
                {
                    while (retiring.Count > 0) composer.FinishRetired(retiring[retiring.Count - 1], false);
                    break;
                }
                var owners = new CharacterAnimationComposition(plan.FullBody.Demand, plan.UpperOwner.Demand, plan.LowerOwner.Demand);
                ik.Prepare(owners, motion, plan, 0f, to);
                composer.BeginSegment(to);
                try { clips.Evaluate(() => ObserveSegment(plan, to)); }
                finally { composer.EndSegment(now); }
                if (!current()) break;
                for (int i = retiring.Count - 1; i >= 0; i--)
                    if (retiring[i].LastEvaluatedAt < to) composer.FinishRetired(retiring[i], false);
            }
            return changed;
        }

        // 核对上次真实提交的句柄、素材与当前播放库；从未显示的请求没有可补采的区间。
        private bool CanResume(CharacterActionPlayback handle)
        {
            if (!handle.HasStarted || !handle.Cursor.HasValue || handle.StopSource == CharacterPlaybackEndSource.ResourceUnavailable)
                return false;
            foreach (var layer in seen)
                if (layer.Ready && layer.Destination.PlaybackId == handle.PlaybackId)
                    return clips.MatchesMaterial(layer.Destination);
            return false;
        }

        // 图求值后保存完整分层快照，同素材重绑可恢复普通声部的真实位置与计时句柄。
        internal void RecordEvaluated(in CharacterAnimationPlaybackPlan plan)
        {
            lastEvaluated = plan;
            ObserveSegment(plan, null);
        }

        // 读取四个原生混合源的求值结果，区间内未推进的声部保持自己的事件游标。
        private void ObserveSegment(in CharacterAnimationPlaybackPlan plan, double? at)
        {
            for (int i = 0; i < seen.Length; i++) seen[i] = clips.Observe((CharacterAnimationSource)i);
            Record(plan.Overlay, at);
            Record(plan.LowerBody, at);
            Record(plan.UpperBody, at);
        }

        // 只有真实求值且素材、实例均一致的声部才能推进对应句柄。
        private void Record(in CharacterAnimationLayerPlan layer, double? at)
        {
            var playback = layer.Demand.Playback;
            if (playback == null || !playback.HasStarted || playback.EndSource != default) return;
            double time = at.HasValue ? System.Math.Min(at.Value, playback.Clock.End) : layer.Demand.Timing.Sample.Time;
            if (at.HasValue && (!playback.Clock.Valid || time <= playback.LastEvaluatedAt)) return;
            var observation = seen[(int)layer.Demand.Source];
            if (observation.Ready && observation.Destination.fullPathHash == layer.StateHash)
            {
                playback.RecordCursor(observation.Destination, time);
                if (playback.Clock.Valid && playback.Cursor.HasValue) playback.EntryCoverage ??= layer.Demand.EntryCoverage;
            }
        }

''')
write()
use('CharacterAnimationPipeline')
cut('            modules.Composer.RetireDue(batch.Input.Time);','            bool reconciled', '''            bool drained = modules.Player.DrainRetired(modules.Composer, modules.Intent.Current, batch.Input.Time,
                Ik, batch.Input.Motion, () => generation == graphGeneration);
            if (generation != graphGeneration) return;
            var preview = modules.Composer.Preview(modules.Intent.Current);
''')
sub('modules.Bindings.PlayExplicit(key, weight, origin, now)', 'modules.Bindings.PlayExplicit(key, weight, origin, modules.Composer.Time)')
write()

