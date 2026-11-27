exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterBehaviorConfig');sub('80, allowGuard ? CharacterBehaviorId.Guard : default,','80, CharacterBehaviorId.Dash | (allowGuard ? CharacterBehaviorId.Guard : default),');write()
use('CharacterMovementForecast');sub('(Behaviors & (CharacterBehaviorId.Dash | CharacterBehaviorId.Attack))','(Behaviors & CharacterBehaviorId.Dash)');write()
use('CharacterActionHandoff')
sub('public readonly bool UsesRouteBlend;', 'public readonly bool IsAttack;')
sub('UsesRouteBlend = false;', 'IsAttack = false;')
cut('        internal CharacterActionHandoff(float duration, Vector2','        public CharacterBehaviorPhase','''        // 从状态三段时钟构造动画覆盖区间，不依赖任何路线或移动许可。
        internal static CharacterActionHandoff ForAttack(float duration, float fullFrom, float releaseFrom) =>
            new CharacterActionHandoff(duration, fullFrom, releaseFrom);

        // 攻击全程使用角色时钟，后摇开始时直接淡出。
        private CharacterActionHandoff(float duration, float fullFrom, float releaseFrom)
            : this(duration, fullFrom, releaseFrom, 0f, null) => IsAttack = true;

''')
sub('if (UsesRouteBlend || !MoveStart.HasValue','if (!MoveStart.HasValue')
sub('关闭时沿用招式阶段，普通单次动作沿用原播放方式。自定义覆盖区间会包含行为声明的关键出招段。','为普通单次动作设置覆盖区间。具名攻击直接使用自身的三段时钟。')
write()
use('CharacterBehaviorAnimationMapping')
cut('            if (BehaviorId == CharacterBehaviorId.Attack && AttackStateId != 0)','            if (settings?.OverridePhases', '            if (IsAttackState) return false;\n')
sub('        internal bool HasValidTrigger','        internal bool IsAttackState => BehaviorId == CharacterBehaviorId.Attack && Trigger == CharacterAnimationTrigger.OnStart && AttackStateId != 0;\n        internal bool HasValidTrigger')
sub('判断提供本物理步运动的招式是否仍在保留原步态的前摇阶段。','仅按非攻击动作的配置判断是否在进入阶段保留原步态。')
write()
use('CharacterBehaviorAnimationBindings')
sub('var settings = rule.Handoff;', 'var settings = rule.IsAttackState ? null : rule.Handoff;')
sub('if (settings?.OverridePhases != true && rule.EntryAction != null','if (!rule.IsAttackState && settings?.OverridePhases != true && rule.EntryAction != null')
cut('        // 独立调用同一动作键','        // 按该物理步','''        // 将动作键解析到对应的具名攻击，独立播放在请求时从该字段冻结时钟。
        internal static int FindAttackState(string key, CharacterAnimationConfiguration config)
        {
            if (config.BehaviorAnimations != null)
                foreach (var mapping in config.BehaviorAnimations)
                    if (mapping?.IsAttackState == true && mapping.EntryAction == key) return mapping.AttackStateId;
            return 0;
        }

''')
cut('        // 按状态公开的交接','        // 按行为序列')
write()
use('CharacterAnimationIntent')
sub('        public CharacterActionHandoffSettings Handoff { get; }','''        public CharacterActionHandoffSettings Handoff { get; }
        internal readonly Func<CharacterActionHandoff> ReadAttackTiming; // 仅在主线程接受独立播放时读取。''')
sub('CharacterActionHandoffSettings handoff = null)','CharacterActionHandoffSettings handoff = null,\n            Func<CharacterActionHandoff> readAttackTiming = null)')
sub('            Handoff = handoff?.Snapshot();','            Handoff = handoff?.Snapshot();\n            ReadAttackTiming = readAttackTiming;')
write()
use('CharacterAnimationResourceCatalog')
sub('        private float blendDelta;', '        private float blendDelta;\n        internal float ActionDelta { get; private set; }')
sub('            blendDelta = clips.LayerBlendDelta(dt);','            ActionDelta = Mathf.Max(0f, dt);\n            blendDelta = clips.LayerBlendDelta(dt);')
sub('                var handoff = CharacterBehaviorAnimationBindings.ResolveHandoff(action, config);', '''                int attackState = CharacterBehaviorAnimationBindings.FindAttackState(action.Key, config);
                Func<CharacterActionHandoff> readTiming = attackState == 0 ? null :
                    () => config.AttackTable?.Find(attackState)?.AttackTiming ?? default;
                var handoff = attackState == 0 && action.Handoff?.OverridePhases == true ? action.Handoff : null;''')
sub('action.Partners, handoff));','action.Partners, handoff, readTiming));')
write()
use('AnimationModuleChanges')
sub('(string action, string handoff)', '(string action, CharacterActionHandoff attack)')
sub('new Dictionary<string, (string, string)>','new Dictionary<string, (string, CharacterActionHandoff)>')
sub('var handoff = CharacterBehaviorAnimationBindings.ResolveHandoff(action, config);\n                Actions.Add(action.Key, (JsonUtility.ToJson(action), handoff == null ? null : JsonUtility.ToJson(handoff)));','''int state = CharacterBehaviorAnimationBindings.FindAttackState(action.Key, config);
                var timing = config.AttackTable?.Find(state)?.AttackTiming ?? default;
                Actions.Add(action.Key, (JsonUtility.ToJson(action), timing));''')
