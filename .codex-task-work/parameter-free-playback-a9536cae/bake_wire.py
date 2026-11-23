from pathlib import Path
p=Path('Assets/CharacterBehavior/Editor/CharacterMotionDatabaseEditor.cs')
s=p.read_text()
s=s.replace('            var paths = new Dictionary', '''            bool direct = controller.parameters.Length == 0;
            var playback = direct ? CharacterClipLibraryBaker.Build(controller) : null;
            var paths = new Dictionary''')
s=s.replace('ValidatePlayback(next, state, path);','ValidatePlayback(next, state, path, direct);\n                        bool scale = next.IsLoop && (direct ? !next.FixedCycleSpeed : state.speedParameterActive);')
s=s.replace('next.ScalesWithSpeed = next.IsLoop && state.speedParameterActive;', 'next.ScalesWithSpeed = scale;').replace('next.ScalesWithSpeed != state.speedParameterActive','next.ScalesWithSpeed != scale')
s=s.replace('            db.BakedTracks = baked;','            db.Playback = playback;\n            db.BakedTracks = baked;')
s=s.replace('ValidatePlayback(CharacterMotionTrack track, AnimatorState state, string path)','ValidatePlayback(CharacterMotionTrack track, AnimatorState state, string path, bool direct)')
s=s.replace('            if (track.IsStop && (!state.speedParameterActive','            if (direct) return; // 独立片段时钟由 C# 根据轨道与播放计划调速。\n            if (track.IsStop && (!state.speedParameterActive')
p.write_text(s)

