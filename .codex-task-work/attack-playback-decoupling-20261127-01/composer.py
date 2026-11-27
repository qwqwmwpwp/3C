exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterAnimationResourceCatalog')
cut('        private readonly Dictionary<string, int> attackStates','        private readonly float[] weights','        private readonly Dictionary<string, int> oneShotClips = new Dictionary<string, int>(StringComparer.Ordinal);\n')
s=s.replace('            attackStates.Clear();\n','').replace('            attackTable = config.AttackTable;\n','').replace('            playableAttacks.Clear();','            oneShotClips.Clear();').replace('            CaptureAttackClips();\n','')
cut('                int attackState =','                if (handoff != null','                var handoff = action.Handoff?.OverridePhases == true ? action.Handoff : null;\n')
sub('if (clips.CanPlayOnce(pair.Value.StateHash)) oneShots.Add(pair.Key);','''if (clips.CanPlayOnce(pair.Value.StateHash))
                {
                    oneShots.Add(pair.Key);
                    oneShotClips[pair.Key] = clips.StateClip(pair.Value.StateHash).GetInstanceID();
                }''')
cut('        // 后台规划只读主线程','        public bool HasBinding','''        // 查询主线程冻结的可播放单次素材，后台不读取资源对象。
        public bool CanPlayOnce(string key) => CanSubmit(key) && oneShots.Contains(key);

        // 以实际资源标识核对已冻结的播放请求；不匹配时不得复用旧素材游标。
        internal int OneShotClipId(string key) => key != null && oneShotClips.TryGetValue(key, out var id) ? id : 0;

''')
write()
use('CharacterClipPlayback')
cut('        // 确认动作映射绑定','        public bool CanPlayOnce(')
write()
use('CharacterAnimationIntent')
sub('&& Timing.Handoff.Valid','&& Timing.HasWeight');sub('Timing.Handoff.WeightAt(Timing.WeightPhase.GetValueOrDefault(), EntryCoverage)','Timing.WeightAt(EntryCoverage)')
write()
use('CharacterAnimationComposer')
sub('        private bool planning;', '''        private bool planning;
        private double presentationTime;
        private readonly List<CharacterActionPlayback> retiring = new List<CharacterActionPlayback>();
        internal IReadOnlyList<CharacterActionPlayback> Retiring => retiring;

        // 冻结当前规划时刻；重复试选同一时刻只读取时钟。
        internal void SetTime(double now) => presentationTime = now;''')
sub('CharacterActionHandoff explicitTiming = default)','CharacterPlaybackClock clock = default, int clipId = 0)')
sub('source, explicitTiming);','source, clock, clipId);')
sub('''                End(key, latest, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost);''','''                var at = playback != null && playback.Clock.Valid ? playback.Clock.Start : presentationTime;
                EndAt(key, latest, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost, at);''')
sub('bool startAtZero, bool resumed, double attackElapsed = 0d)','bool startAtZero, bool resumed)')
sub('resumed, attackElapsed: attackElapsed)', 'resumed)')
sub('''        public bool End(string key, long instanceId, CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source)
        {''','''        public bool End(string key, long instanceId, CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source) =>
            EndAt(key, instanceId, status, source, presentationTime);

        // 将已生效的撤销与仲裁移除分开，已开始的计时实例仍须实际求值最后合法区间。
        private bool EndAt(string key, long instanceId, CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source, double at)
        {''')
sub('''            if (playback != null && playback.QueueEnd(status, source))''','''            if (playback != null && playback.Clock.Valid && status != CharacterActionPlaybackStatus.Completed)
            {
                playback.Cancel(at, status, source);
                retiring.Add(playback);
                return true;
            }
            if (playback != null && playback.QueueEnd(status, source))''')