write()
use('CharacterActionPlayback');sub('        internal CharacterPlaybackOrigin Origin { get; }','        internal CharacterPlaybackOrigin Origin { get; }\n        internal readonly CharacterActionHandoff ExplicitAttackTiming;');sub('CharacterPlaybackStartSource startSource)\n','CharacterPlaybackStartSource startSource, CharacterActionHandoff explicitAttackTiming = default)\n');sub('            StartSource = startSource;','            StartSource = startSource;\n            ExplicitAttackTiming = explicitAttackTiming;');write()
use('CharacterAnimationComposer');sub('            var playback = new CharacterActionPlayback(key, id, origin, source);','''            var timing = default(CharacterActionHandoff);
            if (source == CharacterPlaybackStartSource.ExplicitRequest && rules.TryGetValue(key, out var rule) && rule.ReadAttackTiming != null)
            {
                timing = rule.ReadAttackTiming();
                if (!timing.Valid) return null;
            }
            var playback = new CharacterActionPlayback(key, id, origin, source, timing);''');write()
use('CharacterAnimationPipeline');lines('bool returning = modules.Bindings.IsReturningToMovement');sub('committedPlan, returning, modules.Intent','committedPlan, modules.Intent');write()
use('CharacterPulseAnimationState');cut('        // 只有行为和播放句柄','        // 按单步先结束');sub('SetHandoff(step.Attack.Handoff, step.MoveApproved, reconnect || newInstance);','handoff = step.Attack.Handoff;');sub('SetHandoff(default, step.MoveApproved, entering);','SetConfiguredHandoff(step.MoveApproved, entering);')
cut('        // 将本次行为相位和移动许可','        // 收到开始边沿','''        // 为自定义交接的非攻击动作记录首次获准移动时刻。
        private void SetConfiguredHandoff(bool moveApproved, bool entering)
        {
            var settings = binding.HandoffSettings;
            if (entering) moveStart = null;
            if (moveApproved && !moveStart.HasValue && lastPose >= settings.ReleaseFrom)
                moveStart = Mathf.Max(firstPose.GetValueOrDefault(), settings.ReleaseFrom);
            handoff = settings.Resolve(duration, moveStart: moveStart);
        }

''')
sub('!handoff.UsesRouteBlend && binding.HandoffSettings?.StartAtZero == true','binding.HandoffSettings?.StartAtZero == true')
write()
use('CharacterLocomotionPoseSelector')
sub('bool attackReturning, bool useMovingVariant','bool useMovingVariant')
sub('bool matchRecovery = attackReturning &&','bool matchRecovery = timing.Handoff.IsAttack &&')
sub('overlay.HasHandoff && timing.Handoff.MoveStart.HasValue && timing.SamplePhase >= timing.Handoff.ReleaseFrom;', 'timing.SamplePhase >= timing.Handoff.ReleaseFrom;')
sub('bool routeBlend = demand.HasHandoff && timing.Handoff.UsesRouteBlend;', 'bool attackBlend = demand.HasHandoff && timing.Handoff.IsAttack;');sub('routeBlend','attackBlend')
sub('float? requestedStart = demand.StartNormalizedTime ?? demand.Timing.StartPhase;', '''float? requestedStart = demand.StartNormalizedTime ?? demand.Timing.StartPhase;
            if (demand.Playback?.ExplicitAttackTiming.Valid == true) requestedStart = 0f;''')
sub('            if (demand.Playback != null && !demand.Timing.SamplePhase.HasValue && settings != null)','''            var attack = demand.Playback?.ExplicitAttackTiming ?? default;
            if (attack.Valid)
            {
                // 从上次实际提交的相位推算本帧终点，试选与重复规划都不会重复推进。
                float before = continuing ? previous.Demand.Timing.SamplePhase.GetValueOrDefault() : 0f;
                float phase = Mathf.Clamp01(before + resources.ActionDelta / attack.Duration);
                demand = demand.WithTiming(new CharacterActionTiming(0f, phase, attack, false, false));
            }
            else if (demand.Playback != null && !demand.Timing.SamplePhase.HasValue && settings != null)''')
write()
use('CharacterClipMixer');sub('routeClockDuration','attackDuration');sub('routeEntryCoverage','attackEntryCoverage');sub('routeBlend','attackBlend');sub('UsesRouteBlend','IsAttack')
sub('public float StartWeight, StartWeightSpeed, Weight, WeightSpeed, LogicalRate;','public float StartWeight, StartWeightSpeed, Weight, WeightSpeed, LogicalRate, AttackDuration;')
sub('State = state, Motion = motion, Hash = hash, Port = port,','State = state, Motion = motion, Hash = hash, Port = port,\n                AttackDuration = attackDuration,')
sub('float rate = playback.Pace(voice.Hash).Rate','float rate = voice.AttackDuration > 0f ? voice.Motion.Length / voice.AttackDuration : playback.Pace(voice.Hash).Rate')
write()
use('CharacterLayerBlendWeight');sub('!handoff.UsesRouteBlend','!handoff.IsAttack');write()

