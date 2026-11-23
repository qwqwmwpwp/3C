from pathlib import Path
p=Path('Assets/CharacterBehavior/Runtime/CharacterMotionDatabase.cs')
s=p.read_text(encoding='utf-8-sig')
i=s.index('    [Serializable]\n    internal sealed class CharacterMotionTrack')
s=s[:i]+'''    internal enum GroundMotionUse { Legacy, Cycle, Start, Turn, Stop }
    internal enum GroundTrackGait { Any, Walk, Run, Sprint }
    internal enum GroundTrackSide { Either, Left, Right }
    internal enum GroundPathSource { Straight, Authored, RootMotion }

    [Serializable]
    internal struct GroundPathKey
    {
        public float Seconds;
        public Vector2 Position; // 以片段起点为原点，右、前（米）。
        public float Yaw; // 连续展开的偏航角（度）。
    }

'''+s[i:]
s=s.replace('        [Min(0f)] public float ReferenceSpeed;', '''        public GroundMotionUse Use;
        public GroundTrackGait Gait;
        public GroundTrackSide Side; // 转向侧或收步承重脚。
        public bool Loop;
        [Min(0f)] public float MinimumSpeed, MaximumSpeed; // 上限 0 表示不限。
        [Min(0f)] public float FirstEntrySeconds, ExitSeconds;
        public GroundPathSource PathSource;
        public GroundPathKey[] ReferencePath;
        [HideInInspector] public GroundPathKey[] RootPath;
        [Range(0f, 1f)] public float Blend = 0.08f;
        [Min(0f)] public float ReferenceSpeed;''')
s=s.replace('public bool IsStop => BrakeSeconds > 0f;', '''public bool IsStop => Use == GroundMotionUse.Stop || Use == GroundMotionUse.Legacy && BrakeSeconds > 0f;
        public bool IsLoop => Use == GroundMotionUse.Legacy ? !IsStop : Loop;
        public float ExitTime => ExitSeconds > 0f ? ExitSeconds : Seconds;
        public bool Accepts(string gait) => Gait == GroundTrackGait.Any ||
            Gait == GroundTrackGait.Walk && gait == CharacterAnimationIntentConverter.WalkKey ||
            Gait == GroundTrackGait.Run && gait == CharacterAnimationIntentConverter.MoveKey ||
            Gait == GroundTrackGait.Sprint && gait == CharacterAnimationIntentConverter.SprintKey;''')
s=s.replace('IsStop ? Mathf.Clamp01(phase) : Mathf.Repeat(phase, 1f)', 'IsLoop ? Mathf.Repeat(phase, 1f) : Mathf.Clamp01(phase)')
s=s.replace('        [SerializeField] internal Avatar Avatar;', '        [SerializeField, HideInInspector] internal int Format; // 0 沿用旧库，显式烘焙升级。\\n        [SerializeField] internal Avatar Avatar;'.replace('\\n','\n'))
p.write_text(s,encoding='utf8')
# Preserve the legacy search and its scoring for existing V0 assets.
p=p.with_name('CharacterMotionMatcher.cs')
s=p.read_text(encoding='utf-8-sig')
a=s.index('    internal enum'); b=s.index('    internal sealed class')
p.with_name('CharacterLegacyMotionMatcher.cs').write_text((s[:a]+s[b:]).replace('CharacterMotionMatcher','CharacterLegacyMotionMatcher'),encoding='utf8')