sub('        public CharacterAnimationComposition Preview(','''        // 在主线程实际提交前摘下到期实例；未获选者撤回，已获选者进入尾段结算。
        internal void RetireDue(double now)
        {
            presentationTime = now;
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                var playback = demand.Playback;
                if (playback == null || !playback.Clock.Valid || playback.Clock.End > now) continue;
                rankedKeys.RemoveAt(i);
                active.Remove(demand.Key);
                retiring.Add(playback);
            }
        }

        // 核对实际素材结果后发布终态，无法再次取得素材的实例按资源失效退出。
        internal void FinishRetired(CharacterActionPlayback playback, bool available)
        {
            if (!retiring.Remove(playback)) return;
            bool finished = playback.HasStarted && playback.Clock.NaturalEnd <= playback.Clock.StopAt &&
                playback.Cursor?.normalizedTime >= 1f;
            var status = finished ? CharacterActionPlaybackStatus.Completed : !playback.HasStarted ? CharacterActionPlaybackStatus.Withdrawn :
                !available || playback.StopSource == default ? CharacterActionPlaybackStatus.Interrupted : playback.StopStatus;
            var reason = finished ? CharacterPlaybackEndSource.ClipFinished : !available ? CharacterPlaybackEndSource.ResourceUnavailable :
                playback.StopSource == default ? CharacterPlaybackEndSource.BehaviorWindowEnded : playback.StopSource;
            if (playback.QueueEnd(status, reason)) PublishEnd(playback);
        }

        // 同素材图重建保留逻辑句柄；映射消失或真正换片段时终结旧请求。
        internal void RebindPlayback(CharacterAnimationResourceCatalog resources)
        {
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                var playback = demand.Playback;
                if (playback == null) continue;
                if (playback.Clock.Valid && resources.OneShotClipId(demand.Key) == playback.ClipId) continue;
                End(demand.Key, demand.InstanceId, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ResourceUnavailable);
            }
            previousSelection = default;
        }

        // 以绝对角色时刻对尚在窗口内的请求做纯值试选；再次选择不推进时钟。
        public CharacterAnimationComposition Preview(''')
sub('''                var demand = active[key];
                if (claims.TryClaim''','''                var demand = active[key];
                if (demand.Playback != null && demand.Playback.Clock.Valid)
                {
                    var clock = demand.Playback.Clock;
                    if (presentationTime < clock.Start || presentationTime >= clock.End) continue;
                    demand = demand.WithTiming(new CharacterActionTiming(clock.Sample(presentationTime), demand.Playback.Cursor));
                }
                if (claims.TryClaim''')
# Retire losing previous clocks before graph replacement; remove legacy double-cancel via later Commit.
sub('        public void Commit(in CharacterAnimationComposition selected)','''        // 在替换图声部前，给已经失去部位的计时实例记录准确的交接截止。
        internal void RetireDisplaced(in CharacterAnimationComposition selected)
        {
            RetireDisplaced(previousSelection.Overlay, selected.Overlay);
            RetireDisplaced(previousSelection.UpperBody, selected.UpperBody);
            RetireDisplaced(previousSelection.LowerBody, selected.LowerBody);
        }

        // 新请求到达早于帧末时，旧句柄只采样至新请求的生效时刻。
        private void RetireDisplaced(in CharacterAnimationDemand previous, in CharacterAnimationDemand selected)
        {
            var playback = previous.Playback;
            if (playback == null || !playback.Clock.Valid || selected.Playback == playback) return;
            double at = selected.Playback != null && selected.Playback.Clock.Valid ? selected.Playback.Clock.Start : presentationTime;
            EndAt(previous.Key, previous.InstanceId, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost, at);
        }

        public void Commit(in CharacterAnimationComposition selected)''')
