exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
for file in ('CharacterAnimationComposer','CharacterAnimationIntent','CharacterBodyPlaybackGraph','CharacterClipPlayback','CharacterLayerBlendWeight','CharacterLocomotionPoseSelector','CharacterAnimationPlayer'):
 use(file);s=s.replace('Timing.SamplePhase','Timing.WeightPhase').replace('timing.SamplePhase','timing.WeightPhase').replace('Timing.StartPhase','Timing.StartClipPhase');write()
use('CharacterAnimationComposer')
sub('        public void Sample(CharacterActionPlayback playback, float? firstPhase, float phase, CharacterActionHandoff handoff,\n            bool startAtZero, bool resumed)','''        // 保留已下发的素材入点，分别提交本帧状态权重和驱动素材的攻击经过秒数。
        public void Sample(CharacterActionPlayback playback, float? firstPhase, float phase, CharacterActionHandoff handoff,
            bool startAtZero, bool resumed, double attackElapsed = 0d)''')
sub('new CharacterActionTiming(start, phase, handoff, startAtZero, resumed)','new CharacterActionTiming(start, phase, handoff, startAtZero, resumed, attackElapsed: attackElapsed)')
write()
use('CharacterLocomotionPoseSelector')
sub('timing.Handoff.Duration * Mathf.Max(0f, timing.Handoff.FullFrom - start)','Mathf.Max(0f, timing.Handoff.Duration * timing.Handoff.FullFrom - (float)timing.AttackElapsed)')
sub('''                float before = continuing ? previous.Demand.Timing.WeightPhase.GetValueOrDefault() : 0f;
                float phase = Mathf.Clamp01(before + resources.ActionDelta / attack.Duration);
                demand = demand.WithTiming(new CharacterActionTiming(0f, phase, attack, false, false));''','''                double before = continuing ? previous.Demand.Timing.AttackElapsed : 0d;
                double elapsed = System.Math.Min(attack.Duration, before + resources.ActionDelta);
                float weightPhase = Mathf.Clamp01((float)(elapsed / attack.Duration));
                demand = demand.WithTiming(new CharacterActionTiming(0f, weightPhase, attack, false, false, attackElapsed: elapsed));''')
write()
use('CharacterClipPlayback');sub('            mixer.ClockPhase = plan.Demand.Timing.ClockPhase;\n','');sub('mixer.Advance(dt, this, frame);','mixer.Advance(dt, this, frame, plan.Demand.Timing);');write()
use('CharacterClipMixer')
sub('WeightSpeed, LogicalRate, AttackDuration;', 'WeightSpeed, LogicalRate;\n            public bool Attack;')
sub('private float attackDuration, attackEntryCoverage;', 'private float attackEntryCoverage;')
sub('        public float? ClockPhase { get; set; }\n','')
sub('            attackDuration = demand.HasHandoff && demand.Timing.Handoff.IsAttack ? demand.Timing.Handoff.Duration : 0f;','            bool attack = demand.HasHandoff && demand.Timing.Handoff.IsAttack;')
sub('if (attackDuration > 0f && fadeTime > 0f)\n                fadeTime = attackDuration * demand.Timing.Handoff.FullFrom;', 'if (attack && fadeTime > 0f)\n                fadeTime = demand.Timing.Handoff.Duration * demand.Timing.Handoff.FullFrom;')
sub('smoothFade = attackDuration > 0f ||','smoothFade = attack ||');sub('AttackDuration = attackDuration,','Attack = attack,')
sub('            ClockPhase = null;\n','')
sub('        // 按各声部自身的步态和状态倍率推进逻辑时钟，包括尚未淡出的声部。','        // 攻击素材跟随独立片段相位，进入权重跟随状态秒数；已中断的旧攻击只贡献最后姿势。')
sub('public void Advance(float dt, CharacterClipPlayback playback, in CharacterAnimationPlaybackPlan frame)','public void Advance(float dt, CharacterClipPlayback playback, in CharacterAnimationPlaybackPlan frame, in CharacterActionTiming timing)')
sub('bool attackBlend = attackDuration > 0f && ClockPhase.HasValue;\n            elapsed = attackBlend ? ClockPhase.Value * attackDuration : elapsed + dt;','bool attackBlend = timing.Handoff.IsAttack;\n            elapsed = attackBlend ? (float)timing.AttackElapsed : elapsed + dt;')
sub('float rate = voice.AttackDuration > 0f ? voice.Motion.Length / voice.AttackDuration : playback.Pace(voice.Hash).Rate(frame.StrideSpeed, frame.StartSpeed, frame.StopRate, out _);','double rate = voice.Attack ? 0d : playback.Pace(voice.Hash).Rate(frame.StrideSpeed, frame.StartSpeed, frame.StopRate, out _);')
sub('if (voice == target && ClockPhase.HasValue && dt > 0f)','if (voice == target && timing.ClipPhase.HasValue && dt > 0f)')
sub('double seconds = (double)ClockPhase.Value * voice.Motion.Length;\n                    rate = (float)(System.Math.Max(0d, seconds - voice.Clip.GetTime()) / dt);','double seconds = (double)timing.ClipPhase.Value * voice.Motion.Length;\n                    rate = System.Math.Max(0d, seconds - voice.Clip.GetTime()) / dt;')
sub('voice.LogicalRate = rate;','voice.LogicalRate = (float)rate;')
write()
use('CharacterPulseAnimationState')
sub('private float attackPhase, attackEntryPhase;', '''private float attackEntryClipPhase;
        private float? lastRenderedAttackPhase;
        private double attackElapsed;''')
