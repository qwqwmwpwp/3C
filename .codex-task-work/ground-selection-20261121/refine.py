from pathlib import Path
import re,uuid
r=Path('Assets/CharacterBehavior/Runtime')
p=r/'CharacterMotionDatabase.cs';s=p.read_text().replace('[InspectorName("应用匹配")] Apply','[InspectorName("应用匹配")] Apply,\n        [InspectorName("旧版跑停匹配（对照）")] LegacyBaseline')
s=s.replace('        public GroundMotionUse Use;', '        [Tooltip("同一用途的多个片段会一起比较入点。第一个绑定状态用于选播，其余状态共享观测数据。")]\n        public GroundMotionUse Use;')
p.write_text(s)
p=r/'CharacterLegacyMotionMatcher.cs';s=p.read_text().replace('if (Mode == CharacterMatchMode.Rules) return', 'if (Mode == CharacterMatchMode.Rules) return',1)
s=s.replace('animator.avatar == database.Avatar &&\n            animator.runtimeAnimatorController == database.Controller','animator.avatar == (database.Format == 0 ? database.Avatar : database.BakedAvatar) &&\n            animator.runtimeAnimatorController == (database.Format == 0 ? database.Controller : database.BakedController)')
s=s.replace('            if (!database || database.Tracks == null) return;\n            foreach (var track in database.Tracks)','''            var source = !database ? null : database.Format == 0 ? database.Tracks : database.BakedTracks;
            if (source == null) return;
            foreach (var track in source)''')
s=s.replace('                foreach (string state in track.States)','''                if (database.Format == 1 && !(track.Use == GroundMotionUse.Cycle && track.Gait != GroundTrackGait.Sprint ||
                    track.Use == GroundMotionUse.Stop && track.Gait == GroundTrackGait.Run)) continue;
                foreach (string state in track.States)''')
p.write_text(s)
p=r/'CharacterMotionMatcher.cs';s=p.read_text()
s=s.replace('        private float hold;', '''        private float hold;
        private long pendingTurn;
        private float turnWait;''')
s=s.replace('        public CharacterMatchMode Mode','''        private bool UsesLegacy => !database || database.Format == 0 || mode == CharacterMatchMode.LegacyBaseline;
        public CharacterMatchMode Mode''').replace('if (legacy != null) legacy.Mode = value;', 'legacy.Mode = value == CharacterMatchMode.LegacyBaseline ? CharacterMatchMode.Apply : value;')
s=s.replace('Describe() => legacy != null ?', 'Describe() => UsesLegacy ?')
a=s.index('            if (!database || database.Format == 0)');b=s.index('            if (database.Format',a)
s=s[:a]+'''            legacy = new CharacterLegacyMotionMatcher(animator, resources, config);
            if (!database) return;
'''+s[b:]
s=s.replace('            hold = 0f;\n            playing = null;', '            hold = turnWait = 0f;\n            pendingTurn = 0;\n            playing = null;',1)
s=s.replace('if (legacy != null) return legacy.Evaluate','if (UsesLegacy) return legacy.Evaluate')
s=s.replace('            bool idleCrossing = reverse &&', '''            if (pendingTurn != intent.DirectTurnStep)
            {
                pendingTurn = intent.DirectTurnStep;
                turnWait = 0f;
            }
            bool idleCrossing = reverse &&''')
s=s.replace('                playing = null;\n                return Rules("下身归属已改变");','''                bool interrupt = owners.LowerBody.IsValid || owner.Key != CharacterAnimationIntentConverter.IdleKey;
                playing = null;
                baseInstance = 0;
                return interrupt ? Rules("下身归属已改变") : default;''')
s=s.replace('            if (!reverse) hold = 0f;', '''            if (!reverse) hold = 0f;
            bool returningToCycle = false;''')
s=s.replace(': !observedPlaying;', ': !observedPlaying && last.FullBody.Command != CharacterAnimationPlaybackCommand.Keep;')
s=s.replace('                if (keep && intent.Phase', '''                returningToCycle = !keep && !playing.IsLoop;
                if (keep && intent.Phase''')
s=s.replace('intent.Phase == CharacterLocomotionPhase.Starting ? GroundMotionUse.Start', 'intent.Phase == CharacterLocomotionPhase.Starting && !returningToCycle ? GroundMotionUse.Start')
s=s.replace('!reverse && baseInstance == owner.InstanceId)', '!reverse && !returningToCycle && baseInstance == owner.InstanceId)')
s=s.replace('            if (turn) handledTurn = intent.DirectTurnStep;', '''            if (turn)
            {
                turnWait += dt;
                if (best.Choice != CharacterMatchChoice.Continue || turnWait >= maxHold)
                    handledTurn = intent.DirectTurnStep;
            }''')
