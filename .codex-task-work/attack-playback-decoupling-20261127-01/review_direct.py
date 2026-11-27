exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterBehaviorFlow')
sub('''internal CharacterBehaviorFrameResult AdvanceTimeline(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, Vector2 groundVelocity) =>
            Run(input, facing, InputMath.NonNegative(deltaTime, 0f), settings, groundVelocity, true);''','''internal CharacterBehaviorFrameResult AdvanceTimeline(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, Vector2 groundVelocity, double start, double end) =>
            Run(input, facing, InputMath.NonNegative(deltaTime, 0f), settings, groundVelocity, true, start, end);''')
sub('CharacterMovementSettings settings, Vector2 groundVelocity, bool separateBoundaries)\n        {\n            using var dispatch = EnterDispatch();','''CharacterMovementSettings settings, Vector2 groundVelocity, bool separateBoundaries, double? start = null, double? end = null)
        {
            using var dispatch = EnterDispatch();
            if (start.HasValue) simulationSeconds = start.Value;''')
sub('            if (step.DurationCommitted) simulationSeconds += dt;', '            if (step.DurationCommitted) simulationSeconds = end ?? simulationSeconds + dt;');write()
use('CharacterBehaviorTimeline');sub('seconds, frame.Movement, frame.Velocity);','seconds, frame.Movement, frame.Velocity, start, end);');write()
use('CharacterForecastRollout');sub('flow.AdvanceTimeline(frame.Held, facing, 0f, frame.Movement, groundVelocity);','flow.AdvanceTimeline(frame.Held, facing, 0f, frame.Movement, groundVelocity, time, time);');sub('flow.AdvanceTimeline(input, facing, seconds, frame.Movement, groundVelocity);','flow.AdvanceTimeline(input, facing, seconds, frame.Movement, groundVelocity, time, next);');sub('                    facing, 0f, frame.Movement, groundVelocity);','                    facing, 0f, frame.Movement, groundVelocity, time, time);');write()
use('CharacterAttackPresentation')
sub('internal bool Valid => ClipId != 0 && AnimationSeconds > 0f && !float.IsInfinity(AnimationSeconds);','''internal bool Valid => ClipId != 0 && AnimationSeconds > 0f && !float.IsInfinity(AnimationSeconds) &&
            Entry >= 0f && Active > 0f && Recovery >= 0f && !float.IsInfinity(Entry + Active + Recovery);''')
sub('        internal double ExitAt(', '        // 计算这个固定三段招式的自然退出时刻。\n        internal double ExitAt(');write()
use('CharacterPlaybackClock')
sub('internal bool Valid => Duration > 0f;', '''internal bool Valid => !double.IsNaN(Start) && !double.IsInfinity(Start) && Duration > 0f &&
            !float.IsInfinity(Duration) && BlendIn >= 0f && !float.IsInfinity(BlendIn) && !double.IsInfinity(NaturalEnd);''')
sub('            if (at >= FadeAt) return;','            if (!(at < FadeAt) || double.IsInfinity(at) || !(seconds >= 0f) || float.IsInfinity(seconds)) return;')
sub('        internal void Cancel(double at) => StopAt = Math.Min(StopAt, Math.Max(Start, at));','''        internal void Cancel(double at)
        {
            if (!double.IsNaN(at) && !double.IsInfinity(at)) StopAt = Math.Min(StopAt, Math.Max(Start, at));
        }''')
write()
use('CharacterPulseAnimationState')
sub('维护一条瞬发映射的行为时钟、招式身份、播放句柄与重绑游标。', '维护普通单次映射的许可、阶段与对应的播放句柄。')
sub('收到开始边沿时使用入队时的招式身份，真实开始优先于同帧续播。','新的许可可申请播放；同一执行同一素材的续绑保留原句柄及其终态。')
sub('''            if (source == CharacterPlaybackStartSource.Rebind && animations.SameClip(entry) &&
                (entry.Status == CharacterActionPlaybackStatus.Started || entry.Status == CharacterActionPlaybackStatus.Pending)) return;''','''            if (source == CharacterPlaybackStartSource.Rebind && animations.SameClip(entry) &&
                entry.StateVersion == origin.StateVersion && entry.ExecutionId == origin.Execution) return;''')
write()
use('CharacterAnimationComposer');sub('分别提交本帧状态权重和驱动素材的攻击经过秒数。','提交普通动作本帧的权重和素材采样相位。');write()
use('CharacterAnimationPipeline')
sub('''        public void ResetPoseHistory()
        {
            Join();''','''        public void ResetPoseHistory()
        {
            graphGeneration++;
            Join();''')
sub('            observer.InvalidatePosition();\n            ClearPoseHistory();', '            observer.InvalidatePosition();\n            modules.Player.AfterEvaluation(ClearPoseHistory);')
write()

