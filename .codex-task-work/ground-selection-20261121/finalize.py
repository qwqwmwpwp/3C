from pathlib import Path
r=Path('Assets/CharacterBehavior/Runtime')
p=r/'CharacterMotionMatcher.cs';s=p.read_text().replace('bool sameCycle = hasCurrent && use == GroundMotionUse.Cycle && winner == source && !reverse;', '''bool sameCycle = hasCurrent && use == GroundMotionUse.Cycle && winner == source && !reverse &&
                current.fullPathHash == best.Binding.StateHash;''')
s=s.replace('variant, phase, track.BrakeSeconds);','''variant, phase, track.BrakeSeconds, track.IsStop && track.ExitSeconds > 0f && track.Seconds > 0f
                        ? track.ExitSeconds / track.Seconds : 0.98f);''')
p.write_text(s)
p=r/'CharacterAnimationResourceCatalog.cs';s=p.read_text().replace('        public readonly float BrakingClipSeconds;', '        public readonly float BrakingClipSeconds;\n        public readonly float StopExitPhase;').replace('CharacterLocomotionGaitPhase gait = default, float brakingClipSeconds = 0f)\n        {','CharacterLocomotionGaitPhase gait = default, float brakingClipSeconds = 0f, float stopExitPhase = 0.98f)\n        {',1).replace('            BrakingClipSeconds = brakingClipSeconds;', '            BrakingClipSeconds = brakingClipSeconds;\n            StopExitPhase = Mathf.Clamp01(stopExitPhase);')
p.write_text(s)
p=r/'CharacterLocomotionPoseSelector.cs';s=p.read_text().replace('if (actual.normalizedTime >= 0.98f)', 'if (actual.normalizedTime >= last.FullBody.Binding.StopExitPhase)')
p.write_text(s)
p=Path('Assets/CharacterBehavior/Editor/CharacterMotionDatabaseEditor.cs');s=p.read_text().replace('track.BrakeSeconds > length))','track.BrakeSeconds > length || track.ExitSeconds > 0f && track.ExitSeconds < track.BrakeSeconds))')
p.write_text(s)
p=r/'CharacterMotionDatabase.cs';s=p.read_text().replace('        public string[] States;','        [Tooltip("第一个为候选播放状态，其余为共享观测的同片段别名。填写包含层名的完整路径。")]\n        public string[] States;').replace('同一用途的多个片段会一起比较入点。第一个绑定状态用于选播，其余状态共享观测数据。','同一用途的多个片段会一起比较入点。').replace('        public GroundPathKey[] ReferencePath;', '        [Tooltip("以片段起点为原点，时间为秒、水平位置为米、连续偏航为度。")]\n        public GroundPathKey[] ReferencePath;')
p.write_text(s)
p=Path('Assets/player/player.controller')
s=p.read_text();i=s.index('--- !u!1102 &881100000000000030');p.write_text(s[:i]+'\n'.join(line.rstrip() for line in s[i:].splitlines())+'\n')
for p in [Path('Assets/player/LocomotionTurns.fbx.meta'),r/'GroundMotionPath.cs.meta',r/'CharacterLegacyMotionMatcher.cs.meta']:
    p.write_text('\n'.join(line.rstrip() for line in p.read_text().splitlines())+'\n')

