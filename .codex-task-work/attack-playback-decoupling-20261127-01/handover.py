exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterActionPlayback')
sub('        internal float InitialPhase;', '        internal float? EntryCoverage; // 仅在首次真实求值后锁定，图重建不重置正在执行的权重过渡。\n        internal float InitialPhase;')
write()
use('CharacterLocomotionPoseSelector')
sub('            if (!demand.HasHandoff) return demand;', '''            if (!demand.HasHandoff) return demand;
            if (demand.Timing.Sample.Valid && demand.Playback?.EntryCoverage is float captured)
                return demand.WithEntryCoverage(captured);''')
write()
use('CharacterAnimationPlayer')
sub('if (playback == null || playback.Status != CharacterActionPlaybackStatus.Started ||','if (playback == null || playback.Clock.Valid || playback.Status != CharacterActionPlaybackStatus.Started ||')
sub('if (playback == null || !playback.Clock.Valid || !playback.HasStarted) return;', 'if (playback == null || !playback.HasStarted) return;')
sub('''            if (observation.Ready && observation.Destination.fullPathHash == layer.StateHash)
                playback.RecordCursor(observation.Destination);''','''            if (observation.Ready && observation.Destination.fullPathHash == layer.StateHash)
            {
                playback.RecordCursor(observation.Destination);
                if (playback.Clock.Valid && playback.Cursor.HasValue) playback.EntryCoverage ??= layer.Demand.EntryCoverage;
            }''')
write()
use('CharacterAnimationComposer')
sub('if (playback.Clock.Valid && resources.OneShotClipId(demand.Key) == playback.ClipId) continue;', 'if (playback.ClipId != 0 && resources.OneShotClipId(demand.Key, playback.Clock.Valid) == playback.ClipId) continue;')
cut('        // 播放资源重绑时重建选中历史','        // 结束本角色当前')
sub('        // 冻结当前规划时刻；重复试选同一时刻只读取时钟。\n        internal double Time', '        internal double Time')
sub('        public void Commit(in CharacterAnimationComposition selected)', '        // 提交最终获选组合，并按普通动作原策略结束旧许可。\n        public void Commit(in CharacterAnimationComposition selected)')
write()
use('CharacterClipMixer')
sub('''            if (resetEvents && demand.Playback?.Cursor is CharacterClipProgress cursor)
                startSeconds = cursor.ClipId == clipId ? cursor.Seconds : (double)motion.Length * demand.Timing.ClipPhase.GetValueOrDefault();''','''            if (demand.Playback?.Cursor is CharacterClipProgress cursor && cursor.ClipId == clipId)
            {
                startSeconds = cursor.Seconds;
                resetEvents = true;
            }''')
s=s.replace('bool timed = demand.HasHandoff && demand.Timing.Sample.Valid;', 'bool timed = demand.Timing.Sample.Valid;')
write()
use('CharacterAnimationResourceCatalog')
sub('Dictionary<string, int> oneShotClips = new Dictionary<string, int>(StringComparer.Ordinal);','Dictionary<string, (int id, bool single)> oneShotClips = new Dictionary<string, (int, bool)>(StringComparer.Ordinal);')
sub('oneShotClips[pair.Key] = clips.StateClip(pair.Value.StateHash).GetInstanceID();','oneShotClips[pair.Key] = clips.OneShotIdentity(pair.Value.StateHash);')
sub('internal int OneShotClipId(string key) => key != null && oneShotClips.TryGetValue(key, out var id) ? id : 0;', '''internal int OneShotClipId(string key, bool singleOnly = false) => key != null && oneShotClips.TryGetValue(key, out var clip) &&
            (!singleOnly || clip.single) ? clip.id : 0;

        // 主线程在每次帧采集和显式调用时核对实际素材，后台只查本批身份和值。
        internal void CaptureOneShots()
        {
            foreach (string key in actionKeys)
            {
                if (validBindings.TryGetValue(key, out var binding) && clips.CanPlayOnce(binding.StateHash))
                {
                    oneShots.Add(key);
                    oneShotClips[key] = clips.OneShotIdentity(binding.StateHash);
                }
                else
                {
                    oneShots.Remove(key);
                    oneShotClips.Remove(key);
                }
            }
        }''')
