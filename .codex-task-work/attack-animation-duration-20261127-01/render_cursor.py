exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterPlaybackObservation')
sub('    internal readonly struct CharacterClipProgress','    // 标识实际求值的片段、句柄与精确素材秒数，归一化相位供选姿和结束判定。\n    internal readonly struct CharacterClipProgress')
sub('        public readonly bool loop;', '''        public readonly bool loop;
        internal readonly int ClipId;
        internal readonly long PlaybackId;
        internal readonly double Seconds;''')
sub('        public CharacterClipProgress(int hash, float progress, bool looping)','''        // 捕获素材声部的实际时钟，不从行为目标或上一帧计划反推。
        public CharacterClipProgress(int hash, float progress, bool looping, int clipId, long playbackId, double seconds)''')
sub('            loop = looping;', '            loop = looping;\n            ClipId = clipId;\n            PlaybackId = playbackId;\n            Seconds = seconds;')
write()
use('CharacterClipMixer')
sub('public long InstanceId;', 'public long InstanceId, PlaybackId;')
sub('public int Hash, Port;', 'public int Hash, ClipId, Port;')
sub('double phase = Motion.Length > 0f ? Clip.GetTime() / Motion.Length : 0d;', 'double seconds = Clip.GetTime();\n                    double phase = Motion.Length > 0f ? seconds / Motion.Length : 0d;')
sub('new CharacterClipProgress(Hash, observed, Motion.Loop);', 'new CharacterClipProgress(Hash, observed, Motion.Loop, ClipId, PlaybackId, seconds);')
sub('                returning.InstanceId = demand.InstanceId;', '                returning.InstanceId = demand.InstanceId;\n                returning.PlaybackId = demand.Playback?.PlaybackId ?? 0;')
sub('''            motion.Seek(start);
            if (resetEvents) motion.Seek(start);''','''            int clipId = asset.GetInstanceID();
            double startSeconds = start;
            if (resetEvents && demand.Playback?.ResumeCursor is CharacterClipProgress cursor)
                startSeconds = cursor.ClipId == clipId ? cursor.Seconds : (double)motion.Length * demand.Timing.ClipPhase.GetValueOrDefault();
            motion.Seek(startSeconds);
            if (resetEvents) motion.Seek(startSeconds);''')
sub('                Attack = attack,','                Attack = attack, ClipId = clipId, PlaybackId = demand.Playback?.PlaybackId ?? 0,')
write()
use('CharacterActionPlayback')
sub('        internal readonly CharacterActionHandoff ExplicitAttackTiming;', '''        internal readonly CharacterActionHandoff ExplicitAttackTiming;
        internal CharacterClipProgress? ResumeCursor; // 同一攻击实例与素材上次实际求值的秒数。''')
write()
use('CharacterAnimationResourceCatalog')
sub('private readonly HashSet<string> playableAttacks = new HashSet<string>(StringComparer.Ordinal);', 'private readonly Dictionary<string, int> playableAttacks = new Dictionary<string, int>(StringComparer.Ordinal);')
sub('playableAttacks.Contains(key)', 'playableAttacks.ContainsKey(key)')
sub('''            foreach (var entry in attackStates)
                if (oneShots.Contains(entry.Key) && MatchesAttackClip(entry.Key, attackTable?.Find(entry.Value)))
                    playableAttacks.Add(entry.Key);''','''            foreach (var entry in attackStates)
            {
                var definition = attackTable?.Find(entry.Value);
                if (oneShots.Contains(entry.Key) && MatchesAttackClip(entry.Key, definition))
                    playableAttacks.Add(entry.Key, definition.Clip.GetInstanceID());
            }''')
sub('        // 使用当前播放库验证作者声明','''        // 后台恢复只比较本批主线程冻结的片段身份。
        internal int AttackClipId(string key) => playableAttacks.TryGetValue(key, out int id) ? id : 0;

        // 使用当前播放库验证作者声明''')
write()
use('CharacterBehaviorAnimationBindings')
sub('        private int? version;', '''        private int? version;
        private int renderedStateVersion;
        private long renderedAttackInstance;
        private readonly Dictionary<int, CharacterClipProgress> attackCursors = new Dictionary<int, CharacterClipProgress>();''')
sub('        // 将动作键解析到对应的具名攻击','''        // 在图求值后记录本角色当前招式各素材的实际位置，替换招式时丢弃旧记录。
        internal void RecordAttackCursor(CharacterActionPlayback playback, in CharacterClipProgress sample)
        {
            if (playback.MoveInstanceId == 0 || playback.StateVersion != StateVersion ||
                sample.PlaybackId != playback.PlaybackId || sample.ClipId == 0 || sample.loop) return;
            if (renderedAttackInstance != playback.MoveInstanceId || renderedStateVersion != StateVersion)
            {
                attackCursors.Clear();
                renderedStateVersion = StateVersion;
                renderedAttackInstance = playback.MoveInstanceId;
            }
            attackCursors[sample.ClipId] = sample;
        }

        // 暂停只改变模拟代次；恢复必须仍是同一角色重置代次、招式实例和实际片段。
        internal CharacterClipProgress? ReadAttackCursor(in CharacterPlaybackOrigin origin, string key)
        {
            if (origin.StateVersion != renderedStateVersion || origin.Move != renderedAttackInstance) return null;
            return attackCursors.TryGetValue(resources.AttackClipId(key), out var cursor) ? cursor : (CharacterClipProgress?)null;
        }

        // 将动作键解析到对应的具名攻击''')
sub('        // 先撤回当前需求，再清除旧代观察供下一次重绑。','        // 暂停保留同一招式尚待求值的目标及素材游标，其余角色边界清空全部观察。')
sub('            foreach (var binding in bindings) binding.ResetObservation();', '''            bool pause = source == CharacterPlaybackEndSource.Paused;
            if (!pause) attackCursors.Clear();
            foreach (var binding in bindings)
            {
                if (pause && binding.Rule.IsAttackState) binding.Pulse.Rebind();
                else binding.ResetObservation();
            }''')
write()
use('CharacterAnimationPipeline');sub('modules.Player.FinishEvaluatedAttacks(plan, modules.Composer);','modules.Player.FinishEvaluatedAttacks(plan, modules.Bindings);');write()
use('CharacterAnimationPlayer')
sub('void FinishEvaluatedAttacks(in CharacterAnimationPlaybackPlan plan, CharacterAnimationComposer composer)','void FinishEvaluatedAttacks(in CharacterAnimationPlaybackPlan plan, CharacterBehaviorAnimationBindings bindings)')
for k in ('Overlay','LowerBody','UpperBody'):sub('FinishEvaluatedAttack(plan.'+k+', composer);','FinishEvaluatedAttack(plan.'+k+', bindings);')
sub('void FinishEvaluatedAttack(in CharacterAnimationLayerPlan layer, CharacterAnimationComposer composer)','void FinishEvaluatedAttack(in CharacterAnimationLayerPlan layer, CharacterBehaviorAnimationBindings bindings)')
sub('            if (timing.AttackElapsed >= timing.Handoff.Duration','''            var destination = observation.Destination;
            if (observation.Ready && destination.fullPathHash == layer.StateHash)
                bindings.RecordAttackCursor(demand.Playback, destination);
            var composer = bindings.Composer;
            if (timing.AttackElapsed >= timing.Handoff.Duration''')
write()

