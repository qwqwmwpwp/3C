exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterAttackDefinition');sub('        [InspectorName("显示名称")]', '        [InspectorName("攻击片段")] public AnimationClip Clip;\n        [InspectorName("显示名称")]');write()
use('CharacterLightAttackBehavior');lines('[InspectorName("攻击片段")]');write()
use('CharacterAnimationIntent');sub('Func<CharacterActionHandoff> ReadAttackTiming', 'Func<CharacterAttackDefinition> ReadAttack');sub('Func<CharacterActionHandoff> readAttackTiming', 'Func<CharacterAttackDefinition> readAttack');sub('ReadAttackTiming = readAttackTiming','ReadAttack = readAttack');sub('仅在主线程接受独立播放时读取。','仅在主线程核对片段或接受播放时读取。');write()
use('CharacterAnimationResourceCatalog');sub('Func<CharacterActionHandoff> readTiming', 'Func<CharacterAttackDefinition> readAttack')
sub('() => config.AttackTable?.Find(attackState)?.AttackTiming ?? default;', '() => config.AttackTable?.Find(attackState);')
sub('action.Partners, handoff, readTiming','action.Partners, handoff, readAttack')
sub('        public bool CanPlayOnce(string key) => CanSubmit(key) && oneShots.Contains(key);','''        // 在主线程核对一次播放可用性，具名招式还要匹配自身声明的攻击片段。
        public bool CanPlayOnce(string key)
        {
            if (!CanSubmit(key) || !oneShots.Contains(key)) return false;
            var readAttack = rules[key].ReadAttack;
            if (readAttack == null) return true;
            var clip = readAttack()?.Clip;
            return clip && clips.MatchesAttackClip(validBindings[key].StateHash, clip);
        }''')
write()
use('CharacterClipPlayback');sub('        public bool CanPlayOnce(int hash)','''        // 确认动作映射绑定到该招式声明的单片段，避免播放另一招或混合树。
        internal bool MatchesAttackClip(int hash, AnimationClip clip) =>
            states.TryGetValue(hash, out var state) && state.Clip == clip && !state.Blend.Second && !clip.isLooping;

        public bool CanPlayOnce(int hash)''');write()
use('CharacterAnimationComposer');sub('rule.ReadAttackTiming != null','rule.ReadAttack != null');sub('timing = rule.ReadAttackTiming();','timing = rule.ReadAttack()?.AttackTiming ?? default;');write()
use('CharacterMovementForecast');sub('按各攻击状态公开的移动交接语义查找最早可复用的移动样本。','在突进结束后查找最早获准普通移动的样本。');write()
use('CharacterAttackCombo');sub('将运动和时钟交给行为框架。','发布招式时钟和状态观测。');write()
use('CharacterMotionMatcher');sub('在突进或攻击交还部位之前只缓存候选','在突进交还移动或攻击覆盖淡出时只缓存候选');write()
use('CharacterBehaviorAnimationMapping');sub('        internal CharacterBehaviorAnimationMapping Snapshot()','        // 冻结本条映射的触发条件与非攻击动作需要的交接参数。\n        internal CharacterBehaviorAnimationMapping Snapshot()');sub('copy.Handoff = Handoff?.Snapshot();','copy.Handoff = IsAttackState ? null : Handoff?.Snapshot();');write()