s=s.replace('            ActionDelta = Mathf.Max(0f, dt);','            CaptureOneShots();')
s=s.replace('        internal float ActionDelta { get; private set; }\n','')
write()
use('CharacterClipPlayback')
sub('        public bool CanPlayOnce(int hash)', '''        // 在主线程冻结可播放资源的素材身份与单片段属性。
        internal (int id, bool single) OneShotIdentity(int hash) =>
            (states[hash].Clip.GetInstanceID(), !states[hash].Blend.Second);

        public bool CanPlayOnce(int hash)''')
write()
use('CharacterBehaviorAnimationBindings')
sub('resources.OneShotClipId(key) != clipId','resources.OneShotClipId(key, true) != clipId')
sub('            if (!resources.CanPlayOnce(key)) return null;', '            resources.CaptureOneShots();\n            if (!resources.CanPlayOnce(key)) return null;')
sub('CharacterPlaybackStartSource.ExplicitRequest);', 'CharacterPlaybackStartSource.ExplicitRequest, clipId: resources.OneShotClipId(key));')
sub('        // 按冻结的执行身份提交非循环','''        // 判断资源绑定是否仍指向这个逻辑实例的原素材。
        internal bool SameClip(CharacterActionPlayback playback) => playback != null && playback.ClipId != 0 &&
            resources.OneShotClipId(playback.Key) == playback.ClipId;

        // 按冻结的执行身份提交非循环''')
sub('Composer.PlayOnce(rule.EntryAction, rule.Weight, origin, source) : null;', 'Composer.PlayOnce(rule.EntryAction, rule.Weight, origin, source, clipId: resources.OneShotClipId(rule.EntryAction)) : null;')
write()
use('CharacterGrantedAnimationState')
sub('            if (currentGrant == 0) return;\n            if (entry?.Status','            if (currentGrant == 0 || animations.SameClip(entry)) return;\n            if (entry?.Status')
write()
use('CharacterPulseAnimationState')
sub('            resumeTimed = false;\n            resumedEntry = source', '''            resumeTimed = false;
            if (source == CharacterPlaybackStartSource.Rebind && animations.SameClip(entry) &&
                (entry.Status == CharacterActionPlaybackStatus.Started || entry.Status == CharacterActionPlaybackStatus.Pending)) return;
            resumedEntry = source''')
sub('            if (observedActive && timedActive && (entry == null', '            if (!animations.SameClip(entry) && observedActive && timedActive && (entry == null');write()
use('CharacterAnimationPipeline')
sub('                modules.Bindings.UpdateGrants(validGrant, guardGrant);\n                modules.Bindings.SuspendPulses(CharacterPlaybackEndSource.ResourceUnavailable);\n','')
sub('                    modules.Bindings.RebindGrantedEntries();\n                    modules.Composer.RebindPlayback(modules.Resources);\n','')
sub('                if (!ready) modules.Bindings.SuspendPulses(CharacterPlaybackEndSource.ResourceUnavailable);\n','')
sub('            modules.Resources.CaptureWeights(deltaTime);','            modules.Resources.CaptureWeights(deltaTime);\n            modules.Composer.RetireChangedClips(modules.Resources);')
sub('            modules.Reconfigure(config, changes, seed);','            modules.Composer.SetTime(seed.Time);\n            modules.Reconfigure(config, changes, seed);')
write()
use('CharacterAnimationComposer')
sub('''        internal void RebindPlayback(CharacterAnimationResourceCatalog resources)
        {
            for''','''        internal void RebindPlayback(CharacterAnimationResourceCatalog resources)
        {
            RetireChangedClips(resources);
            previousSelection = default;
        }

        // 已激活的请求保留冻结参数，只有实际绑定到不同素材时才撤回。
        internal void RetireChangedClips(CharacterAnimationResourceCatalog resources)
        {
            for''')
sub('''            previousSelection = default;
        }

        // 以绝对角色时刻''','''        }

        // 以绝对角色时刻''')
write()

