exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterBehaviorFlow')
sub('ReleaseWithdrawn(context, ref step);','ReleaseBeforeRequests(context, ref step);')
cut('        // 在处理新请求前释放已撤销','        // 按已装配的优先顺序','''        // 新请求仲裁前释放已撤销的许可，并让已到自然终点的招式优先尝试自身接续。
        private void ReleaseBeforeRequests(CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            foreach (var rule in rules.Actions)
            {
                if (DispatchCancelled) return;
                if (!step.IsActive(rule.Id)) continue;
                bool keep = rule.Behavior.ShouldContinue(context);
                if (DispatchCancelled) return;
                if (!keep)
                {
                    step.Withdrawn |= rule.Id;
                    step.End(rule, CharacterBehaviorEndReason.Withdrawn);
                }
                else if (rule.Behavior is CharacterAttackBehavior attack && attack.IsComplete &&
                    !attack.TryContinueAtEndpoint(context) && !DispatchCancelled)
                {
                    // 先冻结旧招末相位；若同刻有新首招，本步执行结果将发布新实例。
                    attack.ReadOutcome(ref step);
                    step.End(rule);
                }
            }
        }

''')
write()
use('CharacterAttackCombo')
sub('private void ReadLinks(CharacterBehaviorContext context)','private void ReadLinks(CharacterBehaviorContext context, bool allowNewOpening = false)')
sub('                long request = Journal?.NewRequest() ?? 0;\n                var target = SelectInput','                var target = SelectInput')
sub('                if (reason != default)\n                {\n                    bufferResult', '''                // 终点处未成为接续的输入仍可作为新首招，交给外层裁决记录唯一结果。
                if (allowNewOpening && reason != default) continue;
                long request = Journal?.NewRequest() ?? 0;
                if (reason != default)
                {
                    bufferResult''')
sub('        // 释放尚未执行的预输入','''        // 优先执行已完成招式的端点接续，无可进入的目标时把本批按键留作新请求。
        internal bool TryContinueAtEndpoint(CharacterBehaviorContext context)
        {
            startProgress = current.Progress;
            moveStarted = false;
            var endpoint = At(context, 0d, 0f);
            ReadLinks(endpoint, allowNewOpening: true);
            current.Advance(endpoint, current.Elapsed);
            if (!TrySwitch(At(endpoint, 0d, 0f, true))) return false;
            // 本批按键已交给原招，新实例本步只接收持续输入。
            firstStep = true;
            return true;
        }

        // 释放尚未执行的预输入''')
write()
use('CharacterAnimationIntent');lines('Func<CharacterAttackDefinition> ReadAttack;', 'ReadAttack = readAttack;');sub('CharacterActionHandoffSettings handoff = null,\n            Func<CharacterAttackDefinition> readAttack = null)','CharacterActionHandoffSettings handoff = null)');write()
use('CharacterAnimationResourceCatalog')
sub('        private readonly HashSet<string> oneShots = new HashSet<string>();','''        private readonly HashSet<string> oneShots = new HashSet<string>();
        private readonly Dictionary<string, int> attackStates = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> playableAttacks = new HashSet<string>(StringComparer.Ordinal);
        private CharacterAttackTable attackTable; // 作者数据仅在主线程绑定、捕获或显式播放时读取。''')
