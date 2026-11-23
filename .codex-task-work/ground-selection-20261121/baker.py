from pathlib import Path
R=Path('Assets/CharacterBehavior/Runtime')
p=R/'CharacterMotionDatabase.cs'; s=p.read_text()
s=s.replace('        public bool ReadyForSearch','''        public CharacterMotionTrack CopyForBake()
        {
            var copy = (CharacterMotionTrack)MemberwiseClone();
            copy.States = (string[])States?.Clone();
            copy.ReferencePath = (GroundPathKey[])ReferencePath?.Clone();
            return copy;
        }

        public bool ReadyForSearch''').replace('        [SerializeField] internal CharacterMotionTrack[] Tracks;', '''        [SerializeField] internal CharacterMotionTrack[] Tracks;
        [SerializeField, HideInInspector] internal CharacterMotionTrack[] BakedTracks;
        [SerializeField, HideInInspector] internal Avatar BakedAvatar;
        [SerializeField, HideInInspector] internal RuntimeAnimatorController BakedController;''')
p.write_text(s)
p=R/'CharacterMotionMatcher.cs';s=p.read_text().replace('database.Tracks','database.BakedTracks').replace('database.Avatar','database.BakedAvatar').replace('database.Controller','database.BakedController')
p.write_text(s)
p=Path('Assets/CharacterBehavior/Editor/CharacterMotionDatabaseEditor.cs');s=p.read_text()
s=s.replace('track.Poses?.Length ?? 0','Array.Find(database.BakedTracks ?? database.Tracks, t => t != null && t.Clip == track.Clip)?.Poses?.Length ?? 0')
s=s.replace('GUILayout.Button("按角色 Avatar 重新采样")','GUILayout.Button(database.Format == 0 ? "升级并烘焙动作库" : "校验并烘焙动作库")')
s=s.replace('var baked = new CharacterFootPose[db.Tracks.Length][];', 'var baked = new CharacterMotionTrack[db.Tracks.Length];')
s=s.replace('                    baked[i] = Sample(animator, track);','''                    var next = track.CopyForBake();
                    if (db.Format == 0 && next.Use == GroundMotionUse.Legacy) UpgradeLegacy(next);
                    Validate(next);
                    try { next.Poses = Sample(animator, next); }
                    catch (Exception error) { throw new InvalidOperationException($"{next.Clip.name}：{error.Message}", error); }
                    next.Seconds = next.Clip.length;
                    baked[i] = next;''')
