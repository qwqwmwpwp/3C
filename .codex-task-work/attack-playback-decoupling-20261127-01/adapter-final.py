exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
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