# A cycle selected via the normal rule already handles phase mapping when switching nominal gait.
# Only use explicit entry if leaving a matched non-loop, restarting, or a competitor wins.
s=s.replace('            if (!stopping && !idleCrossing) baseInstance', '''            if (owner.Key == CharacterAnimationIntentConverter.IdleKey && best.HasEntry)
                best.Choice = CharacterMatchChoice.Rules;
            if (!stopping && !idleCrossing) baseInstance''')
s=s.replace('if (legacy != null)', 'if (legacy != null)')
p.write_text(s)
p=r/'CharacterAnimationLocomotion.cs';s=p.read_text().replace('        private Vector3 turnTarget;', '''        private Vector3 turnTarget;
        private float directWindow;''')
s=s.replace('            if (grounded && !motion.IsBlocked && !IsReleasing','            directWindow = Mathf.Max(0f, directWindow - stepSeconds);\n            if (grounded && !motion.IsBlocked && !IsReleasing').replace('                    turnTarget = expectedDirection;', '                    turnTarget = expectedDirection;\n                    directWindow = reverseWindowSeconds;').replace('            if (!grounded || !motion.HasAcceleration || Vector3.Dot(turnTarget','            if (directWindow <= 0f || !grounded || !motion.HasAcceleration || Vector3.Dot(turnTarget')
p.write_text(s)
p=r/'CharacterAnimationIk.cs';s=p.read_text().replace('        public CharacterFootObservation ReadFeet', '''        internal bool HasTeleported => hasPosition && teleportDistance > 0f &&
            (owner.position - lastPosition).sqrMagnitude > teleportDistance * teleportDistance;

        public CharacterFootObservation ReadFeet''').replace('hasPosition && teleportDistance > 0f && (owner.position - lastPosition).sqrMagnitude > teleportDistance * teleportDistance)','HasTeleported)').replace('            bool teleported = hasPosition && teleportDistance > 0f &&\n                (position - lastPosition).sqrMagnitude > teleportDistance * teleportDistance;\n            if (teleported ||','            if (HasTeleported ||')
p.write_text(s)
p=r/'CharacterAnimationPipeline.cs';s=p.read_text().replace('            if (motion.IsValid)\n                intent.AdvancePresentation','            if (Ik.HasTeleported) ResetPoseHistory();\n            if (motion.IsValid)\n                intent.AdvancePresentation')
p.write_text(s)
p=r/'CharacterMatchReplay.cs';s=p.read_text().replace('        private string entry, query;','        private string entry, query;\n        private int emitted;')
s=s.replace('            if (phase == 3 && acceptedReverse && !observedRestart && layer.Demand.IsLocomotion && !layer.Binding.IsStop &&\n                layer.Command != CharacterAnimationPlaybackCommand.Keep && (!Direct || isTurn))','''            bool relevant = phase == 3 && acceptedReverse && layer.Demand.IsLocomotion && !layer.Binding.IsStop &&
                layer.Command != CharacterAnimationPlaybackCommand.Keep && (!Direct || isTurn);
            if (relevant) emitted++;
            if (relevant && !observedRestart)''')
s=s.replace('mode == CharacterMatchMode.Observe ? "旁路观察" : "原规则";','mode == CharacterMatchMode.Observe ? "旁路观察" : mode == CharacterMatchMode.LegacyBaseline ? "旧版跑停匹配" : "原规则";')
s=s.replace('ms · {entry}', 'ms · {entry} · {emitted} 次指令')
p.write_text(s)
p=Path('Assets/player/player 1.prefab');b=p.read_bytes()
sp=b'\r\n' if b'\r\n' in b else b'\n'
p.write_bytes(b.replace(b'  matchReverseHold: 0.08'+sp,b'  matchReverseHold: 0.08'+sp+b'  matchTurnAngle: 55'+sp))
for f in ['CharacterLegacyMotionMatcher','GroundMotionPath']:
    dest=r/(f+'.cs.meta')
    assert not dest.exists()
    dest.write_text(re.sub(r'guid: [a-f0-9]+','guid: '+uuid.uuid4().hex,(r/'CharacterMatchTrajectory.cs.meta').read_text()))

