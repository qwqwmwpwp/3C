exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterPlaybackClock')
sub('        // 从真实入场覆盖量过渡','''        // 把合法播放秒数换算成双精度素材秒数，取消端点不经单精度相位往返。
        internal double MaterialSeconds(float clipLength) => !Valid ? 0d : Time >= Clock.NaturalEnd ? clipLength :
            Math.Min(clipLength, Elapsed / Clock.Duration * clipLength);

        // 从真实入场覆盖量过渡''')
write()
use('CharacterClipMixer');sub('double seconds = (double)timing.ClipPhase.Value * voice.Motion.Length;', 'double seconds = timed ? timing.Sample.MaterialSeconds(voice.Motion.Length) : (double)timing.ClipPhase.Value * voice.Motion.Length;');write()
use('CharacterClipPlayback')
sub('double end = (double)state.Clip.length * clock.Sample(clock.End).Phase;', 'double end = clock.Sample(clock.End).MaterialSeconds(state.Clip.length);')
sub('''                handle.RecordCursor(new CharacterClipProgress(cursor.fullPathHash, (float)(actual / state.Clip.length),
                    false, cursor.ClipId, handle.PlaybackId, actual));''','''                float observed = actual >= state.Clip.length ? 1f : Mathf.Min(0.99999994f, (float)(actual / state.Clip.length));
                handle.RecordCursor(new CharacterClipProgress(cursor.fullPathHash, observed, false, cursor.ClipId, handle.PlaybackId, actual));''')
write()
use('CharacterAttackAnimationBinding')
sub('''        // 标记该请求的最后合法时刻''','''        // 角色硬重置后丢弃旧招身份，新代次可以独立接收相同编号。
        internal void Reset()
        {
            instance = 0;
            fading = false;
            playback = null;
        }

        // 标记该请求的最后合法时刻''')
write()
use('CharacterBehaviorAnimationBinding');sub('            Origin = default;\n            if (IsPulse)', '            Origin = default;\n            Attack?.Reset();\n            if (IsPulse)');write()
use('CharacterBehaviorAnimationBindings');cut('        // 中断逐帧提交的脉冲','        // 清理失去时间窗');write()
use('CharacterPulseAnimationState');cut('        // 暂停撤回句柄','        // 撤回时间窗');write()
use('CharacterAnimationPlayer')
# On same-material reconstruction, legacy requests haven't reached the goal simply from the new graph starting empty.
sub('if (playback == null || playback.Clock.Valid || playback.Status', 'if (playback == null || playback.Clock.Valid || playback.Status')
write()
use('CharacterActionPlayback')
sub('        internal float? EntryCoverage;', '        internal double LastEvaluatedAt { get; private set; }\n        internal float? EntryCoverage;')
sub('internal void RecordCursor(in CharacterClipProgress sample)','internal void RecordCursor(in CharacterClipProgress sample, double at)')
sub('            if (sample.PlaybackId == PlaybackId && sample.ClipId == ClipId && !sample.loop) Cursor = sample;', '''            if (sample.PlaybackId != PlaybackId || sample.ClipId != ClipId || sample.loop) return;
            Cursor = sample;
            LastEvaluatedAt = at;''')
write()
use('CharacterAnimationPlayer');sub('playback.RecordCursor(observation.Destination);','playback.RecordCursor(observation.Destination, layer.Demand.Timing.Sample.Time);');write()
use('CharacterClipPlayback');sub('false, cursor.ClipId, handle.PlaybackId, actual));','false, cursor.ClipId, handle.PlaybackId, actual), clock.End);');write()
use('CharacterAnimationComposer')
sub('''            double at = selected.Playback != null && selected.Playback.Clock.Valid ? selected.Playback.Clock.Start : presentationTime;
            EndAt''','''            double at = selected.Playback != null && selected.Playback.Clock.Valid &&
                selected.Playback.Clock.Start >= playback.LastEvaluatedAt ? selected.Playback.Clock.Start : presentationTime;
            EndAt''')
write()

