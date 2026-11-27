exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterActionHandoff');sub('''        internal float ClipProgress(double elapsed) => elapsed >= AnimationDuration ? 1f :
            Mathf.Min(0.99999994f, Mathf.Max(0f, (float)(elapsed / AnimationDuration)));''','''        internal float ClipProgress(double elapsed) => ClipProgress(elapsed, AnimationDuration);

        // 实走与预测以同一算法计算素材采样位置，缺失动画时长时没有可采样的进度。
        internal static float ClipProgress(double elapsed, float animationDuration)
        {
            if (!(animationDuration > 0f)) return 0f;
            return elapsed >= animationDuration ? 1f :
                Mathf.Min(0.99999994f, Mathf.Max(0f, (float)(elapsed / animationDuration)));
        }''');write()
use('CharacterMovementForecast')
sub('private readonly float attackAnimationDuration;', 'private readonly float attackAnimationDuration, attackWindowEnd;')
sub('attackElapsed < attackAnimationDuration;', 'attackElapsed < attackWindowEnd;')
sub('attackAnimationDuration > 0f ? Mathf.Clamp01((float)(attackElapsed / attackAnimationDuration)) : 0f;', 'CharacterActionHandoff.ClipProgress(attackElapsed, attackAnimationDuration);')
sub('            attackAnimationDuration = behavior.Attack.Handoff.AnimationDuration;', '            attackAnimationDuration = behavior.Attack.Handoff.AnimationDuration;\n            attackWindowEnd = Mathf.Min(behavior.Attack.Duration, attackAnimationDuration);')
sub('        // 在同一招式实例内插值相位，跨状态时保留前一已提交样本。','        // 在同一招式实例内插值状态；跨招时沿前一样本的片段时钟推进到其可见终点。')
sub('in CharacterForecastSample b, float fraction)\n','in CharacterForecastSample b, float fraction, float stepSeconds)\n')
sub('                attackElapsed = a.attackElapsed + (b.attackElapsed - a.attackElapsed) * fraction;\n            }','                attackElapsed = a.attackElapsed + (b.attackElapsed - a.attackElapsed) * fraction;\n            }\n            else attackElapsed = a.attackElapsed + (double)stepSeconds * fraction;')
sub('        internal static CharacterForecastSample Between(in CharacterForecastSample a, in CharacterForecastSample b, float t)\n            => new CharacterForecastSample(a, b, t);','''        // 按已发布的采样步长生成两个预测点之间的观测。
        internal static CharacterForecastSample Between(in CharacterForecastSample a, in CharacterForecastSample b, float t, float stepSeconds)
            => new CharacterForecastSample(a, b, t, stepSeconds);''')
sub('samples[index + 1], Mathf.Clamp01((float)(offset - index)));','samples[index + 1], Mathf.Clamp01((float)(offset - index)), step);')
write()
use('CharacterPulseAnimationState')
sub('            firstPose = step.Attack.StartProgress;', '            firstPose = step.Attack.Handoff.IsAttack ? step.Attack.Handoff.ClipProgress(step.Attack.StartElapsed) : (float?)null;')
sub('start = resumedEntry ? lastRenderedAttackPhase ?? handoff.ClipProgress(attackElapsed) : attackEntryClipPhase;', 'start = resumedEntry ? Mathf.Max(lastRenderedAttackPhase.GetValueOrDefault(), firstPose.Value) : attackEntryClipPhase;')
write()
use('CharacterComboTableEditor')
sub('            DrawAttack(table, nameof(CharacterAttackTable.LightOne), "轻击一");','            string useClipLength = null;\n            DrawAttack(table, nameof(CharacterAttackTable.LightOne), "轻击一", ref useClipLength);')
for field,label in [('LightTwo','轻击二'),('Heavy','重击'),('SkillTwo','技能二')]:
 sub('DrawAttack(table, nameof(CharacterAttackTable.'+field+'), "'+label+'");','DrawAttack(table, nameof(CharacterAttackTable.'+field+'), "'+label+'", ref useClipLength);')
sub('            serializedObject.ApplyModifiedProperties();','            serializedObject.ApplyModifiedProperties();\n            if (useClipLength != null) UseClipLengths(serializedObject, useClipLength);')
sub('DrawAttack(SerializedProperty table, string field, string label)','DrawAttack(SerializedProperty table, string field, string label, ref string useClipLength)')
sub('if (GUILayout.Button("使用片段原长")) UseClipLengths(slot);','if (GUILayout.Button("使用片段原长")) useClipLength = slot.propertyPath;')
sub('private static void UseClipLengths(SerializedProperty slot)\n        {\n            var selection = slot.serializedObject;\n            string path = slot.propertyPath;\n            selection.ApplyModifiedProperties();','private static void UseClipLengths(SerializedObject selection, string path)\n        {')
sub('            selection.Update();\n','')
write()

