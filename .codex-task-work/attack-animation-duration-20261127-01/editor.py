exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterComboTableEditor')
sub('''                if (!clip.hasMultipleDifferentValues && clip.objectReferenceValue is AnimationClip source &&
                    source.length > 0f && !float.IsInfinity(source.length) && GUILayout.Button("使用片段原长"))
                    animation.floatValue = source.length;''','''                using (new EditorGUI.DisabledScope(!clip.hasMultipleDifferentValues && !clip.objectReferenceValue))
                    if (GUILayout.Button("使用片段原长")) UseClipLengths(slot);''')
sub('        // 用中文标签绘制完整字段','''        // 按每个选中资产自己的片段长度写入该招动画时长，并把这次批量操作合并为一次撤销。
        private static void UseClipLengths(SerializedProperty slot)
        {
            var selection = slot.serializedObject;
            string path = slot.propertyPath;
            selection.ApplyModifiedProperties();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("使用攻击片段原长");
            foreach (var asset in selection.targetObjects)
            {
                using var target = new SerializedObject(asset);
                var attack = target.FindProperty(path);
                if (!(attack.FindPropertyRelative(nameof(CharacterAttackDefinition.Clip)).objectReferenceValue is AnimationClip clip) ||
                    !(clip.length > 0f) || float.IsInfinity(clip.length)) continue;
                attack.FindPropertyRelative(nameof(CharacterAttackMoveSettings.AnimationDurationSeconds)).floatValue = clip.length;
                target.ApplyModifiedProperties();
            }
            Undo.CollapseUndoOperations(group);
            selection.Update();
        }

        // 用中文标签绘制完整字段''')
write()
use('CharacterClipMixer')
sub('            public CharacterClipProgress Progress => new CharacterClipProgress(Hash, Motion.Length > 0f ? (float)(Clip.GetTime() / Motion.Length) : 0f, Motion.Loop);','''            // 单次素材只有在图时钟实际走到末端时才报告完成，避免浮点转换提前结算末尾事件。
            public CharacterClipProgress Progress
            {
                get
                {
                    double phase = Motion.Length > 0f ? Clip.GetTime() / Motion.Length : 0d;
                    float observed = Attack && phase < 1d ? Mathf.Min(0.99999994f, (float)phase) : (float)phase;
                    return new CharacterClipProgress(Hash, observed, Motion.Loop);
                }
            }''')
write()

