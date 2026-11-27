exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
new('Anim/CharacterPlaybackClock.cs','''using System;
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
        internal bool Valid => Duration > 0f;
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
            if (at >= FadeAt) return;
            FadeAt = Math.Max(Start, at);
            FadeSeconds = Mathf.Max(0f, seconds);
        }

        // 将本次实例的合法区间截断在最早生效的取消请求。
        internal void Cancel(double at) => StopAt = Math.Min(StopAt, Math.Max(Start, at));

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
''','CharacterActionPlayback')
new('Behavior/CharacterAttackPresentation.cs','''using System;

namespace ThreeC.CharacterBehavior
{
    // 将招式的已授权表现参数转换成一次播放及有生效时刻的后摇、退出命令。
    internal readonly struct CharacterAttackPresentation
    {
        internal readonly int ClipId;
        internal readonly float AnimationSeconds, Entry, Active, Recovery;
        internal bool Valid => ClipId != 0 && AnimationSeconds > 0f && !float.IsInfinity(AnimationSeconds);

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
''','CharacterAttackState')
use('CharacterLightAttackBehavior')
sub('        internal float Duration =>','        private int frozenClipId;\n        internal float Duration =>')
sub('            copy.Clip = null;', '            copy.frozenClipId = Presentation.ClipId;\n            copy.Clip = null;')
cut('        // 冻结独立的动画时长','    }\n\n    // 维护技能二','''        // 作者资源在主线程捕获身份；执行与预测副本只使用已冻结的整数。
        internal override CharacterAttackPresentation Presentation => new CharacterAttackPresentation(
            ReferenceEquals(Clip, null) ? frozenClipId : Clip ? Clip.GetInstanceID() : 0,
            AnimationDurationSeconds, EntryBlendSeconds, ActiveSeconds, RecoverySeconds);
''')
sub('internal override CharacterActionHandoff Timing { get; }','internal override CharacterAttackPresentation Presentation { get; }')
sub('Timing = settings.AttackTiming;','Presentation = settings.Presentation;');write()
use('CharacterAttackDefinition');sub('internal abstract CharacterActionHandoff AttackTiming','internal abstract CharacterAttackPresentation Presentation');sub('internal abstract CharacterActionHandoff Timing','internal abstract CharacterAttackPresentation Presentation');write()
use('CharacterAttackCombo');sub('private double startElapsed;','private double startedAt;');sub('startProgress, startElapsed, pending','startProgress, startedAt, pending');sub('startElapsed = next.Elapsed;', 'startedAt = context.SimulationTime;');sub('            startElapsed = current.Elapsed;\n','');sub('copy.startElapsed = startElapsed;', 'copy.startedAt = startedAt;');write()
use('CharacterAttackState');sub('internal CharacterActionHandoff Handoff { get; }\n        internal double StartElapsed { get; }','internal CharacterAttackPresentation Presentation { get; }\n        internal double StartedAt { get; }');sub('float start, double startElapsed','float start, double startedAt');sub('Handoff = execution.Timing;\n            StartElapsed = startElapsed;', 'Presentation = execution.Presentation;\n            StartedAt = startedAt;');write()
use('CharacterBehaviorObservation');sub('        public float DeltaTime { get; }','        public float DeltaTime { get; }\n        internal double Time { get; }');sub('float deltaTime, int simulationVersion)','float deltaTime, int simulationVersion, double? time = null)');sub('            DeltaTime = deltaTime;', '            DeltaTime = deltaTime;\n            Time = time ?? behavior.StepStartTime + deltaTime;');write()
use('CharacterBehaviorFrameResult');sub('        internal bool DurationCommitted', '        internal double StepStartTime => step.StartSeconds;\n        internal bool DurationCommitted',) if False else None
s=s.replace('        internal bool DurationCommitted =>', '        internal double StepStartTime => step.StartSeconds;\n        internal bool DurationCommitted =>',1);write()
use('CharacterBehaviorTimeline');sub('seconds, frame.Version);','seconds, frame.Version, Time);');sub('result, 0f, version);','result, 0f, version, Time);');write()
use('CharacterActionHandoff')
cut('    // 分开保存','    // 保存一次动作','''    // 统一向图提供素材位置与覆盖权重，旧式行为映射可继续提供自己的阶段采样。
    internal readonly struct CharacterActionTiming
    {
        public readonly float? StartClipPhase, WeightPhase, ClipPhase;
        public readonly CharacterActionHandoff Handoff;
        internal readonly CharacterPlaybackSample Sample;
        public readonly bool StartAtZero, Resumed;
        internal bool HasWeight => Sample.Valid || Handoff.Valid;
        internal bool Entered => Sample.Valid ? Sample.RemainingEntry <= 0f : Handoff.Valid && WeightPhase >= Handoff.FullFrom;
        internal float RemainingEntry => Sample.Valid ? Sample.RemainingEntry :
            Handoff.Valid ? Mathf.Max(0f, Handoff.FullFrom - WeightPhase.GetValueOrDefault()) * Handoff.Duration : 0f;

        // 普通行为映射继续选择随素材或由行为给出相位。
        public CharacterActionTiming(float? startClipPhase, float weightPhase, CharacterActionHandoff handoff,
            bool startAtZero, bool resumed, bool followClip = false)
        {
            this = default;
            StartClipPhase = startClipPhase;
            WeightPhase = weightPhase;
            Handoff = handoff;
            StartAtZero = startAtZero;
            Resumed = resumed;
            ClipPhase = followClip ? null : (float?)weightPhase;
        }

        // 独立计时的播放请求以句柄保存的真实游标作为图节点恢复点。
        internal CharacterActionTiming(CharacterPlaybackSample sample, CharacterClipProgress? cursor)
        {
            this = default;
            Sample = sample;
            ClipPhase = sample.Phase;
            StartClipPhase = cursor?.normalizedTime ?? 0f;
            Resumed = cursor.HasValue;
        }

        // 求当前权重，使用请求的独立过渡或旧映射的相位窗口。
        internal float WeightAt(float entry) => Sample.Valid ? Sample.Weight(entry, out _) : Handoff.WeightAt(WeightPhase.GetValueOrDefault(), entry);
    }

''')
sub('        public readonly float AnimationDuration;\n        public bool IsAttack => AnimationDuration > 0f;\n','');sub('            AnimationDuration = 0f;\n','')
cut('        // 冻结状态覆盖窗口','        // 按当前进度')
write()
use('CharacterActionPlayback')
sub('        internal readonly CharacterActionHandoff ExplicitAttackTiming;\n        internal CharacterClipProgress? ResumeCursor;', '''        internal CharacterPlaybackClock Clock;
        internal readonly int ClipId;
        internal CharacterClipProgress? Cursor { get; private set; }
        internal CharacterPlaybackEndSource StopSource { get; private set; }
        internal CharacterActionPlaybackStatus StopStatus { get; private set; }
        //''')
