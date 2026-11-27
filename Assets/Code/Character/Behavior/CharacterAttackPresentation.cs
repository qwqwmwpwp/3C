using System;

namespace ThreeC.CharacterBehavior
{
    // 将招式的已授权表现参数转换成一次播放及有生效时刻的后摇、退出命令。
    internal readonly struct CharacterAttackPresentation
    {
        internal readonly int ClipId;
        internal readonly float AnimationSeconds, Entry, Active, Recovery;
        internal bool Valid => ClipId != 0 && AnimationSeconds > 0f && !float.IsInfinity(AnimationSeconds) &&
            Entry >= 0f && Active > 0f && Recovery >= 0f && !float.IsInfinity(Entry + Active + Recovery);

        // 在受理招式时冻结素材身份与上层安排的三个行为阶段。
        internal CharacterAttackPresentation(int clipId, float duration, float entry, float active, float recovery)
        {
            ClipId = clipId;
            AnimationSeconds = duration;
            Entry = entry;
            Active = active;
            Recovery = recovery;
        }

        // 进入状态只请求动画自身的播放时长和缓入时间。
        internal CharacterPlaybackClock Play(double start) => Valid ? new CharacterPlaybackClock(start, AnimationSeconds, Entry) : default;

        // 标记后摇与自然退出的绝对角色时刻，与行为的浮点边界一致。
        internal double RecoveryAt(double start) => start + (Entry + Active);
        // 计算这个固定三段招式的自然退出时刻。
        internal double ExitAt(double start) => start + (Entry + Active + Recovery);

        // 为按键独立播放与纯数据预测安排同一组已知边界，不依赖真实句柄或图。
        internal CharacterPlaybackClock Schedule(double start)
        {
            var clock = Play(start);
            clock.FadeOut(RecoveryAt(start), Recovery);
            clock.Cancel(ExitAt(start));
            return clock;
        }
    }
}
