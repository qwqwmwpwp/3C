exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterClipMixer')
cut('        // 保持原混合器的各声部连接', '        public CharacterClipMixer')
sub('            public long InstanceId, PlaybackId;', '''            public long InstanceId;
            public CharacterActionPlayback Playback;
            public long PlaybackId => Playback?.PlaybackId ?? 0;''')
sub('returning.PlaybackId = demand.Playback?.PlaybackId ?? 0;','returning.Playback = demand.Playback;')
sub('PlaybackId = demand.Playback?.PlaybackId ?? 0,','Playback = demand.Playback,')
sub('double rate = voice.Timed ? 0d : playback.Pace(voice.Hash).Rate(frame.StrideSpeed, frame.StartSpeed, frame.StopRate, out _);','''bool ended = voice.Playback != null && voice.Playback.EndSource != CharacterPlaybackEndSource.None;
                double rate = voice.Timed || ended ? 0d : playback.Pace(voice.Hash).Rate(frame.StrideSpeed, frame.StartSpeed, frame.StopRate, out _);''')
sub('if (voice == target && timing.ClipPhase.HasValue && dt > 0f)', 'if (!ended && voice == target && timing.ClipPhase.HasValue && dt > 0f)')
sub('rate = System.Math.Max(0d, seconds - voice.Clip.GetTime()) / dt;', '''double current = voice.Clip.GetTime();
                    rate = System.Math.Max(0d, seconds - current) / dt;
                    // 自然终点仅在实际素材末端向上取一位，防止除再乘遗漏最后一个事件。
                    if (timed && timing.Sample.Time >= timing.Sample.Clock.NaturalEnd && current + rate * dt < seconds)
                        rate = System.BitConverter.Int64BitsToDouble(System.BitConverter.DoubleToInt64Bits(rate) + 1);''')
write()
use('CharacterAnimationComposer')
sub('        // 按语义键撤销调用者指定的实例。','''        // 为普通外部定相位动作读取本帧已提交的目标，核对句柄以免跨实例插值。
        internal CharacterActionTiming CurrentTiming(CharacterActionPlayback playback) =>
            active.TryGetValue(playback.Key, out var demand) && demand.Playback == playback ? demand.Timing : default;

        // 按语义键撤销调用者指定的实例。''')
write()
use('CharacterAnimationResourceResolver')
sub('CharacterClipPlayback clips, double at)\n        {\n            this = source;\n            if (!source.HasState)', '''CharacterClipPlayback clips, CharacterAnimationComposer composer, in CharacterAnimationPlaybackPlan frame,
            in CharacterPlaybackFrameStep step)
        {
            this = source;
            if (!source.HasState)''')
sub('''            if (handle == null || !handle.Clock.Valid) return;
            Demand = Demand.WithTiming(new CharacterActionTiming(handle.Clock.Sample(at), handle.Cursor, handle.InitialPhase));
            Weight = Demand.Weight;''','''            if (handle == null) return;
            var timing = source.Demand.Timing;
            if (handle.Clock.Valid)
                timing = new CharacterActionTiming(handle.Clock.Sample(step.End), handle.Cursor, handle.InitialPhase);
            else if (timing.WeightPhase.HasValue)
            {
                bool followClip = !timing.ClipPhase.HasValue;
                float phase = timing.WeightPhase.Value;
                var latest = composer.CurrentTiming(handle);
                if (followClip && seen.DestinationLength > 0f)
                {
                    float rate = clips.Pace(source.StateHash).Rate(frame.StrideSpeed, frame.StartSpeed, frame.StopRate, out _);
                    phase = Mathf.Clamp01((float)((cursor.Seconds + step.GraphDelta * rate) / seen.DestinationLength));
                }
                else if (latest.WeightPhase.HasValue)
                    phase = Mathf.Lerp(phase, latest.WeightPhase.Value, (float)step.Fraction);
                timing = new CharacterActionTiming(timing.StartClipPhase, phase,
                    latest.Handoff.Valid ? latest.Handoff : timing.Handoff, timing.StartAtZero, timing.Resumed, followClip);
            }
            Demand = Demand.WithTiming(timing);
            Weight = Demand.Weight;''')
sub('按上次真实提交的分层关系生成区间起点姿态','按上次真实提交的分层关系生成本段截止时刻的姿态')
cut('            CharacterClipPlayback clips, double at)\n        {\n            this = source;\n            FullBody', '        // 保存已获选的基础和覆盖计划', '''            CharacterClipPlayback clips, CharacterAnimationComposer composer, in CharacterPlaybackFrameStep step)
        {
            this = source;
            FullBody = new CharacterAnimationLayerPlan(source.FullBody, seen[(int)source.FullBody.Demand.Source], clips, composer, source, step);
            UpperOwner = new CharacterAnimationLayerPlan(source.UpperOwner, seen[(int)source.UpperOwner.Demand.Source], clips, composer, source, step);
            LowerOwner = new CharacterAnimationLayerPlan(source.LowerOwner, seen[(int)source.LowerOwner.Demand.Source], clips, composer, source, step);
        }

''')
write()

