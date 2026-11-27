exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterLightAttackBehavior')
sub('    // 配置招式使用的动画片段以及进入、主体和后摇三段角色时间。','    // 分别配置片段播放时长和招式进入、主体、后摇的三段角色时间。')
sub('        [Min(0f), InspectorName("进入缓入（秒）")]', '''        [Min(0f), InspectorName("动画持续时间（秒）")] public float AnimationDurationSeconds = 1f;
        internal bool HasValidAnimationDuration => AnimationDurationSeconds > 0f && !float.IsInfinity(AnimationDurationSeconds);
        [Min(0f), InspectorName("进入缓入（秒）")]''')
sub('        // 为行为播放和独立播放提供同一组阶段边界与总时长。\n        internal override CharacterActionHandoff AttackTiming => IsUsable\n            ? CharacterActionHandoff.ForAttack(Duration, EntryBlendSeconds / Duration,','''        // 冻结独立的动画时长与状态覆盖窗口；表现参数失效不阻止状态本身运行。
        internal override CharacterActionHandoff AttackTiming => IsUsable && HasValidAnimationDuration
            ? CharacterActionHandoff.ForAttack(Duration, AnimationDurationSeconds, EntryBlendSeconds / Duration,''')
write()
use('CharacterAttackCombo')
sub('        private float startProgress;', '        private float startProgress;\n        private double startElapsed;')
sub('current, startProgress, pending,','current, startProgress, startElapsed, pending,')
sub('            startProgress = progress;', '            startProgress = progress;\n            startElapsed = next.Elapsed;')
sub('            startProgress = current.Progress;', '            startProgress = current.Progress;\n            startElapsed = current.Elapsed;')
sub('            copy.startProgress = startProgress;', '            copy.startProgress = startProgress;\n            copy.startElapsed = startElapsed;')
write()
use('CharacterAttackState')
sub('        internal CharacterActionHandoff Handoff { get; }','''        internal CharacterActionHandoff Handoff { get; }
        internal double StartElapsed { get; }
        public double ElapsedSeconds { get; }''')
sub('CharacterAttackExecution execution, float start,','CharacterAttackExecution execution, float start, double startElapsed,')
sub('            Handoff = execution.Timing;', '            Handoff = execution.Timing;\n            StartElapsed = startElapsed;\n            ElapsedSeconds = execution.Elapsed;')
write()
use('CharacterComboValidation')
sub('                CheckLinks(table, definition.Links','''                if (definition is CharacterAttackMoveSettings moveTiming && !moveTiming.HasValidAnimationDuration)
                    report($"状态 {move} 的动画持续时间必须是大于零的有限秒数。");
                CheckLinks(table, definition.Links''')
