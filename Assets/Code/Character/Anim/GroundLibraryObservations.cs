using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 按库解释同一份角色原始姿态与固定步预测，普通选姿和交还预搜共用投影入口。
    internal sealed class GroundLibraryObservations
    {
        private readonly GroundFeatureLayout[] layouts;
        internal readonly string[] Names;
        private readonly Vector2 fallbackWindows;
        internal GroundFeatureQuery[] Features;
        internal CharacterPresentationInput Input;

        // 固定本次角色装配的评分布局和无自定义布局时的轨迹窗。
        internal GroundLibraryObservations(GroundSearchLibraries libraries, Vector2 fallbackWindows)
        {
            layouts = libraries?.Layouts ?? Array.Empty<GroundFeatureLayout>();
            Names = libraries?.Names ?? Array.Empty<string>();
            this.fallbackWindows = fallbackWindows;
        }

        // 更新物理用途查询的轨迹基准，首选窗不可用时尝试其他已装配的窗长。
        internal bool TryAnyGoal(float? ahead, out CharacterMatchTrajectory goal)
        {
            goal = ahead.HasValue ? default : Input.Motion.Trajectory.Match;
            if (goal.Valid) return true;
            foreach (var layout in layouts)
            {
                if (layout == null) continue;
                goal = Project(ahead, layout.Active ? layout.Windows : fallbackWindows);
                if (goal.Valid) return true;
            }
            return false;
        }

        // 固定库自己的特征查询和预测窗；外部控制器未提供所需时刻时保留具体淘汰原因。
        internal bool TryRead(int index, float? ahead, GroundFeatureGroups groups, in CharacterMatchTrajectory baseline,
            out CharacterMatchTrajectory goal, out GroundFeatureQuery features, out bool pelvis,
            out string issue, out int channel)
        {
            var layout = layouts[index];
            goal = baseline;
            features = Features != null && index < Features.Length ? Features[index] : default;
            pelvis = layout?.LegacyPelvis == true;
            channel = -1;
            issue = null;
            if (layout == null) { issue = "没有可用评分布局"; return false; }
            if (layout.Active)
            {
                if (!features.Active) { issue = "尚未采集所需骨骼通道"; return false; }
                channel = features.MissingChannel(groups);
                if (channel >= 0) { issue = "缺少所需骨骼或历史样本，通道"; return false; }
                if (!HasWindows(layout, groups, goal))
                    goal = Project(ahead, layout.Windows);
                if (!HasWindows(layout, groups, goal))
                { issue = "当前轨迹未提供此库的预测窗口"; return false; }
            }
            else if (!goal.Valid || goal.Windows != fallbackWindows)
            {
                // 兼容脚姿也对近、远两个真实时刻评分；外部单步只接受其实际提供的窗口。
                goal = Project(ahead, fallbackWindows);
                if (!goal.Valid || goal.Windows != fallbackWindows)
                { issue = "当前轨迹未提供兼容脚姿评分所需的近远预测窗口"; return false; }
            }
            if (!goal.Valid) { issue = "没有可信的运动轨迹"; return false; }
            return true;
        }

        // 只检查本次启用的轨迹时刻，具有不同骨骼通道的库仍可复用完全相同的轨迹。
        private static bool HasWindows(GroundFeatureLayout layout, GroundFeatureGroups groups, in CharacterMatchTrajectory goal)
        {
            if (!goal.Valid) return false;
            for (int i = 0; i < layout.Channels.Length; i++)
            {
                var c = layout.Channels[i];
                if (!c.Path || c.Weight <= 0f || (layout.Available & (1u << i)) == 0 || (c.Group & groups) == 0) continue;
                if (!Mathf.Approximately(c.Time, goal.Windows.x) && !Mathf.Approximately(c.Time, goal.Windows.y)) return false;
            }
            return true;
        }

        // 在同一角色时间起点或预计交还起点读取本库要求的两个实际轨迹样本。
        private CharacterMatchTrajectory Project(float? ahead, Vector2 windows)
        {
            if (!ahead.HasValue) return Input.MatchAt(windows);
            CharacterForecastQuery.TryMatch(Input.Forecast, Input.Time, ahead.Value, windows, out var goal);
            return goal;
        }
    }
}
