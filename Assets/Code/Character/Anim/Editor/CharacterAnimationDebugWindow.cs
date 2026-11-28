using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在播放模式下显示所选角色的动画播放、匹配决策和移动预测。</summary>
    internal sealed class CharacterAnimationDebugWindow : EditorWindow
    {
        private readonly AnimationPlaybackHistoryView actionHistory = new AnimationPlaybackHistoryView();
        private readonly CharacterMatchHistoryView matchHistory = new CharacterMatchHistoryView();
        private Vector2 scroll;
        private CharacterBehaviorAnimator displayedCharacter;
        private bool editorPaused;
        private CharacterAnimationDebugSnapshot currentSnapshot;

        // 打开角色动画运行状态窗口。
        [MenuItem("Window/3C/角色动画调试")]
        private static void Open() => GetWindow<CharacterAnimationDebugWindow>("角色动画调试");

        // 订阅播放模式变化以便退出时立即释放诊断目标。
        private void OnEnable() => EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

        // 刷新新选中角色的状态，并停止采集刚离开的角色。
        private void OnSelectionChange()
        {
            actionHistory.Clear();
            matchHistory.StopIfSelectionChanged(FindSelectedCharacter());
            Repaint();
        }

        // 在播放模式下更新已显示角色的运行状态。
        private void OnInspectorUpdate()
        {
            if (EditorApplication.isPlaying) Repaint();
        }

        // 关闭窗口时退订播放模式变化并结束诊断采集。
        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            actionHistory.Clear();
            matchHistory.Clear();
        }

        // 退出播放时立即丢弃本次匹配和动作历史。
        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode) return;
            actionHistory.Clear();
            matchHistory.Clear();
        }

        // 识别所选场景对象或其父层级中的运行时角色。
        private static CharacterBehaviorAnimator FindSelectedCharacter()
        {
            if (!EditorApplication.isPlaying) return null;
            var selected = Selection.activeGameObject;
            var animator = selected ? selected.GetComponentInParent<CharacterBehaviorAnimator>(true) : null;
            return animator && !EditorUtility.IsPersistent(animator) ? animator : null;
        }

        // 绘制所选角色当前的动作、匹配过程和独立移动预测。
        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout)
            {
                displayedCharacter = FindSelectedCharacter();
                if (displayedCharacter) ReadCurrent(displayedCharacter);
                else
                {
                    actionHistory.Clear();
                    matchHistory.Clear();
                }
            }

            var animator = displayedCharacter;
            if (!animator)
            {
                EditorGUILayout.HelpBox("在播放模式下选择场景中的角色或其子对象。", MessageType.Info);
                return;
            }

            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                EditorGUILayout.LabelField(animator.name, EditorStyles.boldLabel);
                if (editorPaused)
                    EditorGUILayout.LabelField("编辑器已暂停", EditorStyles.miniLabel);
                DrawSnapshot(currentSnapshot);
                actionHistory.Draw(animator);
                matchHistory.Draw(animator);
            }
        }

        // 在布局阶段读取已发布的状态，使对应的重绘使用同一份数据。
        private void ReadCurrent(CharacterBehaviorAnimator animator)
        {
            editorPaused = EditorApplication.isPaused;
            currentSnapshot = animator.ReadRuntimeDebug();
        }

        // 显示动画运行状态、当前动作和实际播放混合。
        private static void DrawSnapshot(in CharacterAnimationDebugSnapshot snapshot)
        {
            switch (snapshot.Status)
            {
                case CharacterAnimationDebugStatus.Inactive:
                    EditorGUILayout.LabelField("角色行为动画未启用。");
                    return;
                case CharacterAnimationDebugStatus.Paused:
                    EditorGUILayout.LabelField("角色模拟已暂停。");
                    return;
                case CharacterAnimationDebugStatus.AnimatorUnavailable:
                    EditorGUILayout.LabelField("Animator 未启用或已移除。");
                    return;
                case CharacterAnimationDebugStatus.ResourceUnavailable:
                    EditorGUILayout.HelpBox(snapshot.Error, MessageType.Warning);
                    return;
                case CharacterAnimationDebugStatus.WaitingForUpdate:
                    EditorGUILayout.LabelField("等待动画更新。");
                    return;
            }

            var root = snapshot.Root;
            if (root.Bound)
                EditorGUILayout.LabelField("视觉根（世界米）", $"{root.Binding} · {root.Phase}（{root.Reason}） · {root.PositionMode}/{root.RotationMode} · 模型步 {root.Actual:F3} · 素材步 {root.Reference:F3} · 模型/素材转角 {root.ActualYaw:F1}°/{root.ReferenceYaw:F1}° · 偏移 {root.Offset:F3} · 偏航 {root.Yaw:F1}° · 限位 {root.Limited}", EditorStyles.wordWrappedLabel);
            var mix = snapshot.Mix;
            DrawBody("上身", snapshot.UpperBody, mix, mix.Upper, mix.UpperWeight);
            DrawBody("下身", snapshot.LowerBody, mix, mix.Lower, mix.LowerWeight);
        }

        // 展示该部位的动作归属和写入姿势的片段配比。
        private static void DrawBody(string label, in CharacterAnimationDebugLayer owner, in CharacterDebugBodyMix mix,
            in CharacterDebugClipMix local, float localWeight)
        {
            string action = owner.Key ?? "无";
            if (owner.SentStart.HasValue) action += $" · 播放指令起点 {owner.SentStart.Value:P1}";
            EditorGUILayout.LabelField(label + "动作", action, EditorStyles.wordWrappedLabel);
            float baseline = Mathf.Max(0f, 1f - mix.Coverage - localWeight);
            if (baseline > 0f) DrawClipMix($"默认姿势 {baseline:P0}", mix.Base);
            if (localWeight > 0f) DrawClipMix($"局部动作 {localWeight:P0}", local);
            if (mix.Coverage > 0f) DrawClipMix($"共享动作 {mix.Coverage:P0}", mix.Action);
        }

        // 显示片段当前进度、源内权重和淡出片段。
        private static void DrawClipMix(string label, CharacterDebugClipMix mix)
        {
            string detail = mix.Target == null ? "无片段" : $"{mix.Target} · 进度 {mix.Phase:P1} · 源内权重 {mix.TargetWeight:P1}";
            if (mix.Outgoing != null) detail += $"  ·  淡出 {mix.Outgoing} {mix.OutgoingWeight:P1}";
            if (mix.OtherCount > 0) detail += $"  ·  其余 {mix.OtherCount} 段合计 {Mathf.Max(0f, 1f - mix.TargetWeight - mix.OutgoingWeight):P1}";
            EditorGUILayout.LabelField(label, detail, EditorStyles.wordWrappedLabel);
        }
    }
}
