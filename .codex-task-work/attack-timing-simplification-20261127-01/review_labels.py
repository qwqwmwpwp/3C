exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterBehaviorFlow')
sub('            ReleaseBeforeRequests(context, ref step);\n            if (!DispatchCancelled) ResolveRequests(ref context, ref step);','            bool attackContinued = ReleaseBeforeRequests(context, ref step);\n            if (!DispatchCancelled) ResolveRequests(ref context, ref step, attackContinued);')
a=s.index('        private void ReleaseBeforeRequests');b=s.index('        // 按已装配的优先顺序',a);chunk=s[a:b]
chunk=chunk.replace('private void ReleaseBeforeRequests','private bool ReleaseBeforeRequests').replace('            foreach (var rule','            bool attackContinued = false;\n            foreach (var rule').replace('if (DispatchCancelled) return;','if (DispatchCancelled) return false;').replace('attack.IsComplete &&\n                    !attack.TryContinueAtEndpoint(context) && !DispatchCancelled)','attack.IsComplete)').replace('                    // 先冻结旧招末相位','                    attackContinued = attack.TryContinueAtEndpoint(context);\n                    if (attackContinued || DispatchCancelled) continue;\n                    // 先冻结旧招末相位').replace('            }\n        }\n','            }\n            return attackContinued;\n        }\n')
s=s[:a]+chunk+s[b:]
sub('private void ResolveRequests(ref CharacterBehaviorContext context, ref CharacterBehaviorStep step)','private void ResolveRequests(ref CharacterBehaviorContext context, ref CharacterBehaviorStep step, bool attackContinued)')
sub('                var behavior = rule.Behavior;\n                bool explicitRequest','''                var behavior = rule.Behavior;
                // 同批突进可能刚打断已接续的新招；已用作接续的攻击键不能再次申请首招。
                if (attackContinued && behavior is CharacterAttackBehavior) continue;
                bool explicitRequest''')
write()
use('CharacterPulseAnimationState')
sub('if (!seed) animations.QueueEdge(binding, CharacterActionPlaybackStatus.Withdrawn);','''if (!seed) animations.QueueEdge(binding, CharacterActionPlaybackStatus.Interrupted,
                        end: CharacterPlaybackEndSource.ArbitrationLost);''')
write()
use('CharacterActionHandoff')
sub('    [Serializable]\n    internal sealed class CharacterActionHandoffSettings','    // 配置普通单次动作的接管阶段与移动交接权重。\n    [Serializable]\n    internal sealed class CharacterActionHandoffSettings')
sub('    internal readonly struct CharacterActionHandoff','    // 保存一次动作的覆盖时钟，按阶段计算动画权重。\n    internal readonly struct CharacterActionHandoff')
sub('        public CharacterActionHandoff(float duration,','        // 冻结普通动作的接管阶段和移动获准后的淡出时间。\n        public CharacterActionHandoff(float duration,')
sub('        public float WeightAt(float progress, float entryWeight) =>','        // 按当前进度和起始覆盖量计算本次动作的显示权重。\n        public float WeightAt(float progress, float entryWeight) =>')
sub('        public float WeightAt(float progress, float entryWeight, out float phaseSlope)','        // 计算覆盖权重及其相位导数，零时长的边界直接切换权重。\n        public float WeightAt(float progress, float entryWeight, out float phaseSlope)')
write()
use('CharacterLayerBlendWeight');sub('        private float AdvanceAction(', '        // 从本层实际覆盖量按动作时钟推进权重，并记录下一帧承接的速度。\n        private float AdvanceAction(');write()
use('CharacterComboTableEditor')
a=s.rfind('}')
s=s[:a]+'''
    // 为原生可排序路线数组的每个元素显示中文字段、归一化范围与时间单位。
    [CustomPropertyDrawer(typeof(CharacterAttackLink))]
    internal sealed class CharacterAttackLinkDrawer : PropertyDrawer
    {
        private static readonly (string field, GUIContent label)[] Fields = {
            (nameof(CharacterAttackLink.Trigger), new GUIContent("触发方式")),
            (nameof(CharacterAttackLink.Button), new GUIContent("按键")),
            (nameof(CharacterAttackLink.To), new GUIContent("目标招式")),
            (nameof(CharacterAttackLink.OpeningCondition), new GUIContent("起手移动条件")),
            (nameof(CharacterAttackLink.LinkStart), new GUIContent("开放接续 / 定时进度（0～1）")),
            (nameof(CharacterAttackLink.LinkEnd), new GUIContent("关闭输入进度（0～1）")),
            (nameof(CharacterAttackLink.EarlySeconds), new GUIContent("提前输入（秒）")),
            (nameof(CharacterAttackLink.Result), new GUIContent("匹配状态结果（非零）"))
        };

        // 根据元素折叠状态计算标题与八个原生属性控件需要的高度。
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight;
            return property.isExpanded ? line + Fields.Length * (line + EditorGUIUtility.standardVerticalSpacing) : line;
        }

        // 通过 Unity 序列化字段绘制当前路线，使多选、撤销和原生数组排序继续生效。
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (new EditorGUI.PropertyScope(position, label, property))
            {
                position.height = EditorGUIUtility.singleLineHeight;
                string path = property.propertyPath;
                int index = path.LastIndexOf('[');
                string title = index >= 0 && int.TryParse(path.Substring(index + 1, path.Length - index - 2), out int number)
                    ? $"路线 {number + 1}" : "路线";
                property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, title, true);
                if (!property.isExpanded) return;
                using (new EditorGUI.IndentLevelScope())
                    foreach (var item in Fields)
                    {
                        position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
                        EditorGUI.PropertyField(position, property.FindPropertyRelative(item.field), item.label);
                    }
            }
        }
    }
'''+s[a:]
write()