sub('            previousSelection = default;\n            if (forgetInstances)', '            previousSelection = default;\n            if (forgetInstances)') if False else None
# Hard boundaries don't enqueue tails in a different generation.
a=s.index('        public void Reset(CharacterPlaybackEndSource');i=s.index('            previousSelection = default;',a)
s=s[:i]+'''            while (retiring.Count > 0)
            {
                var old = retiring[retiring.Count - 1];
                retiring.RemoveAt(retiring.Count - 1);
                if (old.QueueEnd(old.HasStarted ? CharacterActionPlaybackStatus.Interrupted : CharacterActionPlaybackStatus.Withdrawn, source))
                    PublishEnd(old);
            }
'''+s[i:]
write()
use('CharacterLocomotionPoseSelector')
cut('            var timing = overlay.Playback?.ExplicitAttackTiming','            bool coversFeet','            var timing = overlay.Timing;\n')
sub('timing.Handoff.IsAttack','timing.Sample.Valid');sub('timing.WeightPhase >= timing.Handoff.ReleaseFrom','timing.Sample.Fading')
sub('            if (demand.Playback?.ExplicitAttackTiming.Valid == true) requestedStart = 0f;\n','')
sub('bool attackBlend = demand.HasHandoff && timing.Sample.Valid;', 'bool clockBlend = timing.Sample.Valid;')
sub('attackBlend','clockBlend');sub('Mathf.Max(0f, timing.Handoff.Duration * timing.Handoff.FullFrom - (float)timing.AttackElapsed)','timing.Sample.RemainingEntry')
cut('            var attack = demand.Playback?.ExplicitAttackTiming','            {\n                var handoff = continuing', '''            if (demand.Playback != null && !demand.Timing.HasWeight && !demand.Timing.WeightPhase.HasValue && settings != null)
''');write()
use('CharacterBodyPlaybackGraph')
sub('timing.Handoff.Valid && timing.WeightPhase < timing.Handoff.FullFrom','timing.HasWeight && !timing.Entered');cut('            float untilCovered = timing.Handoff.Valid','            lower.AdvanceWeight','            float untilCovered = timing.RemainingEntry;\n');write()
use('CharacterLayerBlendWeight')
sub('            var handoff = timing.Handoff;', '''            if (timing.Sample.Valid)
            {
                actionKey = demand.Key;
                actionInstance = demand.InstanceId;
                duration = timedSeconds = 0f;
                float coverage = timing.Sample.Weight(demand.EntryCoverage, out float slope);
                velocity = slope * demand.MaximumWeight;
                return coverage * demand.MaximumWeight;
            }
            var handoff = timing.Handoff;''')
sub('bool inherit = !handoff.IsAttack && velocity', 'bool inherit = velocity')
s=s.replace('float coverage = timing.Sample.Weight','float sampledWeight = timing.Sample.Weight').replace('return coverage * demand.MaximumWeight;', 'return sampledWeight * demand.MaximumWeight;');write()
use('CharacterClipMixer')
sub('public bool Attack;', 'public bool Timed;')
sub('float observed = Attack &&', 'float observed = Timed &&')
sub('private float attackEntryCoverage;', 'private float entryCoverage;')
sub('bool attack = demand.HasHandoff && demand.Timing.Handoff.IsAttack;', 'bool timed = demand.Timing.Sample.Valid;')
sub('attackEntryCoverage','entryCoverage');sub('if (attack && fadeTime','if (timed && fadeTime')
sub('fadeTime = demand.Timing.Handoff.Duration * demand.Timing.Handoff.FullFrom;', 'fadeTime = demand.Timing.Sample.Clock.BlendIn;')
sub('smoothFade = attack ||','smoothFade = timed ||');sub('demand.Playback?.ResumeCursor','demand.Playback?.Cursor')
sub('Attack = attack,','Timed = timed,')
sub('bool attackBlend = timing.Handoff.IsAttack;', 'bool timed = timing.Sample.Valid;')
sub('elapsed = attackBlend ? (float)timing.AttackElapsed','elapsed = timed ? (float)timing.Sample.Elapsed');sub('if (attackBlend) blend', 'if (timed) blend');sub('voice.Attack ?','voice.Timed ?')
sub('攻击素材跟随独立片段相位，进入权重跟随状态秒数；已中断的旧攻击只贡献最后姿势。','独立播放跟随实例的素材与权重时钟，已停止的旧声部只贡献最后姿势。')
write()
use('CharacterClipPlayback')
sub('plan.Demand.HasHandoff && plan.Demand.Timing.WeightPhase >= plan.Demand.Timing.Handoff.FullFrom','plan.Demand.Timing.Entered')
write()