a=s.index('            for (int i',s.index('Undo.RecordObject'));b=s.index('            EditorUtility.SetDirty',a)
s=s[:a]+'''            db.BakedTracks = baked;
            db.BakedAvatar = db.Avatar;
            db.BakedController = db.Controller;
            db.Format = 1;
            for (int i = 0; i < baked.Length; i++)
            {
                var author = baked[i].CopyForBake();
                author.Poses = null;
                author.RootPath = null;
                db.Tracks[i] = author;
            }
'''+s[b:]
a=s.index('        private static void Collect')
s=s[:a]+'''        private static void UpgradeLegacy(CharacterMotionTrack track)
        {
            string state = track.States[0];
            if (state == "Base Layer.walk" || state == "Base Layer.move" || state == "Base Layer.sprint")
            {
                track.Use = GroundMotionUse.Cycle;
                track.Loop = true;
                track.Gait = state == "Base Layer.walk" ? GroundTrackGait.Walk :
                    state == "Base Layer.sprint" ? GroundTrackGait.Sprint : GroundTrackGait.Run;
            }
            else if (state == "Base Layer.run_stop_left" || state == "Base Layer.run_stop_right")
            {
                track.Use = GroundMotionUse.Stop;
                track.Gait = GroundTrackGait.Run;
                track.Side = state.EndsWith("_left", StringComparison.Ordinal) ? GroundTrackSide.Left : GroundTrackSide.Right;
            }
            else throw new InvalidOperationException($"{state}：请显式选择用途、步态与循环方式。");
        }

        private static void Validate(CharacterMotionTrack track)
        {
            string clip = track.Clip.name;
            float length = track.Clip.length;
            if (track.Use == GroundMotionUse.Legacy || track.Loop != track.Clip.isLooping ||
                track.Loop != (track.Use == GroundMotionUse.Cycle))
                throw new InvalidOperationException($"{clip}：请检查用途、片段循环设置与 Loop。");
            if (track.MinimumSpeed < 0f || track.MaximumSpeed > 0f && track.MaximumSpeed < track.MinimumSpeed ||
                track.FirstEntrySeconds < 0f || track.LastEntrySeconds < track.FirstEntrySeconds ||
                track.LastEntrySeconds > length + 0.001f || track.ExitSeconds > length + 0.001f ||
                !track.IsLoop && track.ExitSeconds > 0f && track.ExitSeconds <= track.LastEntrySeconds)
                throw new InvalidOperationException($"{clip}：速度、入点或退出区间无效。");
            if (track.IsStop && (track.Side == GroundTrackSide.Either || track.BrakeSeconds <= track.FirstEntrySeconds ||
                track.BrakeSeconds > length))
                throw new InvalidOperationException($"{clip}：收步需要承重脚及有效的主制动标记。");
            if (!track.IsLoop && !track.IsStop && track.LastEntrySeconds + CharacterMatchTrajectory.FarSeconds > length + 0.001f)
                throw new InvalidOperationException($"{clip}：末入点之后必须覆盖远端预测时间窗。");
            if (track.Use == GroundMotionUse.Turn && track.PathSource == GroundPathSource.Straight)
                throw new InvalidOperationException($"{clip}：转向需要根轨迹或作者参考轨迹。");
            if (track.PathSource != GroundPathSource.Authored) return;
            var path = track.ReferencePath;
            if (path == null || path.Length < 2 || path[0].Seconds != 0f || path[0].Position.sqrMagnitude > 0.000001f ||
                Mathf.Abs(path[0].Yaw) > 0.001f || path[path.Length - 1].Seconds < length - 0.001f)
                throw new InvalidOperationException($"{clip}：参考轨迹应从局部原点开始并覆盖整个片段。");
            for (int i = 1; i < path.Length; i++)
                if (!(path[i].Seconds > path[i - 1].Seconds))
                    throw new InvalidOperationException($"{clip}：参考轨迹时间必须严格递增。");
        }

'''+s[a:]
s=s.replace('            var clip = track.Clip;', '''            var clip = track.Clip;
            bool extractRoot = track.PathSource == GroundPathSource.RootMotion;
            animator.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            animator.applyRootMotion = extractRoot;''')
s=s.replace('                animation.SetSpeed(0d);','                animation.SetSpeed(extractRoot ? 1d : 0d);')
s=s.replace('                for (int i = 0; i <= intervals; i++)\n                {','''                track.RootPath = extractRoot ? new GroundPathKey[intervals + 1] : null;
                Vector3 origin = default;
                Quaternion basis = Quaternion.identity;
                float previousYaw = 0f;
                for (int i = 0; i <= intervals; i++)
                {''',1)
s=s.replace('                    animation.SetTime(phase * clip.length);\n                    graph.Evaluate(0f);','''                    if (!extractRoot || i == 0) animation.SetTime(phase * clip.length);
                    graph.Evaluate(extractRoot && i > 0 ? clip.length / intervals : 0f);
                    if (extractRoot)
                    {
                        if (i == 0)
                        {
                            origin = animator.transform.position;
                            basis = Quaternion.Inverse(animator.transform.rotation);
                        }
                        Vector3 local = basis * (animator.transform.position - origin);
                        Vector3 forward = basis * animator.transform.forward;
                        float yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                        previousYaw += Mathf.DeltaAngle(previousYaw, yaw);
                        track.RootPath[i] = new GroundPathKey { Seconds = phase * clip.length,
                            Position = new Vector2(local.x, local.z), Yaw = previousYaw };
                    }''')
s=s.replace('                float step = clip.length / intervals;', '''                if (extractRoot && Array.TrueForAll(track.RootPath, k => k.Position.sqrMagnitude < 0.000001f && Mathf.Abs(k.Yaw) < 0.1f))
                    throw new InvalidOperationException("根轨迹为空，请检查根运动导入或指定参考轨迹。");
                float step = clip.length / intervals;''')
s=s.replace('track.IsStop ? 0 : intervals - 1','track.IsLoop ? intervals - 1 : 0').replace('track.IsStop ? intervals : 1','track.IsLoop ? 1 : intervals').replace('track.IsStop && (i == 0','!track.IsLoop && (i == 0')
p.write_text(s)

