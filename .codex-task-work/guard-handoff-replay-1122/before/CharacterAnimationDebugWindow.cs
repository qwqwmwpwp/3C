using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在 Play Mode 查看所选角色的动作、IK、历史和固定输入回放。</summary>
    internal sealed class CharacterAnimationDebugWindow : EditorWindow
    {
        private Vector2 scroll;
        private bool showHistory;
        private bool shiftPhase;
        private bool editorPaused;
        private long selectedSequence;
        private long firstSequence;
        private long latestSequence;
        private long displayedSequence;
        private CharacterAnimationDebugSnapshot currentSnapshot;
        private CharacterAnimationDebugSnapshot historySnapshot;
        private string ikSnapshot, matchSnapshot;

        [MenuItem("Window/3C/角色动画调试")]
        private static void Open() => GetWindow<CharacterAnimationDebugWindow>("角色动画调试");

        private void OnSelectionChange()
        {
            selectedSequence = 0;
            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (EditorApplication.isPlaying) Repaint();
        }

        private void OnGUI()
        {
            var selected = Selection.activeGameObject;
            var animator = selected ? selected.GetComponentInParent<CharacterBehaviorAnimator>(true) : null;
            if (!EditorApplication.isPlaying || !animator || EditorUtility.IsPersistent(animator))
            {
                EditorGUILayout.HelpBox("在播放模式下选择场景中的角色或其子对象。", MessageType.Info);
                return;
            }

            if (Event.current.type == EventType.Layout)
                ReadSnapshots(animator);
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                EditorGUILayout.LabelField(animator.name, EditorStyles.boldLabel);
                if (editorPaused)
                    EditorGUILayout.LabelField("编辑器已暂停", EditorStyles.miniLabel);
                DrawSnapshot(currentSnapshot);
                EditorGUILayout.LabelField("最近一次动作匹配", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(matchSnapshot ?? "", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("本次 IK 求值", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(ikSnapshot ?? "", EditorStyles.wordWrappedLabel);
                DrawHistory();
                EditorGUILayout.LabelField("固定输入对照", EditorStyles.boldLabel);
                var replay = animator.Replay;
                using (new EditorGUI.DisabledScope(replay != null && replay.Active))
                {
                    shiftPhase = EditorGUILayout.Toggle("错开半个跑步周期触发", shiftPhase);
                    if (GUILayout.Button("回放轻击后摇过半再移动")) animator.BeginReplay(GroundReplayCase.LateRecoveryMove, shiftPhase);
                    if (GUILayout.Button("回放轻击刚恢复移动时接第二刀")) animator.BeginReplay(GroundReplayCase.RecoveryCombo, shiftPhase);
                    if (GUILayout.Button("回放跑步急停")) animator.BeginReplay(GroundReplayCase.Stop, shiftPhase);
                    if (GUILayout.Button("回放制动中反向")) animator.BeginReplay(GroundReplayCase.ReverseAfterRelease, shiftPhase);
                    if (GUILayout.Button("回放持续跑动中直接左转")) animator.BeginReplay(GroundReplayCase.DirectLeft, shiftPhase);
                    if (GUILayout.Button("回放持续跑动中直接右转")) animator.BeginReplay(GroundReplayCase.DirectRight, shiftPhase);
                }
                if (animator.PreviousReplay != null)
                    EditorGUILayout.LabelField("上次：" + animator.PreviousReplay, EditorStyles.wordWrappedLabel);
                if (replay != null)
                    EditorGUILayout.LabelField(replay.Describe(), EditorStyles.wordWrappedLabel);

                if (animator.isActiveAndEnabled)
                {
                    EditorGUI.BeginChangeCheck();
                    var mode = (CharacterMatchMode)EditorGUILayout.EnumPopup("本实例调试匹配模式", animator.CurrentMatchMode);
                    if (EditorGUI.EndChangeCheck()) animator.DebugMatchMode = mode;
                    if (animator.DebugMatchMode.HasValue && GUILayout.Button("恢复配置中的匹配模式"))
                        animator.DebugMatchMode = null;
                }
            }
        }

        private void ReadSnapshots(CharacterBehaviorAnimator animator)
        {
            editorPaused = EditorApplication.isPaused;
            currentSnapshot = animator.ReadRuntimeDebug(out ICharacterAnimationDebugHistory history);
            ikSnapshot = animator.ReadIkDebug();
            matchSnapshot = animator.ReadMatchDebug();
            latestSequence = history?.LatestTransition ?? 0;
            if (latestSequence == 0)
            {
                selectedSequence = 0;
                historySnapshot = default;
                return;
            }

            firstSequence = history.FirstTransition;
            displayedSequence = selectedSequence == 0 ? latestSequence :
                Math.Max(firstSequence, Math.Min(latestSequence, selectedSequence));
            history.TryReadTransition(displayedSequence, out historySnapshot);
        }

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

            if (snapshot.Motion.IsValid)
            {
                EditorGUILayout.LabelField("渲染帧 / 固定步", $"{snapshot.Handoff.RenderFrame} / {snapshot.Motion.FrameNumber}");
                var handoff = snapshot.Handoff;
                var attack = handoff.Attack;
                string stage = StageName(attack.Phase);
                EditorGUILayout.LabelField("招式 / 固定步进度", attack.Move == CharacterAttackMove.None ? "无" :
                    $"{attack.Move} #{attack.InstanceId} · {stage} · {attack.Progress:P1}");
                EditorGUILayout.LabelField("普通移动获准", handoff.MoveApproved ? "是" : "否");
                EditorGUILayout.LabelField("期望 / 实际速度",
                    $"{snapshot.Motion.ExpectedSpeed:0.00} / {snapshot.Motion.ActualSpeed:0.00} 米/秒");
                Vector3 acceleration = snapshot.Motion.ActualAcceleration;
                EditorGUILayout.LabelField("实际水平加速度（世界 X / Z）", snapshot.Motion.HasAcceleration
                    ? $"{acceleration.x:0.00} / {acceleration.z:0.00} 米/秒²" : "等待连续固定步");
                EditorGUILayout.LabelField("运动阶段", $"{PhaseName(snapshot.Phase)} · {PhaseReason(snapshot.PhaseReason)}");
                if (snapshot.ReleaseDecision != CharacterReleaseDecision.None)
                {
                    string foot = snapshot.ObservedLeftSupport.HasValue ?
                        (snapshot.ObservedLeftSupport.Value ? "左脚" : "右脚") : "未观测到";
                    EditorGUILayout.LabelField("本帧收步决策", DescribeRelease(snapshot.ReleaseDecision));
                    EditorGUILayout.LabelField("观测支撑脚 / 松键速度", $"{foot} / {snapshot.ReleaseSpeed:0.00} 米/秒");
                }
                if (snapshot.Motion.Trajectory.IsValid)
                    EditorGUILayout.LabelField("短期预测速度",
                        $"{snapshot.Motion.Trajectory.FutureVelocity.magnitude:0.00} 米/秒（{CharacterGroundTrajectory.LookAheadSeconds:0.00} 秒后）");
                var braking = snapshot.Braking;
                if (braking.IsBraking)
                {
                    EditorGUILayout.LabelField("松键初估 / 已移动 / 预计剩余",
                        $"{braking.InitialStopDistance:0.000} / {braking.ActualStopTravel:0.000} / {braking.RemainingStopDistance:0.000} 米");
                    string brakePhase = braking.IsBrakeLeadIn ? "轻减速" :
                        braking.RemainingStopSeconds > 0f ? "主要制动" : "已停稳";
                    EditorGUILayout.LabelField("制动阶段 / 主要制动已移动",
                        $"{brakePhase} / {braking.MainBrakeTravel:0.000} 米");
                    EditorGUILayout.LabelField("初估停稳时间 / 预计余时",
                        $"{braking.InitialStopSeconds:0.00} / {braking.RemainingStopSeconds:0.00} 秒");
                }
                if (snapshot.StopClipTime.HasValue)
                    EditorGUILayout.LabelField("收步片段时间 / 制动结束标记",
                        $"{snapshot.StopClipTime.Value:0.000} / {snapshot.StopBrakeMarker:0.000} 秒");
                if (snapshot.StopPose.IsValid)
                    EditorGUILayout.LabelField("收步支撑 / 承重权重 / 入场速度",
                        $"{(snapshot.StopPose.LeftSupport ? "左脚" : "右脚")} / {snapshot.StopPose.Load:P0} / {snapshot.StopPose.EntryVelocity.magnitude:0.00} 米/秒");
            }
            else
                EditorGUILayout.LabelField("等待新的固定步。");

            var mix = snapshot.Handoff;
            DrawBody("上身", snapshot.UpperBody, mix, mix.Upper, mix.UpperWeight);
            DrawBody("下身", snapshot.LowerBody, mix, mix.Lower, mix.LowerWeight);
            var conflict = snapshot.Conflict;
            if (conflict.Rejected != null)
                EditorGUILayout.LabelField("首个未获准动作", $"{conflict.Rejected} · {conflict.Reason}（{conflict.Holder}）");
        }

        private static void DrawBody(string label, in CharacterAnimationDebugLayer owner, in CharacterDebugHandoff mix,
            in CharacterDebugClipMix local, float localWeight)
        {
            DrawLayer(label + (owner.IsBasePose ? "（跟随步态）" : "归属"), owner);
            if (!mix.Direct) return;
            float baseline = Mathf.Max(0f, 1f - mix.Coverage - localWeight);
            if (baseline > 0f) DrawClipMix($"默认姿势 {baseline:P0}", mix.Base);
            if (localWeight > 0f) DrawClipMix($"局部动作 {localWeight:P0}", local);
            if (mix.Coverage > 0f) DrawClipMix($"共享动作 {mix.Coverage:P0}", mix.Action);
        }

        private static string StageName(CharacterBehaviorPhase phase) => phase == CharacterBehaviorPhase.Startup ? "前摇" :
            phase == CharacterBehaviorPhase.Active ? "关键段" : "后摇";

        private static void DrawClipMix(string label, CharacterDebugClipMix mix)
        {
            string detail = mix.Target == null ? "无片段" : $"{mix.Target} · 进度 {mix.Phase:P1} · 源内权重 {mix.TargetWeight:P1}";
            if (mix.Outgoing != null) detail += $"  ·  淡出 {mix.Outgoing} {mix.OutgoingWeight:P1}";
            if (mix.OtherCount > 0) detail += $"  ·  其余 {mix.OtherCount} 段合计 {Mathf.Max(0f, 1f - mix.TargetWeight - mix.OutgoingWeight):P1}";
            EditorGUILayout.LabelField(label, detail, EditorStyles.wordWrappedLabel);
        }

        private static string DescribeRelease(CharacterReleaseDecision decision) => decision switch
        {
            CharacterReleaseDecision.ContinueStride => "延续当前半步，等待支撑或主制动",
            CharacterReleaseDecision.WaitForEvaluation => "等待 Animator 求值",
            CharacterReleaseDecision.EnterOnMatch => "按匹配入点开始收步",
            CharacterReleaseDecision.EnterOnContact => "脚步已进入承重区，提前接入",
            CharacterReleaseDecision.EnterOnBraking => "已进入主要制动，接入收步",
            CharacterReleaseDecision.FeetOverridden => "双脚交给其他动作，取消收步",
            CharacterReleaseDecision.Finished => "收步播放完成",
            CharacterReleaseDecision.MissingStop => "收步资源不可用，回到待机",
            CharacterReleaseDecision.Interrupted => "运动或行为已改变，结束收步",
            _ => "沿用本次收步与支撑侧"
        };

        private static void DrawLayer(string label, in CharacterAnimationDebugLayer layer)
        {
            if (layer.Key == null)
            {
                EditorGUILayout.LabelField(label, "无获选需求");
                return;
            }

            EditorGUILayout.LabelField(label, $"{layer.Key}  #{layer.InstanceId}");
            string detail = PlaybackReason(layer);
            if (layer.SamplePhase.HasValue)
                detail += $" · 渲染采样 {layer.SamplePhase.Value:P1}（{StageName(layer.CoveragePhase)}）";
            if (layer.UsedResourceFallback)
                detail = $"{detail} · 素材回退";
            if (layer.SentStart.HasValue)
            {
                string command = layer.Command == CharacterAnimationPlaybackCommand.CrossFade ? "淡入" : "播放";
                detail = $"{detail} · {command}起点 {layer.SentStart.Value:0.######}（归一化）";
            }
            EditorGUILayout.LabelField($"{layer.StateName} · {detail}", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawHistory()
        {
            showHistory = EditorGUILayout.Foldout(showHistory, "最近变化回看", true);
            if (!showHistory)
                return;
            if (latestSequence == 0)
            {
                EditorGUILayout.LabelField("暂无变化记录。");
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(displayedSequence == firstSequence))
                    if (GUILayout.Button("上一条"))
                        selectedSequence = displayedSequence - 1;

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{displayedSequence - firstSequence + 1} / {latestSequence - firstSequence + 1}",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(displayedSequence == latestSequence))
                    if (GUILayout.Button("下一条"))
                        selectedSequence = displayedSequence + 1;

                using (new EditorGUI.DisabledScope(selectedSequence == 0))
                    if (GUILayout.Button("最新"))
                        selectedSequence = 0;
            }

            DrawSnapshot(historySnapshot);
        }

        private static string PhaseName(CharacterLocomotionPhase phase) => phase switch
        {
            CharacterLocomotionPhase.Starting => "起步",
            CharacterLocomotionPhase.Steady => "持续移动",
            CharacterLocomotionPhase.Turning => "反向转身",
            CharacterLocomotionPhase.Stopping => "收步",
            CharacterLocomotionPhase.BrakeLeadIn => "松键随步",
            CharacterLocomotionPhase.Dash => "冲刺",
            _ => "待机"
        };

        private static string PhaseReason(CharacterLocomotionPhaseReason reason) => reason switch
        {
            CharacterLocomotionPhaseReason.BelowMovementThreshold => "实际速度低于移动阈值",
            CharacterLocomotionPhaseReason.StartedMoving => "达到移动阈值",
            CharacterLocomotionPhaseReason.SustainedMovement => "持续移动",
            CharacterLocomotionPhaseReason.ReversedDirection => "移动方向反转",
            CharacterLocomotionPhaseReason.Obstructed => "移动受阻",
            CharacterLocomotionPhaseReason.MovementNotApproved => "未批准普通移动",
            CharacterLocomotionPhaseReason.DashApproved => "行为已批准冲刺",
            CharacterLocomotionPhaseReason.GroundBraking => "松键收步，等待落脚",
            CharacterLocomotionPhaseReason.GroundBrakeLeadIn => "轻减速，延续当前半步",
            _ => "等待运动观测"
        };

        private static string PlaybackReason(in CharacterAnimationDebugLayer layer) => layer.Reason switch
        {
            CharacterAnimationPlaybackReason.KeepInstance => "保持当前实例",
            CharacterAnimationPlaybackReason.KeepSharedResource => "共用素材，保持进度",
            _ when !layer.SentStart.HasValue => "未下发播放指令",
            CharacterAnimationPlaybackReason.Enter => "进入获选状态",
            CharacterAnimationPlaybackReason.SwitchState => "切换素材状态",
            CharacterAnimationPlaybackReason.ReplayInstance => "新实例重新播放",
            CharacterAnimationPlaybackReason.RestoreState => "控制器偏离目标，恢复获选状态",
            CharacterAnimationPlaybackReason.MatchPose => "进入匹配姿势",
            CharacterAnimationPlaybackReason.SynchronizeGait => "按左右脚触地相位衔接",
            _ => "已下发播放指令"
        };
    }
}