sub('CharacterPlaybackStartSource startSource, CharacterActionHandoff explicitAttackTiming = default)','CharacterPlaybackStartSource startSource, CharacterPlaybackClock clock = default, int clipId = 0)')
sub('            ExplicitAttackTiming = explicitAttackTiming;', '            Clock = clock;\n            ClipId = clipId;')
sub('        // 只在播放端实际下发','''        // 对这个逻辑实例安排权重淡出，调用不操作播放图。
        internal void FadeOut(double at, float seconds) => Clock.FadeOut(at, seconds);

        // 记录合法采样的截止与唯一终止原因；相同时刻的显式打断优先于自然窗口退出。
        internal void Cancel(double at, CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source)
        {
            if (at > Clock.StopAt || at == Clock.StopAt && StopSource != CharacterPlaybackEndSource.BehaviorWindowEnded) return;
            Clock.Cancel(at);
            StopStatus = status;
            StopSource = source;
        }

        // 图求值后只接收当前句柄与素材的真实游标，暂停和同素材重绑保留该值。
        internal void RecordCursor(in CharacterClipProgress sample)
        {
            if (sample.PlaybackId == PlaybackId && sample.ClipId == ClipId && !sample.loop) Cursor = sample;
        }

        // 只在播放端实际下发''')
write()

