exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterAnimationResourceResolver')
sub('        public bool HasState { get; }','        internal double? RestoredSeconds { get; }\n        public bool HasState { get; }')
sub('            EntryProfile = ExitProfile = null;', '            RestoredSeconds = null;\n            EntryProfile = ExitProfile = null;')
sub('        public float WeightAfter(','''        // 延续原图的部位与蒙版，从已显示的素材时刻重建同素材声部。
        internal CharacterAnimationLayerPlan(in CharacterAnimationLayerPlan source, in CharacterLayerObservation seen,
            CharacterClipPlayback clips, double at)
        {
            this = source;
            if (!source.HasState) return;
            var cursor = seen.Destination;
            var handle = source.Demand.Playback;
            if (!seen.Ready || cursor.fullPathHash != source.StateHash || !clips.MatchesMaterial(cursor) ||
                handle != null && (handle.EndSource != default || handle.PlaybackId != cursor.PlaybackId ||
                handle.StopSource == CharacterPlaybackEndSource.ResourceUnavailable))
            {
                this = default;
                return;
            }
            Command = CharacterAnimationPlaybackCommand.Keep;
            RestoredSeconds = cursor.Seconds;
            if (handle == null || !handle.Clock.Valid) return;
            Demand = Demand.WithTiming(new CharacterActionTiming(handle.Clock.Sample(at), handle.Cursor, handle.InitialPhase));
            Weight = Demand.Weight;
        }

        public float WeightAfter(''')
sub('        // 保存已获选的基础和覆盖计划', '''        // 按上次真实提交的分层关系生成区间起点姿态，完成或换素材的声部退出。
        internal CharacterAnimationPlaybackPlan(in CharacterAnimationPlaybackPlan source, CharacterLayerObservation[] seen,
            CharacterClipPlayback clips, double at)
        {
            this = source;
            FullBody = Restore(source.FullBody, seen, clips, at);
            UpperOwner = Restore(source.UpperOwner, seen, clips, at);
            LowerOwner = Restore(source.LowerOwner, seen, clips, at);
        }

        // 匹配原部位最近一次真实观测，避免把其他层的入点套给当前节点。
        private static CharacterAnimationLayerPlan Restore(in CharacterAnimationLayerPlan layer, CharacterLayerObservation[] seen,
            CharacterClipPlayback clips, double at) => new CharacterAnimationLayerPlan(layer, seen[(int)layer.Demand.Source], clips, at);

        // 保存已获选的基础和覆盖计划''')
write()
use('CharacterClipMixer')
cut('        // 在替换实际已播', '        public CharacterClipMixer', '''        // 保持原混合器的各声部连接，只有当前已播计时句柄推进到本区间的绝对目标。
        internal void PrepareSegment(CharacterActionPlayback handle, double at, float step)
        {
            foreach (var voice in voices)
            {
                double rate = 0d;
                if (handle != null && handle.Clock.Valid && handle.HasStarted && handle.EndSource == default &&
                    handle.PlaybackId == voice.PlaybackId && handle.ClipId == voice.ClipId && at > handle.LastEvaluatedAt)
                {
                    double targetSeconds = handle.Clock.Sample(at).MaterialSeconds(voice.Motion.Length);
                    rate = System.Math.Max(0d, targetSeconds - voice.Clip.GetTime()) / step;
                }
                voice.LogicalRate = (float)rate;
                voice.StepStart = voice.Clip.GetTime();
                voice.Motion.Advance(rate, step);
                voice.StepEnd = voice.Motion.EvaluationTime(step);
            }
        }

''')
sub('            float start, float duration, bool resumeLoop, bool resetEvents','            double start, float duration, bool resumeLoop, bool resetEvents')
write()
use('CharacterClipPlayback')
sub('plan.Start.InSeconds(state.Blend.Second ? state.Blend.Seconds : clip.length), duration, plan.ResumeLoop, plan.Demand.Timing.Resumed, plan.Demand);','plan.RestoredSeconds ?? plan.Start.InSeconds(state.Blend.Second ? state.Blend.Seconds : clip.length),\n                        duration, plan.ResumeLoop, plan.RestoredSeconds.HasValue || plan.Demand.Timing.Resumed, plan.Demand);')
cut('        // 已有声部只结算','        // 图求值发生重入', '''        // 在主线程确认已显示的状态仍然对应同一实际素材。
        internal bool MatchesMaterial(in CharacterClipProgress seen) => states.TryGetValue(seen.fullPathHash, out var state) &&
            state.Clip && state.Clip.GetInstanceID() == seen.ClipId;

        // 先恢复正常分层图与原有声部，再为这一段设置唯一的原生求值步长。
        internal bool PrepareSegment(in CharacterAnimationPlaybackPlan plan, double at, float step)
        {
            Open();
            if (body == null) return false;
            body.Apply(this, plan, 0f);
            delta = step;
            body.Source(CharacterAnimationSource.Baseline)?.PrepareSegment(null, at, step);
            body.Source(CharacterAnimationSource.LowerBody)?.PrepareSegment(plan.LowerBody.Demand.Playback, at, step);
            body.Source(CharacterAnimationSource.UpperBody)?.PrepareSegment(plan.UpperBody.Demand.Playback, at, step);
            body.Source(CharacterAnimationSource.FullBodyOverlay)?.PrepareSegment(plan.Overlay.Demand.Playback, at, step);
            return true;
        }

''')
sub('public void Evaluate()\n', 'public void Evaluate(Action record = null)\n')
sub('try { graph.Evaluate(delta); }','''try
            {
                graph.Evaluate(delta);
                record?.Invoke(); // 延后重置拆图前，先记录当前区间已经实际采过的事件位置。
            }''')
write()