write()
use('CharacterActionHandoff')
a=s.index('    /// <summary>本次表现');b=s.index('    // 保存一次动作',a)
s=s[:a]+'''    // 分开保存当前权重相位与片段采样相位，攻击独立播放还保留上次实际提交的秒数。
    internal readonly struct CharacterActionTiming
    {
        public readonly float? StartClipPhase, WeightPhase, ClipPhase;
        public readonly CharacterActionHandoff Handoff;
        public readonly bool StartAtZero, Resumed;
        internal readonly double AttackElapsed;

        // 普通动作可随素材自计时；攻击按已经过的角色秒数转换素材相位。
        public CharacterActionTiming(float? startClipPhase, float weightPhase, CharacterActionHandoff handoff,
            bool startAtZero, bool resumed, bool followClip = false, double attackElapsed = 0d)
        {
            StartClipPhase = startClipPhase;
            WeightPhase = weightPhase;
            Handoff = handoff;
            StartAtZero = startAtZero;
            Resumed = resumed;
            AttackElapsed = attackElapsed;
            ClipPhase = handoff.IsAttack ? handoff.ClipProgress(attackElapsed) : followClip ? (float?)null : weightPhase;
        }
    }

'''+s[b:]
sub('public readonly bool IsAttack;', 'public readonly float AnimationDuration;\n        public bool IsAttack => AnimationDuration > 0f;')
sub('IsAttack = false;', 'AnimationDuration = 0f;')
cut('        // 从状态三段时钟','        // 按当前进度','''        // 冻结状态覆盖窗口与片段独立播放时长，两者从同一进入时刻开始。
        internal static CharacterActionHandoff ForAttack(float duration, float animationDuration, float fullFrom, float releaseFrom) =>
            new CharacterActionHandoff(duration, animationDuration, fullFrom, releaseFrom);

        // 攻击权重按状态边界过渡，素材进度按动画持续时间计算。
        private CharacterActionHandoff(float duration, float animationDuration, float fullFrom, float releaseFrom)
            : this(duration, fullFrom, releaseFrom, 0f, null) => AnimationDuration = animationDuration;

        // 把已过秒数换算为素材相位，只有确已走完动画时长才指定素材终点。
        internal float ClipProgress(double elapsed) => elapsed >= AnimationDuration ? 1f :
            Mathf.Min(0.99999994f, Mathf.Max(0f, (float)(elapsed / AnimationDuration)));

''')
write()
use('CharacterComboTableEditor')
sub('                Draw(slot, nameof(CharacterAttackMoveSettings.Clip), "攻击片段");','''                var clip = Draw(slot, nameof(CharacterAttackMoveSettings.Clip), "攻击片段");
                var animation = Draw(slot, nameof(CharacterAttackMoveSettings.AnimationDurationSeconds), "动画持续时间（秒）");
                if (!clip.hasMultipleDifferentValues && clip.objectReferenceValue is AnimationClip source &&
                    source.length > 0f && !float.IsInfinity(source.length) && GUILayout.Button("使用片段原长"))
                    animation.floatValue = source.length;
                if (!animation.hasMultipleDifferentValues && (!(animation.floatValue > 0f) || float.IsInfinity(animation.floatValue)))
                    EditorGUILayout.HelpBox("动画持续时间必须是大于零的有限秒数。", MessageType.Warning);''')
sub('EditorGUILayout.LabelField("总时长（秒）", total);','EditorGUILayout.LabelField("状态总时长（秒）", total);')
write()
use('CharacterMovementForecast')
sub('        public readonly float AttackProgress;', '''        public readonly float AttackProgress;
        private readonly double attackElapsed;
        private readonly float attackAnimationDuration;
        internal bool HasAttackPose => (Behaviors & CharacterBehaviorId.Attack) != 0 && !ExternallyControlled &&
            attackElapsed < attackAnimationDuration;
        internal float AttackClipPhase => attackAnimationDuration > 0f ? Mathf.Clamp01((float)(attackElapsed / attackAnimationDuration)) : 0f;''')
sub('            AttackProgress = behavior.Attack.Progress;', '''            AttackProgress = behavior.Attack.Progress;
            attackElapsed = behavior.Attack.ElapsedSeconds;
            attackAnimationDuration = behavior.Attack.Handoff.AnimationDuration;''')
sub('                AttackProgress = Mathf.Lerp(a.AttackProgress, b.AttackProgress, fraction);','''            {
                AttackProgress = Mathf.Lerp(a.AttackProgress, b.AttackProgress, fraction);
                attackElapsed = a.attackElapsed + (b.attackElapsed - a.attackElapsed) * fraction;
            }''')
write()
use('CharacterForecastPose')
sub('bool attack = pose.AttackStateId != 0 && !pose.ExternallyControlled;', 'bool attack = pose.HasAttackPose;')
sub('Mathf.Clamp01(pose.AttackProgress) * length','pose.AttackClipPhase * length')
write()
p=Path('Assets/Data/Player/Shield/02 连招表.asset');raw=p.read_bytes()
s=raw.decode().replace('\r\n','\n')
for slot,d in (('LightOne','1.5000001'),('LightTwo','2.1000001'),('Heavy','1.8333334'),('SkillTwo','2.1333334')):
 i=s.index('    '+slot+':');j=s.index('      EntryBlendSeconds:',i)
 s=s[:j]+'      AnimationDurationSeconds: '+d+'\n'+s[j:]
p.write_bytes(s.replace('\n','\r\n' if b'\r\n' in raw else '\n').encode())

