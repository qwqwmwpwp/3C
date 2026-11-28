#if UNITY_EDITOR
namespace ThreeC.CharacterBehavior
{
    // 保存一次已提交的地面查询及同次播放图求值结果，供环形槽位和固定查看共用。
    internal sealed class CharacterMatchHistoryEntry
    {
        internal readonly CharacterMatchDebugFrame Frame = new CharacterMatchDebugFrame();
        internal CharacterAnimationDebugSnapshot Playback { get; private set; }
        internal long Sequence { get; private set; }
        internal long MotionStep { get; private set; }
        internal double Time { get; private set; }
        internal string Boundary { get; private set; }
        internal string Library => Playback.Mix.Base.Library;
        internal string Track => Playback.Mix.Base.Track;

        // 复制可变的查询缓冲并保留与其共同提交的播放快照。
        internal void Capture(long sequence, double time, long motionStep, string boundary,
            CharacterMatchDebugFrame query, in CharacterAnimationDebugSnapshot playback)
        {
            Sequence = sequence;
            Time = time;
            MotionStep = motionStep;
            Boundary = boundary;
            Frame.CopyFrom(query);
            Playback = playback;
        }

        // 固定查看时脱离原环形槽，迟到的提交不能改写已经选中的一行。
        internal void CopyFrom(CharacterMatchHistoryEntry source) =>
            Capture(source.Sequence, source.Time, source.MotionStep, source.Boundary, source.Frame, source.Playback);
    }
}
#endif
