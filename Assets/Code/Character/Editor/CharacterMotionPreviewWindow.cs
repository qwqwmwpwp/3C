using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 预览当前数据组变体在参考轨迹上的脚位、接触与承重。
    internal sealed class CharacterMotionPreviewWindow : EditorWindow
    {
        private const string Menu = "Assets/3C/预览地面动作轨道";
        private static readonly Color rootColor = new Color(0.3f, 0.8f, 1f);
        private static readonly Color leftColor = new Color(0.48f, 0.93f, 0.55f);
        private static readonly Color rightColor = new Color(1f, 0.69f, 0.32f);
        private CharacterMotionDatabase database;
        private CharacterMotionPreviewData preview;
        private GroundAnimationGroup group;
        private CharacterMotionTrack variant;
        private string error;
        private Vector2 scroll;
        private int choice, sample;

        // 检查资产或角色配置是否提供可预览的数据组。
        [MenuItem(Menu, true)]
        private static bool CanOpen() => ResolveDatabase() != null;

        // 打开与当前所选资产关联的预览窗口。
        [MenuItem(Menu)]
        private static void Open()
        {
            var window = GetWindow<CharacterMotionPreviewWindow>("地面动作预览");
            window.SetDatabase(ResolveDatabase());
        }

        // 从动作库或使用动作库的角色配置解析预览来源。
        private static CharacterMotionDatabase ResolveDatabase() => Selection.activeObject is CharacterProfile profile
            ? profile.EffectivePlayback : Selection.activeObject as CharacterMotionDatabase;

        // 切换动作库并丢弃之前的瞬时采样。
        private void SetDatabase(CharacterMotionDatabase value)
        {
            database = value;
            group = null;
            variant = null;
            preview = null;
            error = null;
            choice = sample = 0;
        }

        // 绘制变体选择和临时采样结果。
        private void OnGUI()
        {
            var selected = (CharacterMotionDatabase)EditorGUILayout.ObjectField("动作库", database, typeof(CharacterMotionDatabase), false);
            if (selected != database) SetDatabase(selected);
            if (!database || !database.Groups)
            {
                EditorGUILayout.HelpBox("请选择使用基础动画数据组的动作库。", MessageType.Info);
                return;
            }
            if (!DrawVariantPicker()) return;

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("临时采样当前源变体")) SampleSelected();
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (preview == null) return;

            using var view = new EditorGUILayout.ScrollViewScope(scroll);
            scroll = view.scrollPosition;
            DrawPreview();
        }

        // 列出源数据组变体，并让选择的源设置与预览结果保持对应。
        private bool DrawVariantPicker()
        {
            var names = new List<string>();
            var groups = new List<GroundAnimationGroup>();
            var variants = new List<CharacterMotionTrack>();
            foreach (var candidate in database.Groups.Groups ?? Array.Empty<GroundAnimationGroup>())
            {
                if (candidate?.Variants == null) continue;
                for (int i = 0; i < candidate.Variants.Length; i++)
                {
                    var motion = candidate.Variants[i];
                    if (motion == null) continue;
                    groups.Add(candidate);
                    variants.Add(motion);
                    string state = motion.States?.Length > 0 ? motion.States[0] : $"变体 {i + 1}";
                    names.Add($"{candidate.Name} · {state}");
                }
            }
            if (names.Count == 0)
            {
                EditorGUILayout.HelpBox("当前数据组没有地面变体。", MessageType.Info);
                return false;
            }
            choice = EditorGUILayout.Popup("轨道", Mathf.Clamp(choice, 0, names.Count - 1), names.ToArray());
            if (group != groups[choice] || variant != variants[choice])
            {
                group = groups[choice];
                variant = variants[choice];
                preview = null;
                error = null;
            }
            return true;
        }

        // 从当前源片段采样到内存，失败时显示原因。
        private void SampleSelected()
        {
            preview = null;
            error = null;
            try
            {
                preview = CharacterMotionGroupBaker.Preview(database, group, variant);
                sample = 0;
            }
            catch (Exception failure) { error = failure.Message; }
        }

        // 显示当前样点、俯视脚迹、接触曲线和采样质量提示。
        private void DrawPreview()
        {
            var track = preview.Track;
            string basis = track.PathSource == GroundPathSource.RootMotion ? "提取根运动" :
                track.PathSource == GroundPathSource.Authored ? "作者参考位移估算" : "直线参考位移估算";
            var blend = preview.Blend;
            EditorGUILayout.LabelField("基础片段", $"{track.Clip.name} · 相位 {track.FirstPhase:P1} · {(blend.Second ? blend.FirstRate : 1f):F2}×");
            if (blend.Second)
                EditorGUILayout.LabelField("混合片段", $"{blend.Second.name} · 相位 {blend.SecondPhase:P1} · {blend.SecondRate:F2}×");
            EditorGUILayout.LabelField("根轨迹", basis);
            EditorGUILayout.LabelField("图例", "蓝 · 参考根，灰 · 采样根，白 · 髋骨，绿 · 左脚，橙 · 右脚");
            sample = EditorGUILayout.IntSlider("样点", sample, 0, track.Poses.Length - 1);
            float seconds = track.TimeAt(sample);
            var contact = track.Poses[sample].Contact;
            EditorGUILayout.LabelField($"{seconds:F3} / {track.Seconds:F3}s", $"左 {contact.x:F2}  右 {contact.y:F2}  {SupportLabel(contact)}");
            EditorGUILayout.LabelField("连续接触内最大水平位移", $"左 {preview.ContactTravel.x:F3}m   右 {preview.ContactTravel.y:F3}m");
            DrawPath(GUILayoutUtility.GetRect(30f, 210f, GUILayout.ExpandWidth(true)));
            EditorGUILayout.LabelField("接触时间线", "阴影 · 入点窗口，紫 · 主制动，灰 · 退出，白 · 当前样点");
            DrawContacts(GUILayoutUtility.GetRect(30f, 76f, GUILayout.ExpandWidth(true)));
            foreach (var warning in preview.Warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
        }

        // 将样点接触按运行时使用的承重规则解释成左右侧。
        private static string SupportLabel(Vector2 contact)
        {
            switch (CharacterFootObservation.SupportOf(contact))
            {
                case GroundTrackSide.Left: return "左脚承重";
                case GroundTrackSide.Right: return "右脚承重";
                default: return "承重侧未定";
            }
        }

        // 绘制参考根、实际采样根、双脚移动和当前姿势的俯视投影。
        private void DrawPath(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(rect, new Color(0.13f, 0.14f, 0.16f));
            var bounds = new Bounds(Vector3.zero, Vector3.one * 0.4f);
            for (int i = 0; i < preview.ReferenceRoots.Length; i++)
            {
                bounds.Encapsulate(preview.ReferenceRoots[i]);
                bounds.Encapsulate(preview.SourceRoots[i]);
                bounds.Encapsulate(preview.Left[i]);
                bounds.Encapsulate(preview.Right[i]);
                bounds.Encapsulate(preview.Hips[i]);
            }
            var root = preview.ReferenceRoots[sample];
            var arrow = root + preview.Facing[sample] * Vector3.forward * 0.18f;
            bounds.Encapsulate(arrow);
            float scale = Mathf.Max(0f, Mathf.Min((rect.width - 24f) / bounds.size.x, (rect.height - 24f) / bounds.size.z));
            // 把右前坐标平面变换到窗口像素。
            Vector2 Project(Vector3 position) => rect.center + new Vector2(position.x - bounds.center.x, bounds.center.z - position.z) * scale;

            Handles.BeginGUI();
            using (new Handles.DrawingScope(Color.white))
            {
                for (int i = 1; i < preview.ReferenceRoots.Length; i++)
                {
                    Handles.color = rootColor;
                    Handles.DrawLine(Project(preview.ReferenceRoots[i - 1]), Project(preview.ReferenceRoots[i]), 2f);
                    Handles.color = Color.gray;
                    Handles.DrawDottedLine(Project(preview.SourceRoots[i - 1]), Project(preview.SourceRoots[i]), 3f);
                    var a = preview.Track.Poses[i - 1].Contact;
                    var b = preview.Track.Poses[i].Contact;
                    DrawFootLine(Project(preview.Left[i - 1]), Project(preview.Left[i]), leftColor, Mathf.Min(a.x, b.x));
                    DrawFootLine(Project(preview.Right[i - 1]), Project(preview.Right[i]), rightColor, Mathf.Min(a.y, b.y));
                }
                Handles.color = rootColor;
                Handles.DrawLine(Project(root), Project(arrow), 3f);
                var hips = Project(preview.Hips[sample]);
                var left = Project(preview.Left[sample]);
                var right = Project(preview.Right[sample]);
                Handles.color = Color.white;
                Handles.DrawLine(hips, left);
                Handles.DrawLine(hips, right);
                DrawPoint(hips, Color.white);
                DrawPoint(left, leftColor);
                DrawPoint(right, rightColor);
                DrawPoint(Project(preview.SourceRoots[sample]), Color.gray);
            }
            Handles.EndGUI();
        }

        // 用线宽区分接触状态下的脚迹和摆动段。
        private static void DrawFootLine(Vector2 before, Vector2 after, Color color, float contact)
        {
            bool planted = contact >= CharacterMotionTrackAudit.PlantedContact;
            Handles.color = planted ? color : new Color(color.r, color.g, color.b, 0.45f);
            Handles.DrawLine(before, after, planted ? 3f : 1f);
        }

        // 用像素圆标注当前样点的根或骨骼。
        private static void DrawPoint(Vector2 position, Color color)
        {
            Handles.color = color;
            Handles.DrawSolidDisc(position, Vector3.forward, 3.5f);
        }

        // 绘制接触强弱、可选入点、主制动与退出标记。
        private void DrawContacts(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;
            var track = preview.Track;
            float left = rect.xMin + 8f, width = Mathf.Max(0f, rect.width - 16f);
            // 把动作秒数映射到图表的横轴。
            float X(float time) => left + Mathf.Clamp01(time / track.Seconds) * width;
            // 把接触强度映射到图表的纵轴。
            float Y(float value) => Mathf.Lerp(rect.yMax - 8f, rect.yMin + 8f, value);
            EditorGUI.DrawRect(rect, new Color(0.13f, 0.14f, 0.16f));
            float start = X(track.FirstEntrySeconds), end = X(track.LastEntrySeconds);
            EditorGUI.DrawRect(new Rect(start, rect.yMin, Mathf.Max(1f, end - start), rect.height), new Color(0.3f, 0.8f, 1f, 0.12f));
            EditorGUI.DrawRect(new Rect(X(track.ExitTime), rect.yMin, 1f, rect.height), Color.gray);
            if (track.IsStop)
                EditorGUI.DrawRect(new Rect(X(track.BrakeSeconds), rect.yMin, 2f, rect.height), new Color(0.72f, 0.4f, 0.9f));
            EditorGUI.DrawRect(new Rect(left, Y(CharacterMotionTrackAudit.PlantedContact), width, 1f), new Color(0.4f, 0.4f, 0.4f));
            Handles.BeginGUI();
            using (new Handles.DrawingScope(Color.white))
            {
                for (int i = 1; i < track.Poses.Length; i++)
                {
                    float a = X(track.TimeAt(i - 1)), b = X(track.TimeAt(i));
                    var before = track.Poses[i - 1].Contact;
                    var after = track.Poses[i].Contact;
                    Handles.color = leftColor;
                    Handles.DrawLine(new Vector2(a, Y(before.x)), new Vector2(b, Y(after.x)), 2f);
                    Handles.color = rightColor;
                    Handles.DrawLine(new Vector2(a, Y(before.y)), new Vector2(b, Y(after.y)), 2f);
                }
            }
            Handles.EndGUI();
            EditorGUI.DrawRect(new Rect(X(track.TimeAt(sample)), rect.yMin, 1f, rect.height), Color.white);
        }
    }
}