sub('            timedActive = matches && active;','            timedActive = matches && active && step.Attack.Handoff.IsAttack && step.Attack.ElapsedSeconds < step.Attack.Handoff.AnimationDuration;')
sub('                attackPhase = 0f;\n                attackEntryPhase = step.Attack.StartProgress;', '''                lastRenderedAttackPhase = null;
                attackEntryClipPhase = step.Attack.Handoff.IsAttack ? step.Attack.Handoff.ClipProgress(step.Attack.StartElapsed) : 0f;''')
sub('            handoff = step.Attack.Handoff;', '            handoff = step.Attack.Handoff;\n            attackElapsed = step.Attack.ElapsedSeconds;')
sub('                resumeTimed = true;\n            }\n            else if (newInstance', '                resumeTimed = timedActive;\n            }\n            else if (newInstance')
sub('            attackPhase = Mathf.Max(attackPhase, Mathf.Clamp01(entry.LastObservedPhase));\n            if (attackPhase == 0f) attackPhase = 0.000001f; // 首帧事件已交付，恢复点越过零时刻。','''            lastRenderedAttackPhase = Mathf.Max(0.000001f, Mathf.Clamp01(entry.LastObservedPhase)); // 已发过零点事件。''')
sub('            entryAttackInstance = origin.Move;\n            animations.TryPlayEntry','''            if (binding.Rule.IsAttackState && origin.Move == attackInstance &&
                (!handoff.Valid || resumedEntry && !timedActive))
            {
                End(CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.BehaviorWindowEnded);
                return;
            }
            entryAttackInstance = origin.Move;
            animations.TryPlayEntry''')
sub('''                if (attackInstance != 0) attackPhase = Mathf.Max(attackPhase, phase);
                float? start = resumedEntry ? attackPhase : attackInstance != 0 ? attackEntryPhase : firstPose;
                animations.Composer.Sample(entry, start, phase, handoff,
                    binding.HandoffSettings?.StartAtZero == true, resumedEntry);''','''                float? start = firstPose;
                if (handoff.IsAttack)
                    start = resumedEntry ? lastRenderedAttackPhase ?? handoff.ClipProgress(attackElapsed) : attackEntryClipPhase;
                animations.Composer.Sample(entry, start, phase, handoff,
                    binding.HandoffSettings?.StartAtZero == true, resumedEntry, attackElapsed);''')
sub('            attackInstance = entryAttackInstance = 0;','            attackInstance = entryAttackInstance = 0;\n            lastRenderedAttackPhase = null;')
write()
use('CharacterAnimationPlayer')
cut('        // 只有已下发的同一实例', '        // 仅给实际下发','''        // 记录实际素材游标并确认自然完成；状态先结束时以时间窗撤回剩余片段。
        private void FinishEvaluatedAttack(in CharacterAnimationLayerPlan layer, CharacterAnimationComposer composer)
        {
            var demand = layer.Demand;
            var timing = demand.Timing;
            if (!timing.Handoff.IsAttack || demand.Playback?.Status != CharacterActionPlaybackStatus.Started) return;
            var observation = clips.Observe(demand.Source);
            if (timing.AttackElapsed >= timing.Handoff.Duration && timing.AttackElapsed < timing.Handoff.AnimationDuration)
            {
                composer.End(demand.Key, demand.InstanceId, CharacterActionPlaybackStatus.Withdrawn,
                    CharacterPlaybackEndSource.BehaviorWindowEnded);
                return;
            }
            InspectOneShot(layer, observation, demand, composer);
        }

''')
write()

