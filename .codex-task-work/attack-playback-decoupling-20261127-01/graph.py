exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterForecastOrigin');sub('solvedBehavior, actual.Contacts, gait);','solvedBehavior, actual.Contacts, gait, time);');write()
use('CharacterForecastRollout');sub('motion.Sample(behavior, frame.Held.Gait, forced)', 'motion.Sample(behavior, frame.Held.Gait, frame.Time, forced)');sub('motion.Sample(behavior, frame.Held.Gait, end <', 'motion.Sample(behavior, frame.Held.Gait, end, end <');sub('CharacterMoveGait gait, bool forced)', 'CharacterMoveGait gait, double time, bool forced)');sub('contacts, gait, forced);','contacts, gait, time, forced);');write()
use('CharacterForecastTrail');sub('driver.LatestFrame.CollisionFlags, driver.FrameInput.Gait);','driver.LatestFrame.CollisionFlags, driver.FrameInput.Gait, t);');write()
use('CharacterMovementForecast')
cut('        private readonly double attackElapsed;', '        public readonly long AttackInstance;', '''        private readonly CharacterPlaybackClock playback;
        private readonly double sampleTime;
        internal bool HasAttackPose => (Behaviors & CharacterBehaviorId.Attack) != 0 && !ExternallyControlled &&
            playback.Valid && sampleTime >= playback.Start && sampleTime < playback.End;
        internal float AttackClipPhase => playback.Sample(sampleTime).Phase;
''')
sub('CharacterMoveGait gait, bool externallyControlled = false)','CharacterMoveGait gait, double time, bool externallyControlled = false)')
cut('            attackElapsed = behavior.Attack.ElapsedSeconds;', '            AttackInstance =', '''            sampleTime = time;
            playback = behavior.Attack.Presentation.Schedule(behavior.Attack.StartedAt);
''')
sub('default, Contacts, Gait, true);', 'default, Contacts, Gait, sampleTime, true);')
sub('''            if (a.AttackStateId == b.AttackStateId && a.AttackInstance == b.AttackInstance)
            {
                AttackProgress = Mathf.Lerp(a.AttackProgress, b.AttackProgress, fraction);
                attackElapsed = a.attackElapsed + (b.attackElapsed - a.attackElapsed) * fraction;
            }
            else attackElapsed = a.attackElapsed + (double)stepSeconds * fraction;''','''            sampleTime = a.sampleTime + (double)stepSeconds * fraction;
            if (a.AttackStateId == b.AttackStateId && a.AttackInstance == b.AttackInstance)
                AttackProgress = Mathf.Lerp(a.AttackProgress, b.AttackProgress, fraction);''')
write()
use('CharacterAnimationMovement')
sub('        private double elapsed;', '''        private double elapsed, pauseStart, pausedSeconds;
        internal double Time => (Paused ? pauseStart : UnityEngine.Time.timeAsDouble) - pausedSeconds;''')
sub('            Paused = paused;', '''            double now = UnityEngine.Time.timeAsDouble;
            if (paused) pauseStart = now;
            else pausedSeconds += now - pauseStart;
            Paused = paused;''');write()
use('CharacterBehaviorAnimator')
sub('driver ? driver.CharacterTime : Time.timeAsDouble','driver ? driver.CharacterTime : external.Time')
sub('pipeline.Withdraw(playback);','pipeline.Withdraw(playback, driver ? driver.CharacterTime : external.Time);')
sub('pipeline.Withdraw(semanticKey, instanceId);','pipeline.Withdraw(semanticKey, instanceId, driver ? driver.CharacterTime : external.Time);')
sub('pipeline.Submit(request, LatestFrame, StateVersion, SimulationVersion);','pipeline.Submit(request, LatestFrame, StateVersion, SimulationVersion, driver ? driver.CharacterTime : external.Time);')
write()
use('CharacterAnimationPipeline')
sub('int currentStateVersion, int simulationVersion)\n        {\n            Join();\n            if (stateVersion != currentStateVersion)\n                Reset(default','int currentStateVersion, int simulationVersion)\n        {\n            Join();\n            if (stateVersion != currentStateVersion)\n                Reset(default') if False else None
sub('''public bool Submit(CharacterAnimationRequest request, CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion)
        {
            Join();''','''public bool Submit(CharacterAnimationRequest request, CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion, double now)
        {
            Join();
            modules.Composer.SetTime(now);''')
