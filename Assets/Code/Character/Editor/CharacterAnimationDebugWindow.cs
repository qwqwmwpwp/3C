using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在播放模式下显示所选角色的动画播放、匹配决策和移动预测。</summary>
    internal sealed class CharacterAnimationDebugWindow : EditorWindow
    {
        private readonly CharacterAnimationActionHistoryView actionHistory = new CharacterAnimationActionHistoryView();
        private Vector2 scroll;
        private bool showActualPath;
        private bool showMatch;
        private readonly Vector3[] actualPath = new Vector3[128];
        private bool editorPaused;
        private CharacterAnimationDebugSnapshot currentSnapshot;
        private string forecastSnapshot;
        private Animator matchAnimator;
        private readonly CharacterMatchDebugFrame matchFrame = new CharacterMatchDebugFrame();
        private bool hasMatchFrame;

        // 打开角色动画运行状态窗口。
        [MenuItem("Window/3C/角色动画调试")]
        private static void Open() => GetWindow<CharacterAnimationDebugWindow>("角色动画调试");

        // 刷新新选中角色的当前状态。
        private void OnSelectionChange()
        {
            actionHistory.Clear();
            Repaint();
        }

        // 在播放模式下更新已显示角色的运行状态。
        private void OnInspectorUpdate()
        {
            if (EditorApplication.isPlaying) Repaint();
        }

        // 关闭窗口时结束当前角色的按需诊断采集。
        private void OnDisable()
        {
            actionHistory.Clear();
            ObserveMatch(null);
        }

        // 绘制所选角色当前的动作、匹配过程和独立移动预测。
        private void OnGUI()
        {
            var selected = Selection.activeGameObject;
            var animator = selected ? selected.GetComponentInParent<CharacterBehaviorAnimator>(true) : null;
            if (!EditorApplication.isPlaying || !animator || EditorUtility.IsPersistent(animator))
            {
                ObserveMatch(null);
                actionHistory.Clear();
                EditorGUILayout.HelpBox("在播放模式下选择场景中的角色或其子对象。", MessageType.Info);
                return;
            }

            if (Event.current.type == EventType.Layout)
                ReadCurrent(animator);
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                EditorGUILayout.LabelField(animator.name, EditorStyles.boldLabel);
                if (editorPaused)
                    EditorGUILayout.LabelField("编辑器已暂停", EditorStyles.miniLabel);
                DrawSnapshot(currentSnapshot);
                actionHistory.Draw(animator);
                showMatch = EditorGUILayout.Foldout(showMatch, "地面匹配决策");
                if (showMatch) DrawMatch(currentSnapshot.Status, hasMatchFrame ? matchFrame : null);
                else ObserveMatch(null);
                var driver = animator.GetComponent<CharacterBehaviorDriver>();
                if (driver)
                {
                    EditorGUILayout.LabelField("移动预测", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(forecastSnapshot ?? "", EditorStyles.wordWrappedLabel);
                    showActualPath = EditorGUILayout.Foldout(showActualPath, "实际路径（俯视）");
                    if (showActualPath) DrawActualPath(driver);
                }
            }
        }

        // 在俯视图中绘制角色实际移动经过的路径。
        private void DrawActualPath(CharacterBehaviorDriver driver)
        {
            Rect rect = GUILayoutUtility.GetRect(10f, 90f, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            int count = driver.CopyActualPath(actualPath);
            if (count < 2) return;
            var bounds = new Bounds(actualPath[0], Vector3.zero);
            for (int i = 1; i < count; i++) bounds.Encapsulate(actualPath[i]);
            float scale = Mathf.Min((rect.width - 8f) / Mathf.Max(0.1f, bounds.size.x),
                (rect.height - 8f) / Mathf.Max(0.1f, bounds.size.z));
            // 将实际位置的右前坐标映射为图表像素。
            Vector2 Project(Vector3 p) => rect.center + new Vector2(p.x - bounds.center.x, bounds.center.z - p.z) * scale;
            Handles.BeginGUI();
            using (new Handles.DrawingScope(Color.white))
                for (int i = 1; i < count; i++) Handles.DrawLine(Project(actualPath[i - 1]), Project(actualPath[i]), 2f);
            Handles.EndGUI();
        }

        // 在布局阶段读取已发布的状态，使对应的重绘使用同一份数据。
        private void ReadCurrent(CharacterBehaviorAnimator animator)
        {
            editorPaused = EditorApplication.isPaused;
            currentSnapshot = animator.ReadRuntimeDebug();
            forecastSnapshot = animator.GetComponent<CharacterBehaviorDriver>()?.DescribeForecast();
            ObserveMatch(showMatch ? animator.PreviewAnimator : null);
            var captured = matchAnimator ? CharacterMatchDebugCapture.Read(matchAnimator) : null;
            hasMatchFrame = captured != null;
            if (hasMatchFrame) CopyMatchFrame(captured);
        }

        // 仅在调试折叠区展开时订阅当前选中的 Animator。
        private void ObserveMatch(Animator target)
        {
            if (ReferenceEquals(matchAnimator, target)) return;
            matchAnimator = target;
            hasMatchFrame = false;
            CharacterMatchDebugCapture.Select(target);
        }

        // 在布局时复制本次查询，避免重绘读到已交回后台规划的缓冲。
        private void CopyMatchFrame(CharacterMatchDebugFrame source)
        {
            matchFrame.Query = source.Query;
            matchFrame.Reason = source.Reason;
            matchFrame.Choice = source.Choice;
            matchFrame.Scoring = source.Scoring;
            matchFrame.Chosen = source.Chosen;
            matchFrame.Continuing = source.Continuing;
            matchFrame.GroupRejected = source.GroupRejected;
            matchFrame.HistoryRejected = source.HistoryRejected;
            matchFrame.HistoryFallback = source.HistoryFallback;
            matchFrame.UseRejected = source.UseRejected;
            matchFrame.GaitRejected = source.GaitRejected;
            matchFrame.ResourceRejected = source.ResourceRejected;
            matchFrame.SpeedRejected = source.SpeedRejected;
            matchFrame.SupportRejected = source.SupportRejected;
            matchFrame.WindowRejected = source.WindowRejected;
            matchFrame.Evaluated = source.Evaluated;
            matchFrame.TopCount = source.TopCount;
            System.Array.Copy(source.Top, matchFrame.Top, source.TopCount);
        }

        // 显示查询、实际选择、延续方案和本次搜索的候选排序。
        private static void DrawMatch(CharacterAnimationDebugStatus status, CharacterMatchDebugFrame frame)
        {
            if (status != CharacterAnimationDebugStatus.Running || frame == null)
            {
                EditorGUILayout.LabelField("等待当前角色的地面匹配查询。", EditorStyles.miniLabel);
                return;
            }
            EditorGUILayout.LabelField("查询", frame.Query, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("结果", $"{frame.Choice} · {frame.Reason}", EditorStyles.wordWrappedLabel);
            DrawScoring(frame.Scoring);
            if (frame.HistoryFallback) EditorGUILayout.HelpBox("当前评分已统一排除过去采样，适用于本次展示的入点与续播。", MessageType.Info);
            DrawEntry("获选", frame.Chosen, frame.Scoring);
            DrawEntry("延续对照", frame.Continuing, frame.Scoring);
            for (int i = 0; i < frame.TopCount; i++)
                DrawEntry(i == 0 ? "最佳新入点" : $"新入点 {i + 1}", frame.Top[i], frame.Scoring);
            EditorGUILayout.LabelField("筛选与评分", $"前史不足 {frame.HistoryRejected}、评分入点 {frame.Evaluated}；条件表排除 {frame.GroupRejected}、用途 {frame.UseRejected}、步态 {frame.GaitRejected}、" +
                $"资源 {frame.ResourceRejected}、速度 {frame.SpeedRejected}、" +
                $"承重 {frame.SupportRejected}、时间窗 {frame.WindowRejected}", EditorStyles.wordWrappedLabel);
        }

        // 指明当前查询使用的评分配置以及共用通道的归属。
        private static void DrawScoring(GroundMatchScoring scoring)
        {
            string detail = scoring switch
            {
                GroundMatchScoring.Schema => "布局通道代价 = 误差平方 × Weight ÷ Scale²。接触使用当前用途的 Contact 权重与 ContactScale 尺度，步频使用当前用途的 Pace 权重与 PaceScale 尺度。共用项汇总同时命中多个启用组的通道，每个通道计一次。",
                GroundMatchScoring.Compatibility => "兼容脚姿与预测轨迹，使用地面匹配模块的用途权重和尺度。",
                _ => "当前帧未进行地面匹配评分。"
            };
            EditorGUILayout.LabelField("评分来源", detail, EditorStyles.wordWrappedLabel);
        }

        // 展示候选的状态、入点、倍率，以及与其他候选可直接比较的代价分项。
        private static void DrawEntry(string label, in CharacterMatchDebugEntry entry, GroundMatchScoring scoring)
        {
            if (string.IsNullOrEmpty(entry.State))
            {
                EditorGUILayout.LabelField(label, "无");
                return;
            }
            var cost = entry.Cost;
            string sample = $"{entry.State} · {entry.Time:F3}s · {entry.Rate:F2}×";
            float total = cost.Total;
            if (cost.MissingHistory || float.IsNaN(total) || float.IsInfinity(total))
            {
                string reason = cost.MissingHistory ? "前史不足，代价不可比较" : "代价无效";
                EditorGUILayout.LabelField(label, $"{sample} · {reason}", EditorStyles.wordWrappedLabel);
                return;
            }
            EditorGUILayout.LabelField(label, $"{sample} · 总分 {total:F3}", EditorStyles.wordWrappedLabel);
            string sharedCosts = $"接触 {cost.Contact:F3} · 步频 {cost.Pace:F3} · 入点 {cost.Entry:F3}";
            if (scoring == GroundMatchScoring.Schema)
            {
                var groups = cost.FeatureBreakdown;
                EditorGUILayout.LabelField($"布局 {cost.Features:F3}（脚部 {groups.Feet:F3} · 身体 {groups.Body:F3} · 手部 {groups.Hands:F3} · " +
                    $"轨迹 {groups.Trajectory:F3} · 共用 {groups.Shared:F3}） · {sharedCosts} · 通道位 0x{groups.Channels:X}", EditorStyles.wordWrappedLabel);
                return;
            }
            EditorGUILayout.LabelField($"脚位 {cost.Position:F3} · 脚速 {cost.Velocity:F3} · 骨盆 {cost.Pelvis:F3} · " +
                $"路径 {cost.Path:F3} · 朝向 {cost.Facing:F3} · {sharedCosts}", EditorStyles.wordWrappedLabel);
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