sub('            externalKeys.Clear();\n            AddActions','            externalKeys.Clear();\n            attackStates.Clear();\n            AddActions')
sub('            if (config.Actions == null) return;', '            attackTable = config.AttackTable;\n            if (config.Actions == null) return;')
sub('                Func<CharacterAttackDefinition> readAttack = attackState == 0 ? null :\n                    () => config.AttackTable?.Find(attackState);','                if (attackState != 0) attackStates.Add(action.Key, attackState);')
sub('action.Partners, handoff, readAttack','action.Partners, handoff')
sub('            oneShots.Clear();','            oneShots.Clear();\n            playableAttacks.Clear();')
sub('            foreach (var value in validVariants.Values) CacheLength(value.StateHash);','            CaptureAttackClips();\n            foreach (var value in validVariants.Values) CacheLength(value.StateHash);')
cut('        // 在主线程核对一次播放可用性','        public bool HasBinding','''        // 后台规划只读主线程已核对的单次动作与攻击片段匹配结果。
        public bool CanPlayOnce(string key) => CanSubmit(key) && oneShots.Contains(key) &&
            (!attackStates.ContainsKey(key) || playableAttacks.Contains(key));

        // 独立播放在主线程请求时重新核对片段，并冻结调用者此刻配置的三段时钟。
        internal bool TryCaptureExplicitTiming(string key, out CharacterActionHandoff timing)
        {
            timing = default;
            if (!CanSubmit(key) || !oneShots.Contains(key)) return false;
            if (!attackStates.TryGetValue(key, out int id)) return true;
            var definition = attackTable?.Find(id);
            if (!MatchesAttackClip(key, definition)) return false;
            timing = definition.AttackTiming;
            return timing.Valid;
        }

        // 在主线程捕获本批可用的攻击动作键，之后的规划不再读取 Unity 资源。
        private void CaptureAttackClips()
        {
            playableAttacks.Clear();
            foreach (var entry in attackStates)
                if (oneShots.Contains(entry.Key) && MatchesAttackClip(entry.Key, attackTable?.Find(entry.Value)))
                    playableAttacks.Add(entry.Key);
        }

        // 使用当前播放库验证作者声明的非循环单片段，只在主线程调用。
        private bool MatchesAttackClip(string key, CharacterAttackDefinition definition) =>
            definition?.Clip && clips.MatchesAttackClip(validBindings[key].StateHash, definition.Clip);

''')
sub('            ActionDelta = Mathf.Max(0f, dt);','            CaptureAttackClips();\n            ActionDelta = Mathf.Max(0f, dt);')
write()
use('CharacterAnimationComposer')
sub('CharacterPlaybackOrigin origin, CharacterPlaybackStartSource source)\n        {\n            long id','CharacterPlaybackOrigin origin, CharacterPlaybackStartSource source,\n            CharacterActionHandoff explicitTiming = default)\n        {\n            long id')
cut('            var timing = default(CharacterActionHandoff);','            if (!Submit(', '            var playback = new CharacterActionPlayback(key, id, origin, source, explicitTiming);\n')
write()
use('CharacterAnimationPipeline');sub('SynchronizeResources() && modules.Resources.CanPlayOnce(key)\n                ? modules.Composer.PlayOnce(key, weight, origin, CharacterPlaybackStartSource.ExplicitRequest)', 'SynchronizeResources() && modules.Resources.TryCaptureExplicitTiming(key, out var timing)\n                ? modules.Composer.PlayOnce(key, weight, origin, CharacterPlaybackStartSource.ExplicitRequest, timing)')
sub('            modules.Player.EvaluatePose();\n#if UNITY_EDITOR\n            if (clearMotionSources) return; // 片段事件已切换到新的运动基线。','''            modules.Player.EvaluatePose();
            if (clearMotionSources) return; // 片段事件已切换到新的运动基线。
            modules.Player.FinishEvaluatedAttacks(plan, modules.Composer);
#if UNITY_EDITOR''')
write()
use('CharacterPulseAnimationState')
cut('            long instance = matches && active', '            firstPose = step.Attack.StartProgress;', '''            timedActive = matches && active;
            if (!matches)
            {
                if (step.AttackMoveStarted)
                {
                    resumeTimed = false;
                    if (!seed) animations.QueueEdge(binding, CharacterActionPlaybackStatus.Withdrawn);
                }
                return;
            }
            long instance = step.Attack.InstanceId;
            bool newInstance = instance != 0 && attackInstance != instance;
            if (newInstance)
            {
                attackPhase = 0f;
                attackEntryPhase = step.Attack.StartProgress;
            }
            attackInstance = instance;
''')
sub('                if (!seed) animations.QueueEdge(binding, CharacterActionPlaybackStatus.Completed);\n            }\n            else if (reconnect && !step.AttackMoveStarted)','''                // 自然结束保留最后的采样需求，图求值后才能确认片段完成；未获选实例由 ReleaseExpired 撤回。
            }
            else if (reconnect && !step.AttackMoveStarted)''')
sub('            if (entry != null && firstPose.HasValue && (attackInstance != 0 || duration > 0f))','''            if (binding.Rule.IsAttackState && (attackInstance == 0 || entryAttackInstance != attackInstance)) return;
            if (entry != null && firstPose.HasValue && (attackInstance != 0 || duration > 0f))''')
write()
use('CharacterAnimationPlayer')
sub('        // 仅给实际下发播放指令','''        // 在片段图求值后，以本帧实际获选的攻击声部确认自然终点。
        internal void FinishEvaluatedAttacks(in CharacterAnimationPlaybackPlan plan, CharacterAnimationComposer composer)
        {
            FinishEvaluatedAttack(plan.Overlay, composer);
            FinishEvaluatedAttack(plan.LowerBody, composer);
            FinishEvaluatedAttack(plan.UpperBody, composer);
        }

        // 只有已下发的同一实例和真正到达素材终点的播放观测才能产生完成回执。
        private void FinishEvaluatedAttack(in CharacterAnimationLayerPlan layer, CharacterAnimationComposer composer)
        {
            var demand = layer.Demand;
            if (!demand.Timing.Handoff.IsAttack || !(demand.Timing.SamplePhase >= 1f) ||
                demand.Playback?.Status != CharacterActionPlaybackStatus.Started) return;
            InspectOneShot(layer, clips.Observe(demand.Source), demand, composer);
        }

        // 仅给实际下发播放指令''')
write()

