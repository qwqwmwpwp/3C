using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 提供数据组烘焙命令及已发布轨道的覆盖摘要。
    internal static class CharacterMotionDatabaseBaker
    {
        private const string BakeMenu = "Assets/3C/烘焙地面动作库";
        private const string SamplesMenu = "Assets/3C/查看动作库采样数";

        // 仅允许对选中的动作库在编辑模式烘焙。
        [MenuItem(BakeMenu, true)]
        private static bool CanBake() => HasSelectedDatabase() && !EditorApplication.isPlayingOrWillChangePlaymode;

        // 对所选动作库的全部数据组轨道重新采样。
        [MenuItem(BakeMenu)]
        private static void BakeSelected()
        {
            var database = (CharacterMotionDatabase)Selection.activeObject;
            try { CharacterMotionGroupBaker.Bake(database, false); }
            catch (Exception error) { Debug.LogError($"动作数据库采样失败：{error.Message}", database); }
        }

        private const string ChangedMenu = "Assets/3C/烘焙变更的地面轨道";

        // 使用同一编辑模式条件启用增量烘焙。
        [MenuItem(ChangedMenu, true)]
        private static bool CanBakeChanged() => CanBake();

        // 使用素材依赖哈希，重新采样所选动作库中变更的轨道。
        [MenuItem(ChangedMenu)]
        private static void BakeChanged()
        {
            var database = (CharacterMotionDatabase)Selection.activeObject;
            try { CharacterMotionGroupBaker.Bake(database, true); }
            catch (Exception error) { Debug.LogError($"动作数据库采样失败：{error.Message}", database); }
        }

        // 采样列表命令只处理单个选中的动作库。
        [MenuItem(SamplesMenu, true)]
        private static bool HasSelectedDatabase() =>
            Selection.objects.Length == 1 && Selection.activeObject is CharacterMotionDatabase;

        // 列出已发布轨道的姿势数和地面动作覆盖情况。
        [MenuItem(SamplesMenu)]
        private static void ShowSamples()
        {
            var database = (CharacterMotionDatabase)Selection.activeObject;
            var summary = new System.Text.StringBuilder(database.name);
            var tracks = database.BakedTracks ?? Array.Empty<CharacterMotionTrack>();
            foreach (var track in tracks)
            {
                if (track == null || !track.Clip) continue;
                string label = track.States?.Length > 0 ? $"{track.States[0]} · {track.Clip.name}" : track.Clip.name;
                summary.AppendLine().Append(label)
                    .Append(" · ").Append(track.Poses?.Length ?? 0).Append(" 个姿势");
            }
            AppendCoverage(summary, tracks);
            Debug.Log(summary.ToString(), database);
        }

        // 按步态和用途汇报尚未覆盖的地面动作。
        private static void AppendCoverage(System.Text.StringBuilder summary, CharacterMotionTrack[] tracks)
        {
            foreach (var gait in new[] { GroundTrackGait.Walk, GroundTrackGait.Run, GroundTrackGait.Sprint })
            {
                // 查找此步态下满足用途、侧别和角度的轨道。
                bool Has(GroundMotionUse use, GroundTrackSide side = GroundTrackSide.Either, float angle = 0f) =>
                    Array.Exists(tracks, t => t != null && t.ReadyForSearch && (t.Gait == gait || t.Gait == GroundTrackGait.Any) &&
                        t.Use == use && (side == GroundTrackSide.Either || t.Side == side) &&
                        (angle <= 0f || Mathf.Abs(t.TurnDegrees - angle) < 5f));

                if (!Has(GroundMotionUse.Cycle)) summary.AppendLine().Append($"{gait} 缺少循环");
                if (!Has(GroundMotionUse.Start)) summary.AppendLine().Append($"{gait} 缺少独立起步");
                foreach (var side in new[] { GroundTrackSide.Left, GroundTrackSide.Right })
                {
                    if (!Has(GroundMotionUse.Stop, side)) summary.AppendLine().Append($"{gait} 缺少 {side} 脚收步");
                    if (!Has(GroundMotionUse.Cycle, side)) summary.AppendLine().Append($"{gait} 缺少 {side} 弧线循环");
                    foreach (int angle in new[] { 90, 180 })
                        if (!Has(GroundMotionUse.Turn, side, angle))
                            summary.AppendLine().Append($"{gait} 缺少 {side} {angle}° 转向");
                }
                foreach (var from in new[] { GroundTrackGait.Walk, GroundTrackGait.Run, GroundTrackGait.Sprint })
                    if (Math.Abs((int)from - (int)gait) == 1 && !Array.Exists(tracks, t => t != null &&
                        t.ReadyForSearch && t.Use == GroundMotionUse.Transition && t.Gait == gait && t.FromGait == from))
                        summary.AppendLine().Append($"{from} → {gait} 缺少换步");
            }
        }
    }
}
