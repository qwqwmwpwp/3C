using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 单次播放的纯值时钟，起播、淡出与撤销均使用同一角色时间域的绝对时间。
    internal struct CharacterPlaybackClock
    {
        internal readonly double Start;
        internal readonly float Duration, BlendIn;
        internal double FadeAt { get; private set; }
        internal float FadeSeconds { get; private set; }
        internal double StopAt { get; private set; }
        internal bool Valid => !double.IsNaN(Start) && !double.IsInfinity(Start) && Duration > 0f &&
            !float.IsInfinity(Duration) && BlendIn >= 0f && !float.IsInfinity(BlendIn) && !double.IsInfinity(NaturalEnd);
        internal double NaturalEnd => Start + Duration;
        internal double End => Math.Min(NaturalEnd, StopAt);

        // 冻结动画持续时间与入场过渡；未收到停止命令的实例播放到素材末尾。
        internal CharacterPlaybackClock(double start, float duration, float blendIn)
        {
            Start = start;
            Duration = duration;
            BlendIn = blendIn;
            FadeAt = StopAt = double.PositiveInfinity;
            FadeSeconds = 0f;
        }

        // 保存最早生效的淡出请求，重复交付不会重新开始过渡。
        internal void FadeOut(double at, float seconds)
        {
            if (!(at < FadeAt) || double.IsInfinity(at) || !(seconds >= 0f) || float.IsInfinity(seconds)) return;
            FadeAt = Math.Max(Start, at);
            FadeSeconds = Mathf.Max(0f, seconds);
        }

        // 将本次实例的合法区间截断在最早生效的取消请求。
        internal void Cancel(double at)
        {
            if (!double.IsNaN(at) && !double.IsInfinity(at)) StopAt = Math.Min(StopAt, Math.Max(Start, at));
        }

        // 纯查询指定绝对时刻的计划位置，可在重选或独立预测中重复调用。
        internal CharacterPlaybackSample Sample(double at) => new CharacterPlaybackSample(this, at);
    }

    // 冻结一次规划的素材位置和权重，不持有图节点、业务状态或真实句柄。
    internal readonly struct CharacterPlaybackSample
    {
        internal readonly CharacterPlaybackClock Clock;
        internal readonly double Time, Elapsed;
        internal bool Valid => Clock.Valid;
        internal bool Fading => Time >= Clock.FadeAt;
        internal float RemainingEntry => Mathf.Max(0f, Clock.BlendIn - (float)Elapsed);
        internal float Phase => !Valid ? 0f : Time >= Clock.NaturalEnd ? 1f :
            Mathf.Min(0.99999994f, (float)(Elapsed / Clock.Duration));

        // 素材时间以起播为零点，停止请求之后的查询停在最后合法时刻。
        internal CharacterPlaybackSample(CharacterPlaybackClock clock, double at)
        {
            Clock = clock;
            Time = Math.Max(clock.Start, Math.Min(at, clock.End));
            Elapsed = Time - clock.Start;
        }

        // 把合法播放秒数换算成双精度素材秒数，取消端点不经单精度相位往返。
        internal double MaterialSeconds(float clipLength) => !Valid ? 0d : Time >= Clock.NaturalEnd ? clipLength :
            Math.Min(clipLength, Elapsed / Clock.Duration * clipLength);

        // 从真实入场覆盖量过渡至满权重，淡出时从命令生效时的权重继续。
        internal float Weight(float entry, out float slope)
        {
            float entering = Clock.BlendIn > 0f ? Mathf.Clamp01((float)(Math.Min(Time, Clock.FadeAt) - Clock.Start) / Clock.BlendIn) : 1f;
            float weight = Mathf.Lerp(entry, 1f, Mathf.SmoothStep(0f, 1f, entering));
            if (!Fading)
            {
                slope = Clock.BlendIn > 0f ? (1f - entry) * CharacterBlendCurve.SmoothSlope(entering) / Clock.BlendIn : 0f;
                return weight;
            }
            float fade = Clock.FadeSeconds > 0f ? Mathf.Clamp01((float)(Time - Clock.FadeAt) / Clock.FadeSeconds) : 1f;
            slope = Clock.FadeSeconds > 0f ? -weight * CharacterBlendCurve.SmoothSlope(fade) / Clock.FadeSeconds : 0f;
            return weight * (1f - Mathf.SmoothStep(0f, 1f, fade));
        }
    }
}
