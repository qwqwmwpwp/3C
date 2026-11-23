from pathlib import Path
r=Path('Assets/CharacterBehavior/Runtime')
p=r/'CharacterMotionMatcher.cs';s=p.read_text()
s=s.replace('                var binding = new CharacterAnimationBinding(settings, track.IsLoop ? track.ReferenceSpeed : 0f,\n                    variant, brakingClipSeconds: track.BrakeSeconds);','''                resources.TryGetPhase(Animator.StringToHash(settings.StateName), out var phase);
                var binding = new CharacterAnimationBinding(settings, track.IsLoop ? track.ReferenceSpeed : 0f,
                    variant, phase, track.BrakeSeconds);''')
a=s.index('            bool returningToCycle = false;');b=s.index('            if (intent.Phase == CharacterLocomotionPhase.Stopping)',a)
s=s[:a]+'''            bool returningToCycle = false;
            if (!stopping && !turn && !reverse && intent.Phase != CharacterLocomotionPhase.Stopping &&
                KeepPlaying(owner, seen.Full, last.FullBody, out returningToCycle))
                return new CharacterMatchResult { Choice = CharacterMatchChoice.Continue };
            playing = null;
'''+s[b:]
s=s.replace('            GroundMotionUse use = stopping ? GroundMotionUse.Stop : turn ? GroundMotionUse.Turn :\n                reverse ? GroundMotionUse.Cycle : intent.Phase == CharacterLocomotionPhase.Starting && !returningToCycle ? GroundMotionUse.Start : GroundMotionUse.Cycle;', '''            var use = GroundMotionUse.Cycle;
            if (stopping) use = GroundMotionUse.Stop;
            else if (turn) use = GroundMotionUse.Turn;
            else if (!reverse && !returningToCycle && intent.Phase == CharacterLocomotionPhase.Starting) use = GroundMotionUse.Start;''')
a=s.index('            var best = new CharacterMatchResult');b=s.index('            bool canWait',a)
old=s[a:b];c=old.index('#if UNITY_EDITOR');end=old[old.index('            foreach (var entry'):]
s=s[:a]+'''            var best = Search(use, gait, entrySpeed, intent.DirectTurnAngle, feet.Pose, motion,
                out var winner, out var bestCost);
            GroundMotionCost keepCost = default;
            bool hasCurrent = states.TryGetValue(current.fullPathHash, out var source) &&
                current.fullPathHash == last.FullBody.StateHash;
            if (hasCurrent)
            {
                keepCost = new GroundMotionCost(source, source.Index(current.normalizedTime), feet.Pose,
                    motion.Trajectory.Match, motion.ActualSpeed);
                best.ContinueCost = keepCost.Total;
            }
'''+s[b:]
i=s.index('        private string Excluded')
s=s[:i]+'''        private bool KeepPlaying(CharacterAnimationDemand owner, in CharacterLayerObservation seen,
            in CharacterAnimationLayerPlan last, out bool returningToCycle)
        {
            returningToCycle = false;
            if (playing == null || last.StateHash != playingHash || !last.Demand.IsLocomotion) return false;
            var actual = seen.Destination;
            bool selected = actual.fullPathHash == playingHash;
            if (selected) observedPlaying = true;
            bool pendingEvaluation = !observedPlaying && last.Command != CharacterAnimationPlaybackCommand.Keep;
            if (playing.IsLoop) return (selected || pendingEvaluation) && baseInstance == owner.InstanceId && playing.Accepts(owner.Key);
            if (selected && actual.normalizedTime * playing.Seconds < playing.ExitTime || pendingEvaluation) return true;
            returningToCycle = true;
            return false;
        }

        private CharacterMatchResult Search(GroundMotionUse use, string gait, float entrySpeed, float angle,
            in CharacterFootPose feet, CharacterAnimationMotion motion, out CharacterMotionTrack winner, out GroundMotionCost bestCost)
        {
            var best = new CharacterMatchResult { Cost = float.PositiveInfinity, ContinueCost = float.PositiveInfinity };
            bestCost = default;
            winner = null;
#if UNITY_EDITOR
            exclusions.Clear();
#endif
'''+end.replace('intent.DirectTurnAngle, feet.Pose.Contact','angle, feet.Contact').replace('i, feet.Pose,','i, feet,')+'''            return best;
        }

'''+s[i:]
s=s.replace('            if (best.HasEntry && winner != null && !winner.IsStop && mode == CharacterMatchMode.Apply)','''            if (best.Choice == CharacterMatchChoice.Continue && hasCurrent && source.IsLoop && source.Accepts(owner.Key) &&
                !stopping && !reverse && mode == CharacterMatchMode.Apply)
            {
                playing = source;
                playingHash = current.fullPathHash;
                observedPlaying = true;
            }
            if (best.HasEntry && winner != null && !winner.IsStop && mode == CharacterMatchMode.Apply)''')
