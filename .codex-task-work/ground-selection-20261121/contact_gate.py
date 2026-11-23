from pathlib import Path
p=Path('Assets/CharacterBehavior/Runtime/CharacterMotionMatcher.cs');s=p.read_text()
s=s.replace('                bool admitted = false;', '                bool inWindow = false, admitted = false;')
s=s.replace('                    admitted = true;\n                    var cost', '''                    inWindow = true;
                    if (OppositeSupport(feet.Contact, track.Poses[i].Contact)) continue;
                    admitted = true;
                    var cost''')
s=s.replace('ReportTrack(track, admitted ? "入选比较" : "无合法入点");','ReportTrack(track, admitted ? "入选比较" : inWindow ? "入点承重脚冲突" : "无合法入点");')
s=s.replace('        private string Excluded(', '''        private static bool OppositeSupport(Vector2 live, Vector2 candidate) =>
            live.x >= 0.7f && live.y < 0.45f && candidate.y >= 0.7f && candidate.x < 0.45f ||
            live.y >= 0.7f && live.x < 0.45f && candidate.x >= 0.7f && candidate.y < 0.45f;

        private string Excluded(''')
p.write_text(s)
p=Path('Assets/CharacterBehavior/Editor/CharacterMotionDatabaseEditor.cs');s=p.read_text()
s=s.replace('new Dictionary<string, Motion>()','new Dictionary<string, AnimatorState>()')
a=s.index('                    foreach (string path in track.States)');b=s.index('                    try { next.Poses',a)
s=s[:a]+'''                    var next = track.CopyForBake();
                    if (db.Format == 0 && next.Use == GroundMotionUse.Legacy) UpgradeLegacy(next);
                    Validate(next);
                    foreach (string path in next.States)
                    {
                        if (string.IsNullOrWhiteSpace(path) || !used.Add(path) || !paths.TryGetValue(path, out var state) ||
                            state.motion != next.Clip)
                            throw new InvalidOperationException($"状态 {path} 缺失、重复或使用了不同的片段。");
                        ValidatePlayback(next, state, path);
                    }
'''+s[b:]
s=s.replace('        private static bool Finite(float v)', '''        private static void ValidatePlayback(CharacterMotionTrack track, AnimatorState state, string path)
        {
            if (Mathf.Abs(state.speed - 1f) > 0.0001f || state.timeParameterActive || state.mirror || state.mirrorParameterActive ||
                state.cycleOffsetParameterActive || Mathf.Abs(state.cycleOffset) > 0.0001f)
                throw new InvalidOperationException($"{path}：入点采样要求状态原速、无镜像、无偏移和外部时间驱动。");
            if (track.IsStop && (!state.speedParameterActive || state.speedParameter != "StopRate"))
                throw new InvalidOperationException($"{path}：收步状态需要 StopRate 播放参数。");
            if (!track.IsLoop && !track.IsStop && state.speedParameterActive)
                throw new InvalidOperationException($"{path}：单次起步、转向按素材原速播放。");
            if (!track.IsLoop) return;
            string gaitRate = track.Gait == GroundTrackGait.Walk ? "WalkRate" :
                track.Gait == GroundTrackGait.Sprint ? "SprintRate" : track.Gait == GroundTrackGait.Run ? "RunRate" : "LocomotionRate";
            if (!state.speedParameterActive || state.speedParameter != gaitRate && state.speedParameter != "LocomotionRate")
                throw new InvalidOperationException($"{path}：循环状态请选择 LocomotionRate 或 {gaitRate}。");
        }

        private static bool Finite(float v)''')
s=s.replace('Dictionary<string, Motion> paths','Dictionary<string, AnimatorState> paths').replace('state.state.name, state.state.motion);','state.state.name, state.state);')
s=s.replace('            if (track.Use == GroundMotionUse.Turn && track.PathSource == GroundPathSource.Straight)', '''            if (track.Use == GroundMotionUse.Turn && track.Side == GroundTrackSide.Either)
                throw new InvalidOperationException($"{clip}：请标明转向侧。");
            if (track.Use == GroundMotionUse.Turn && track.PathSource == GroundPathSource.Straight)''')
p.write_text(s)
# Actual prefab parameters are the default for existing authored cycles.

