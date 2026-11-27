exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterPulseAnimationState')
cut('        private long attackInstance','        // 将瞬发状态')
cut('            if (id == CharacterBehaviorId.Attack','            if (binding.HandoffSettings')
cut('        // 招式起止相位','        // 用当帧行为时钟')
cut('        // 恢复窗口内的播放','        // 为自定义交接')
a=s.index('            resumedEntry = source ==');b=s.index('        // 在资源重绑前',a)
s=s[:a]+'''            resumedEntry = source == CharacterPlaybackStartSource.Rebind;
            animations.TryPlayEntry(binding, origin, source, out entry);
        }

        // 为旧式单次映射交付行为相位与移动交接。
        internal void Submit()
        {
            if (resumeTimed && observedActive) Start(binding.Origin, CharacterPlaybackStartSource.Rebind);
            resumeTimed = false;
            if (entry != null && firstPose.HasValue && duration > 0f)
                animations.Composer.Sample(entry, firstPose, Mathf.Clamp01(lastPose), handoff,
                    binding.HandoffSettings?.StartAtZero == true, resumedEntry);
        }

'''+s[b:]
sub('if (CanRecover() &&','if (observedActive && timedActive &&');sub('resumeTimed = CanRecover();','resumeTimed = timedActive;')
s=''.join(l for l in s.splitlines(True) if not any(k in l for k in ['entryAttackInstance','attackOrigin = default;']))
write()
use('CharacterActionPlayback');sub('        // // 同一攻击实例与素材上次实际求值的秒数。','        internal float InitialPhase; // 仅用于中途装配时跳过请求之前的事件。');write()
use('CharacterActionHandoff');sub('internal CharacterActionTiming(CharacterPlaybackSample sample, CharacterClipProgress? cursor)', 'internal CharacterActionTiming(CharacterPlaybackSample sample, CharacterClipProgress? cursor, float initialPhase)')
sub('StartClipPhase = cursor?.normalizedTime ?? 0f;\n            Resumed = cursor.HasValue;', 'StartClipPhase = cursor?.normalizedTime ?? initialPhase;\n            Resumed = cursor.HasValue || initialPhase > 0f;');write()
use('CharacterAnimationComposer');sub('        internal void SetTime(double now)', '        internal double Time => presentationTime;\n\n        // 更新规划与显式请求共用的角色时刻。\n        internal void SetTime(double now)')
sub('new CharacterActionTiming(clock.Sample(presentationTime), demand.Playback.Cursor)', 'new CharacterActionTiming(clock.Sample(presentationTime), demand.Playback.Cursor, demand.Playback.InitialPhase)')
write()
use('CharacterClipMixer');sub('demand.Playback?.ResumeCursor','demand.Playback?.Cursor');write()
use('CharacterAnimationModules');sub('new CharacterBehaviorAnimationBindings(config.BehaviorAnimations,','new CharacterBehaviorAnimationBindings(config,');sub('Bindings.Reconfigure(next.BehaviorAnimations,','Bindings.Reconfigure(next,');write()
use('AnimationModuleChanges')
sub('Dictionary<string, (string action, CharacterActionHandoff attack)>','Dictionary<string, string>');sub('Dictionary<string, (string, CharacterActionHandoff)>','Dictionary<string, string>')
cut('                int state = CharacterBehaviorAnimationBindings.FindAttackState','            }','                Actions.Add(action.Key, JsonUtility.ToJson(action));\n');write()
use('CharacterAnimationPipeline')
sub('modules.Bindings.SuspendPulses(CharacterPlaybackEndSource.ResourceUnavailable);','modules.Bindings.SuspendPulses(CharacterPlaybackEndSource.ResourceUnavailable);')
sub('                    modules.Composer.ResetSelection(CharacterPlaybackEndSource.ResourceUnavailable);','                    modules.Composer.RebindPlayback(modules.Resources);')
sub('                modules.Bindings.UpdateGrants(validGrant, guardGrant);\n                modules.Bindings.SuspendPulses(ready ? CharacterPlaybackEndSource.Paused : CharacterPlaybackEndSource.ResourceUnavailable);','''                if (!ready) modules.Bindings.SuspendPulses(CharacterPlaybackEndSource.ResourceUnavailable);''')
sub('            modules.Resources.CaptureWeights(deltaTime);','            modules.Composer.SetTime(now);\n            modules.Resources.CaptureWeights(deltaTime);')
sub('''                modules.Composer.ResetSelection(modules.Resources.Ready
                    ? CharacterPlaybackEndSource.Rebind : CharacterPlaybackEndSource.ResourceUnavailable);''','''                if (modules.Resources.Ready) modules.Composer.RebindPlayback(modules.Resources);''')
sub('        public CharacterActionPlayback PlayOnce(string key, float weight, CharacterControllerFrameResult latestFrame,\n            int currentStateVersion, int simulationVersion)', '        public CharacterActionPlayback PlayOnce(string key, float weight, CharacterControllerFrameResult latestFrame,\n            int currentStateVersion, int simulationVersion, double now)')
cut('            var result = SynchronizeResources() && modules.Resources.TryCaptureExplicitTiming','            batch.RequestSelection','''            modules.Composer.SetTime(now);
            var result = SynchronizeResources() ? modules.Bindings.PlayExplicit(key, weight, origin, now) : null;
''')
write()
use('CharacterBehaviorAnimator');sub('pipeline.PlayOnce(semanticKey, weight, LatestFrame, StateVersion, SimulationVersion)','pipeline.PlayOnce(semanticKey, weight, LatestFrame, StateVersion, SimulationVersion, driver ? driver.CharacterTime : external.Time)');write()