sub('public bool Withdraw(string semanticKey, long instanceId)\n        {\n            Join();','public bool Withdraw(string semanticKey, long instanceId, double now)\n        {\n            Join();\n            modules.Composer.SetTime(now);')
sub('public bool Withdraw(CharacterActionPlayback playback)\n        {\n            Join();','public bool Withdraw(CharacterActionPlayback playback, double now)\n        {\n            Join();\n            modules.Composer.SetTime(now);')
write()
use('CharacterAnimationPlayer')
cut('        // 在片段图求值后，以本帧实际获选', '        // 仅给实际下发', '''        // 在提交新声部之前结算已开始的旧请求，未获得真实播放的请求没有事件区间。
        internal bool DrainRetired(CharacterAnimationComposer composer)
        {
            bool changed = composer.Retiring.Count > 0;
            int pending = composer.Retiring.Count;
            while (pending-- > 0 && composer.Retiring.Count > 0)
            {
                var handle = composer.Retiring[0];
                bool available = !handle.HasStarted || clips.EvaluateRemainder(handle);
                composer.FinishRetired(handle, available);
            }
            return changed;
        }

        // 图求值后把各声部真实位置写回相应逻辑句柄，同一节点重建也不会另起实例。
        internal void RecordEvaluated(in CharacterAnimationPlaybackPlan plan)
        {
            Record(plan.Overlay);
            Record(plan.LowerBody);
            Record(plan.UpperBody);
        }

        // 使用实际求值的编号、素材和秒数确认当前句柄的事件游标。
        private void Record(in CharacterAnimationLayerPlan layer)
        {
            var playback = layer.Demand.Playback;
            if (playback == null || !playback.Clock.Valid || !playback.HasStarted) return;
            var observation = clips.Observe(layer.Demand.Source);
            if (observation.Ready && observation.Destination.fullPathHash == layer.StateHash)
                playback.RecordCursor(observation.Destination);
        }

        internal bool Evaluating => clips.Evaluating;

        // 事件回调触发硬重置时，延迟图与 IK 的释放直至当前求值退出。
        internal void AfterEvaluation(System.Action change) => clips.AfterEvaluation(change);

''')
write()
use('CharacterClipMixer')
sub('        public bool Empty => target == null;', '''        public bool Empty => target == null;

        // 在替换实际已播的声部前取得原生节点，其他实例不得借用该素材区间。
        internal Playable FindVoice(long playbackId)
        {
            foreach (var voice in voices)
                if (voice.PlaybackId == playbackId) return voice.Clip;
            return Playable.Null;
        }''')
write()
use('CharacterClipPlayback')
sub('        private float delta, clockScale = 1f;', '''        private float delta, clockScale = 1f;
        internal bool Evaluating { get; private set; }
        private Action afterEvaluation;''')
