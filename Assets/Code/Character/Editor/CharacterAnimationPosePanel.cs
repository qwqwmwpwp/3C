using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal sealed class CharacterAnimationPosePanel
    {
        private readonly bool[] expandedLimb = { true, false, false, false };
        private readonly CharacterEvaluatedAnimationFrame[] frozenFrames =
            new CharacterEvaluatedAnimationFrame[CharacterAnimationEvaluationHistory.Capacity];
        private CharacterAnimationEvaluationHistory history;
        private CharacterEvaluatedAnimationFrame frame;
        private int revision, frozenCount, index;
        private bool expanded = true, sceneProbes;

        public void Read(CharacterAnimationEvaluationHistory source)
        {
            long previous = frame.Sequence;
            int sourceRevision = source?.Revision ?? 0;
            bool changed = source != history || sourceRevision != revision;
            if (changed)
            {
                history = source;
                revision = sourceRevision;
                frozenCount = 0;
            }
            if (frozenCount > 0) frame = frozenFrames[index];
            else if (history == null || !history.TryReadLatest(out frame)) frame = default;
            if (sceneProbes && (changed || previous != frame.Sequence)) SceneView.RepaintAll();
        }

        public void Draw(string bindingStatus)
        {
            EditorGUI.BeginChangeCheck();
            expanded = EditorGUILayout.Foldout(expanded, "动画求值帧", true);
            if (EditorGUI.EndChangeCheck() && sceneProbes) SceneView.RepaintAll();
            if (!expanded) return;
            if (!string.IsNullOrEmpty(bindingStatus))
                EditorGUILayout.LabelField(bindingStatus, EditorStyles.wordWrappedLabel);
            if (frame.Sequence <= 0)
            {
                EditorGUILayout.LabelField("等待新的动画求值。");
                return;
            }

            DrawNavigation();
            var fixedStep = frame.FixedStep > 0 ? frame.FixedStep.ToString() : "—";
            EditorGUILayout.LabelField("求值序号 / 输入准备帧 / 固定步", $"{frame.Sequence} / {frame.PreparedRenderFrame} / {fixedStep}");
            EditorGUILayout.LabelField("求值间隔", $"{frame.DeltaTime * 1000f:F2} 毫秒");
            if (frame.InertiaBound == 0) EditorGUILayout.LabelField("惯性", "未绑定骨骼");
            else
            {
                EditorGUILayout.LabelField("胸 / 左臂 / 右臂惯性抑制", Percentages(frame.InertiaSuppression));
                EditorGUILayout.LabelField("左 / 右手握持额外抑制", $"{frame.GripInertiaSuppression.x:P0} / {frame.GripInertiaSuppression.y:P0}");
                EditorGUILayout.LabelField("惯性强度 / 胸臂附加转角之和",
                    $"{frame.InertiaStrength:P0} / {FormatVector(frame.InertiaAppliedDegrees, "F1")}°");
            }
            EditorGUI.BeginChangeCheck();
            sceneProbes = EditorGUILayout.ToggleLeft("在场景视图显示探线与本帧脚踝", sceneProbes);
            if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();
            DrawLimb(CharacterIkLimb.LeftHand, "左手", true);
            DrawLimb(CharacterIkLimb.RightHand, "右手", true);
            DrawLimb(CharacterIkLimb.LeftFoot, "左脚", false);
            DrawLimb(CharacterIkLimb.RightFoot, "右脚", false);
        }

        private void DrawNavigation()
        {
            int displayedCount = frozenCount;
            bool reviewing = displayedCount > 0;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(reviewing ? "恢复实时" : "冻结并回看"))
                {
                    if (reviewing) frozenCount = 0;
                    else
                    {
                        frozenCount = history.CopyTo(frozenFrames);
                        index = Mathf.Max(0, frozenCount - 1);
                        while (index > 0 && frozenFrames[index].Sequence > frame.Sequence) index--;
                    }
                }
                using (new EditorGUI.DisabledScope(!reviewing || index == 0))
                    if (GUILayout.Button("上一求值帧")) index--;
                using (new EditorGUI.DisabledScope(!reviewing || index >= displayedCount - 1))
                    if (GUILayout.Button("下一求值帧")) index++;
            }
            if (reviewing)
                index = EditorGUILayout.IntSlider("回看位置", index + 1, 1, displayedCount) - 1;
        }

        private void DrawLimb(CharacterIkLimb limb, string label, bool hand)
        {
            var sample = frame.Read(limb);
            if (sample.Valid == 0)
            {
                EditorGUILayout.LabelField(label, "无有效骨链姿势");
                return;
            }
            ref bool open = ref expandedLimb[(int)limb];
            open = EditorGUILayout.Foldout(open, label, true);
            if (!open) return;
            using (new EditorGUI.IndentLevelScope())
            {
                if (sample.HasIk != 0)
                {
                    EditorGUILayout.LabelField("许可 / IK 位置 / 旋转", $"{sample.Gate:P0} / {sample.PositionWeight:P0} / {sample.RotationWeight:P0}");
                    EditorGUILayout.LabelField("候选来源 / 当前来源", $"{Candidates(sample)} / {Source(sample)}");
                    if (sample.HasTarget != 0)
                        EditorGUILayout.LabelField("最终末端到输入目标", $"{Vector3.Distance(sample.Final.Tip.position, sample.Target.position):F3} 米");
                    if (hand)
                    {
                        if (sample.GripCoverageKnown != 0 || limb == CharacterIkLimb.LeftHand)
                            EditorGUILayout.LabelField("持盾片段可见权重", sample.GripCoverageKnown != 0 ? $"{sample.GripCoverage:P1}" : "未观测");
                        if (sample.HasGripPoint != 0 || limb == CharacterIkLimb.LeftHand)
                            EditorGUILayout.LabelField("最终手腕到握点",
                                sample.HasGripPoint != 0 ? $"{Vector3.Distance(sample.Final.Tip.position, sample.GripPoint.position):F3} 米 / " +
                                    $"{Quaternion.Angle(sample.Final.Tip.rotation, sample.GripPoint.rotation):F1}°" : "无有效握点");
                    }
                }
                else EditorGUILayout.LabelField("仅姿势观测", "无有效 IK 通道");
                DrawStage("基础 FK", sample.Fk, hand && sample.HasSpeed != 0);
                DrawStage("惯性后", sample.Inertial, hand && sample.HasSpeed != 0);
                DrawStage("最终 IK", sample.Final, hand && sample.HasSpeed != 0);
                if (!hand && sample.HasIk != 0) DrawFoot(sample);
            }
        }

        private static void DrawStage(string name, in CharacterEvaluatedLimbPose pose, bool showSpeed)
        {
            EditorGUILayout.LabelField(name + " 位置 / 朝向",
                $"{FormatVector(pose.Tip.position, "F3")} 米 / {FormatVector(pose.Tip.rotation.eulerAngles, "F1")}°",
                EditorStyles.wordWrappedLabel);
            if (showSpeed)
                EditorGUILayout.LabelField(name + " 肩 / 腕角速度", $"{pose.AngularSpeed.x:F1} / {pose.AngularSpeed.y:F1} 度/秒");
        }

        private static void DrawFoot(in CharacterEvaluatedLimb sample)
        {
            var probe = sample.Probe;
            EditorGUILayout.LabelField("接触锁点", sample.Locked != 0 ? $"{FormatVector(sample.AnchorWorld, "F3")} 米 / #{sample.ObjectId}" : "未锁定");
            if (sample.Locked != 0)
            {
                EditorGUILayout.LabelField("平台平面锁点偏差", $"{sample.LockOffset:F3} 米");
                EditorGUILayout.LabelField("相对平台的帧间滑移", sample.HasSlip != 0 ? $"{sample.Slip:F3} 米" : "等待连续同锁点求值");
            }
            if (probe.Count == 0)
            {
                EditorGUILayout.LabelField("地面探测", "未发射探线");
                return;
            }
            for (int i = 0; i < probe.Count; i++)
            {
                var ray = probe.Ray(i);
                string name = i == 0 ? "预测探线" : i == 1 ? "后侧探线" : "前侧探线";
                EditorGUILayout.LabelField(name + " 起点", FormatVector(ray.Origin, "F3"));
                string hit = ray.Platform == 0 ? "无可行走命中" :
                    $"{FormatVector(ray.Point, "F3")} 米 / 法线 {FormatVector(ray.Normal, "F2")} / #{ray.Platform}";
                EditorGUILayout.LabelField(i == probe.AcceptedRay ? "采用命中" : "命中", hit, EditorStyles.wordWrappedLabel);
            }
            if (probe.AcceptedRay < 0) EditorGUILayout.LabelField("地面目标", "当前踝位置无可采用的探地结果");
        }

        public void DrawScene()
        {
            if (!expanded || !sceneProbes || frame.Sequence <= 0) return;
            DrawFootScene(frame.Read(CharacterIkLimb.LeftFoot), new Color(0.15f, 0.75f, 1f));
            DrawFootScene(frame.Read(CharacterIkLimb.RightFoot), new Color(1f, 0.6f, 0.15f));
        }

        private static void DrawFootScene(in CharacterEvaluatedLimb foot, Color color)
        {
            if (foot.Valid == 0 || foot.Probe.Count == 0) return;
            var probe = foot.Probe;
            for (int i = 0; i < probe.Count; i++)
            {
                var ray = probe.Ray(i);
                using (new Handles.DrawingScope(i == probe.AcceptedRay ? color : Color.Lerp(color, Color.gray, 0.7f)))
                {
                    Vector3 end = ray.Platform == 0 ? ray.Origin - probe.Up * probe.Length : ray.Point;
                    Handles.DrawLine(ray.Origin, end, i == probe.AcceptedRay ? 3f : 1f);
                    if (ray.Platform != 0) Handles.DrawLine(ray.Point, ray.Point + 0.08f * ray.Normal);
                }
            }
            using (new Handles.DrawingScope(color))
            {
                Vector3 ankle = foot.Inertial.Tip.position;
                Handles.DrawWireDisc(ankle, probe.Up, 0.028f);
                Handles.DrawLine(ankle, foot.Final.Tip.position, 2f);
                if (foot.Locked != 0) Handles.DrawWireDisc(foot.AnchorWorld, probe.Up, 0.018f);
            }
        }

        private static string FormatVector(Vector3 value, string format) => value.ToString(format);
        private static string Percentages(Vector3 value) => $"{value.x:P0} / {value.y:P0} / {value.z:P0}";

        private static string Candidates(in CharacterEvaluatedLimb sample)
        {
            if (sample.CandidateMask == 0) return "无";
            string text = (sample.CandidateMask & (1 << (int)CharacterIkSource.Animation)) != 0 ? "动画 " : "";
            if ((sample.CandidateMask & (1 << (int)CharacterIkSource.Scene)) != 0) text += $"场景×{sample.SceneCount} ";
            if ((sample.CandidateMask & (1 << (int)CharacterIkSource.Ground)) != 0) text += "地面";
            return text.TrimEnd();
        }

        private static string Source(in CharacterEvaluatedLimb sample) => sample.Source switch
        {
            CharacterIkSource.Animation => "动画目标骨",
            CharacterIkSource.Scene => $"场景目标 #{sample.ObjectId} / 实例 {sample.Instance}",
            CharacterIkSource.Ground => $"地面 #{sample.ObjectId}",
            _ => "无"
        };
    }
}