s=s.replace('if (!database || !animator || animator.avatar', 'if (!database || !animator || layer < 0 || layer >= animator.layerCount || animator.avatar')
p.write_text(s)
p=r/'CharacterMotionDatabase.cs';s=p.read_text().replace('        [SerializeField] internal Avatar Avatar;','        [SerializeField, Min(0), InspectorName("地面动作所在层")] internal int GroundLayer;\n        [SerializeField] internal Avatar Avatar;')
p.write_text(s)
p=Path('Assets/CharacterBehavior/Editor/CharacterMotionDatabaseEditor.cs');s=p.read_text()
s=s.replace('            foreach (var layer in controller.layers) Collect(layer.name, layer.stateMachine, paths);','''            if (db.GroundLayer < 0 || db.GroundLayer >= controller.layers.Length)
                throw new InvalidOperationException("地面动作所在层超出控制器层范围。");
            var layer = controller.layers[db.GroundLayer];
            Collect(layer.name, layer.stateMachine, paths);''')
s=s.replace('            if (track.Use == GroundMotionUse.Legacy ||', '''            if (!Finite(length) || !Finite(track.ReferenceSpeed) || !Finite(track.BrakeSeconds) ||
                !Finite(track.FirstEntrySeconds) || !Finite(track.LastEntrySeconds) || !Finite(track.ExitSeconds) ||
                !Finite(track.MinimumSpeed) || !Finite(track.MaximumSpeed) || !Finite(track.Blend))
                throw new InvalidOperationException($"{clip}：时间、速度和混合参数必须是有限数值。");
            if (track.Use == GroundMotionUse.Legacy ||''')
s=s.replace('            for (int i = 1; i < path.Length; i++)\n                if (!(path[i].Seconds > path[i - 1].Seconds))','''            for (int i = 0; i < path.Length; i++)
                if (!Finite(path[i].Seconds) || !Finite(path[i].Position.x) || !Finite(path[i].Position.y) ||
                    !Finite(path[i].Yaw) || i > 0 && !(path[i].Seconds > path[i - 1].Seconds))''').replace('        private static void Collect(','''        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static void Collect(''')
s=s.replace('            var leftContact = Contact(source, "IKLeftFoot");\n            var rightContact = Contact(source, "IKRightFoot");','''            var leftContact = Contact(source, clip, "IKLeftFoot");
            var rightContact = Contact(source, clip, "IKRightFoot");''').replace('Contact = new Vector2(leftContact.Evaluate(phase), rightContact.Evaluate(phase))','Contact = new Vector2(Mathf.Clamp01(leftContact(phase)), Mathf.Clamp01(rightContact(phase)))')
s=s.replace('        private static AnimationCurve Contact(ModelImporterClipAnimation source, string name)','        private static Func<float, float> Contact(ModelImporterClipAnimation source, AnimationClip clip, string name)').replace('if (entry.name == name) return entry.curve;\n            return null;', '''if (entry.name == name && entry.curve != null) return entry.curve.Evaluate;
            var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), name);
            var native = AnimationUtility.GetEditorCurve(clip, binding);
            if (native == null) return null;
            return phase => native.Evaluate(phase * clip.length);''')
p.write_text(s)
# Give cyclic fallback the original phase continuity and stop parameters even when a custom turn is absent.
p=r/'CharacterLocomotionPoseSelector.cs';s=p.read_text().replace('    /// <summary>规则选姿。只读本帧观测、候选归属和上一次已经提交的播放计划。</summary>', '    /// <summary>基于归属预览决定地面动作、消费收步并产生本帧最终播放计划。</summary>')
p.write_text(s)