sub('        public void Evaluate()\n        {\n            if (!graph.IsValid()) return;\n            graph.Evaluate(delta);\n        }','''        // 已有声部只结算真实游标之后的合法素材区间；同素材重绑才可从游标重建节点。
        internal bool EvaluateRemainder(CharacterActionPlayback handle)
        {
            if (!(handle.Cursor is CharacterClipProgress cursor) || !states.TryGetValue(cursor.fullPathHash, out var state) ||
                !state.Clip || state.Clip.GetInstanceID() != handle.ClipId || !Available) return false;
            var clock = handle.Clock;
            double end = (double)state.Clip.length * clock.Sample(clock.End).Phase;
            if (cursor.Seconds >= end) return true;
            Open();
            if (!graph.IsValid()) return false;
            var voice = Playable.Null;
            for (int i = 0; i < 4 && !voice.IsValid(); i++)
                voice = body.Source((CharacterAnimationSource)i)?.FindVoice(handle.PlaybackId) ?? Playable.Null;
            bool rebuilt = !voice.IsValid();
            if (rebuilt) voice = AnimationClipPlayable.Create(graph, state.Clip);
            // 临时只向原 Animator 求值已获选的声部，其他部位的素材时间由正常帧提交一次。
            var previous = output.GetSourcePlayable<Playable>();
            Evaluating = true;
            try
            {
                voice.SetTime(cursor.Seconds);
                voice.SetTime(cursor.Seconds);
                float step = (float)((end - cursor.Seconds) / state.Clip.length * clock.Duration);
                if (!(step > 0f)) step = float.Epsilon;
                voice.SetSpeed((end - cursor.Seconds) / step);
                output.SetSourcePlayable(voice);
                graph.Evaluate(step);
                double actual = voice.GetTime();
                handle.RecordCursor(new CharacterClipProgress(cursor.fullPathHash, (float)(actual / state.Clip.length),
                    false, cursor.ClipId, handle.PlaybackId, actual));
                return true;
            }
            finally
            {
                output.SetSourcePlayable(previous);
                if (rebuilt) graph.DestroyPlayable(voice);
                else voice.SetSpeed(0d);
                FinishEvaluation();
            }
        }

        // 图求值发生重入时只登记拆卸操作，保持当前节点和原生缓冲有效。
        internal void AfterEvaluation(Action change)
        {
            if (Evaluating) afterEvaluation += change;
            else change();
        }

        // 退出原生求值后依次处理事件触发的图生命周期操作。
        private void FinishEvaluation()
        {
            Evaluating = false;
            var changes = afterEvaluation;
            afterEvaluation = null;
            changes?.Invoke();
        }

        // 在最终姿势与 IK 全部就绪后进行本帧正常求值。
        public void Evaluate()
        {
            if (!graph.IsValid()) return;
            Evaluating = true;
            try { graph.Evaluate(delta); }
            finally { FinishEvaluation(); }
        }''')
s=s.replace('output.GetSourcePlayable<Playable>()','output.GetSourcePlayable()');write()
use('CharacterAnimationPipeline')
sub('        private bool clearMotionSources;', '        private bool clearMotionSources;\n        private int graphGeneration;')
sub('''            var preview = batch.NeedsSelection ? modules.Composer.Preview(modules.Intent.Current) : batch.Candidate.Owners.Selection;
            bool reconciled''','''            int generation = graphGeneration;
            modules.Composer.RetireDue(batch.Input.Time);
            var preview = modules.Composer.Preview(modules.Intent.Current);
            modules.Composer.RetireDisplaced(preview);
            bool drained = modules.Player.DrainRetired(modules.Composer);
            if (generation != graphGeneration) return;
            if (drained) preview = modules.Composer.Preview(modules.Intent.Current);
            bool reconciled''')
sub('if (reconciled || batch.NeedsSelection)','if (drained || reconciled || batch.NeedsSelection)')
sub('if (clearMotionSources) return; // 片段事件已切换到新的运动基线。\n            modules.Player.FinishEvaluatedAttacks(plan, modules.Bindings);','if (generation != graphGeneration || clearMotionSources) return; // 片段事件已切换到新的运动基线。\n            modules.Player.RecordEvaluated(plan);')
sub('        private bool SynchronizeResources()\n        {', '        private bool SynchronizeResources()\n        {\n            if (modules.Player.Evaluating) return modules.Resources.Ready;')
sub('        internal void MotionBoundary(CharacterMotionBoundaryKind kind, int simulationVersion)\n        {','        internal void MotionBoundary(CharacterMotionBoundaryKind kind, int simulationVersion)\n        {\n            graphGeneration++;')
sub('        public void Reset(CharacterControllerFrameResult latestFrame, int currentStateVersion, int simulationVersion)\n        {','        public void Reset(CharacterControllerFrameResult latestFrame, int currentStateVersion, int simulationVersion)\n        {\n            graphGeneration++;')
sub('            Ik.Reset();\n            modules.Player.CloseGraph();','            modules.Player.AfterEvaluation(() => { Ik.Reset(); modules.Player.CloseGraph(); });')
sub('            Ik.Dispose();\n            modules.Player.Release();','            graphGeneration++;\n            modules.Player.AfterEvaluation(() => { Ik.Dispose(); modules.Player.Release(); });')
write()

