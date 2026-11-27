exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterComboValidation')
sub('            var keys = new HashSet<string>(StringComparer.Ordinal);','            var actions = new Dictionary<string, CharacterAnimationActionSettings>(StringComparer.Ordinal);')
sub('if (action != null) keys.Add(action.Key);','if (!string.IsNullOrWhiteSpace(action?.Key)) actions[action.Key] = action;')
sub('                foreach (string key in','''                if (row.IsAttackState && table?.Find(row.AttackStateId) is CharacterAttackMoveSettings attack &&
                    !string.IsNullOrWhiteSpace(row.EntryAction) && actions.TryGetValue(row.EntryAction, out var entry))
                    CheckMappedClip(attack, entry, animation.PlaybackLibrary, report);
                foreach (string key in''')
sub('!keys.Contains(key)','!actions.ContainsKey(key)')
sub('        // 检查四个固定状态','''        // 核对攻击的进入动作键在当前播放库实际使用同一个非循环片段。
        private static void CheckMappedClip(CharacterAttackMoveSettings attack, CharacterAnimationActionSettings action,
            CharacterMotionDatabase library, Action<string> report)
        {
            var states = library ? library.Playback?.States : null;
            var state = Array.Find(states ?? Array.Empty<CharacterClipState>(), item => item != null && item.Path == action.StateName);
            if (state == null || !state.Clip || state.Clip != attack.Clip)
                report($"{attack.Name} 的动作 {action.Key} 与播放库状态 {action.StateName} 的片段不一致。");
            else if (state.Blend.Second || state.Clip.isLooping || !(state.Clip.length > 0f) || !(state.Speed > 0f))
                report($"{attack.Name} 的动作 {action.Key} 需要可正向播放的非循环单片段。");
        }

        // 检查四个固定状态''')
sub('                CheckLinks(table, definition.Links','''                if (checkExecution && definition is CharacterAttackMoveSettings moveSettings &&
                    (!moveSettings.Clip || moveSettings.Clip.isLooping || !(moveSettings.Clip.length > 0f)))
                    report($"状态 {move} 需要非循环且非空的攻击片段。");
                CheckLinks(table, definition.Links''')
write()
use('CharacterAttackDefinition');sub('internal void Advance(double end) => Elapsed = end;', '''internal void Advance(CharacterBehaviorContext context, double end)
        {
            double start = Elapsed;
            Elapsed = end;
            OnAdvance(context, start, end);
        }

        // 处理状态专属的时间或输入事件，普通招式只需推进时钟。
        protected virtual void OnAdvance(CharacterBehaviorContext context, double start, double end) { }''')
sub('        // 在预输入与实际交接时按角色时刻和持续输入检查专属条件，按键边沿由来源状态和路线处理。\n        internal virtual bool CanEnter(CharacterBehaviorContext context) => true;\n\n','')
sub('CheckSkill(double time, double readyAt)','CheckEntry(CharacterBehaviorContext context, double skillReadyAt)');write()
use('CharacterLightAttackBehavior');sub('CheckSkill(double time, double readyAt)','CheckEntry(CharacterBehaviorContext context, double skillReadyAt)');sub('readyAt > time + CharacterAttackBehavior.Tolerance','skillReadyAt > context.SimulationTime + CharacterAttackBehavior.Tolerance');write()
use('CharacterAttackCombo')
cut('            var skill = definition.CheckSkill','        }','            return definition.CheckEntry(At(context, 0d, 0f, true), skillReadyAt);\n')
sub('AdvanceSegment(context.DeltaTime, NextEndpoint(current.NextBoundary))','AdvanceSegment(context, context.DeltaTime, NextEndpoint(current.NextBoundary))')
sub('            double offset = 0d;', '            current.Advance(At(context, 0d, 0f), current.Elapsed);\n            double offset = 0d;')
sub('while (TrySwitch(endpoint)) { }','while (TrySwitch(endpoint)) current.Advance(endpoint, current.Elapsed);')
sub('AdvanceSegment(seconds, next);','AdvanceSegment(At(context, offset, (float)seconds, true), seconds, next);')
sub('private void AdvanceSegment(double seconds, double boundary)','private void AdvanceSegment(CharacterBehaviorContext context, double seconds, double boundary)');sub('current.Advance(end);','current.Advance(context, end);');write()
# Editor names and read-only totals for the fixed slots.
p=root/'Editor/CharacterComboTableEditor.cs';assert not p.exists()
p.write_text('''using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 展示四招各自的片段、路线及三段秒数，实时计算只读总时长。
    [CustomEditor(typeof(CharacterComboTable)), CanEditMultipleObjects]
    internal sealed class CharacterComboTableEditor : Editor
    {
        // 使用序列化属性编辑原资产，支持撤销、多选与路线数组增删。
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var table = serializedObject.FindProperty(nameof(CharacterComboTable.Data));
            Draw(table, nameof(CharacterAttackTable.DashInterruptPhases), "允许突进打断的阶段");
            Draw(table, nameof(CharacterAttackTable.Openers), "起手路线");
            DrawAttack(table, nameof(CharacterAttackTable.LightOne), "轻击一");
            DrawAttack(table, nameof(CharacterAttackTable.LightTwo), "轻击二");
            DrawAttack(table, nameof(CharacterAttackTable.Heavy), "重击");
            DrawAttack(table, nameof(CharacterAttackTable.SkillTwo), "技能二");
            serializedObject.ApplyModifiedProperties();
        }

        // 在具名招式内编辑通用参数，只有技能字段具备冷却时间。
        private static void DrawAttack(SerializedProperty table, string field, string label)
        {
            var slot = table.FindPropertyRelative(field);
            slot.isExpanded = EditorGUILayout.Foldout(slot.isExpanded, label, true);
            if (!slot.isExpanded) return;
            using (new EditorGUI.IndentLevelScope())
            {
                Draw(slot, nameof(CharacterAttackDefinition.Name), "显示名称");
                Draw(slot, nameof(CharacterAttackMoveSettings.Clip), "攻击片段");
                var entry = Draw(slot, nameof(CharacterAttackMoveSettings.EntryBlendSeconds), "进入缓入（秒）");
                var active = Draw(slot, nameof(CharacterAttackMoveSettings.ActiveSeconds), "主体持续（秒）");
                var recovery = Draw(slot, nameof(CharacterAttackMoveSettings.RecoverySeconds), "后摇淡出（秒）");
                bool mixed = entry.hasMultipleDifferentValues || active.hasMultipleDifferentValues || recovery.hasMultipleDifferentValues;
                string total = mixed ? "—" : (entry.floatValue + active.floatValue + recovery.floatValue).ToString("G7");
                EditorGUILayout.LabelField("总时长（秒）", total);
                if (field == nameof(CharacterAttackTable.SkillTwo))
                    Draw(slot, nameof(CharacterSkillAttackSettings.CooldownSeconds), "技能冷却（秒）");
                Draw(slot, nameof(CharacterAttackDefinition.Links), "后续路线");
            }
        }

        // 用中文标签绘制完整字段并返回本次编辑后的值。
        private static SerializedProperty Draw(SerializedProperty parent, string field, string label)
        {
            var value = parent.FindPropertyRelative(field);
            EditorGUILayout.PropertyField(value, new GUIContent(label), true);
            return value;
        }
    }
}
''',encoding='utf-8')
import uuid
meta=(root/'Editor/CharacterProfileEditor.cs.meta').read_text()
p.with_suffix('.cs.meta').write_text(re.sub('guid: [a-f0-9]+','guid: '+uuid.uuid4().hex,meta),encoding='utf-8')

