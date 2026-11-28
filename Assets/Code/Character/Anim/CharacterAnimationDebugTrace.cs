#if UNITY_EDITOR
using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("HumanoidAnimation.Editor")]
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterAnimationDebugStatus
    {
        WaitingForUpdate,
        Running,
        Inactive,
        Paused,
        AnimatorUnavailable,
        ResourceUnavailable
    }

    // 在声部入图时保留库、轨道及实际素材名称，资产重绑后仍可查看。
    internal readonly struct CharacterDebugClipSource
    {
        internal readonly string Library, Track, PrimaryName, SecondaryName;

        // 主线程根据实际声部绑定冻结两路素材的显示名称。
        internal CharacterDebugClipSource(string library, string track, AnimationClip primary, AnimationClip secondary)
        {
            Library = library;
            Track = track;
            PrimaryName = primary ? primary.name : null;
            SecondaryName = secondary ? secondary.name : null;
        }
    }

    // 描述一个片段源当前播放和淡出的素材、来源和权重。
    internal readonly struct CharacterDebugClipMix
    {
        public readonly string Target, Outgoing;
        public readonly AnimationClip PrimaryClip, SecondaryClip, OutgoingPrimaryClip, OutgoingSecondaryClip;
        public readonly float TargetWeight, OutgoingWeight, Phase;
        public readonly double Seconds;
        public readonly float Rate;
        public readonly int OtherCount;
        private readonly CharacterDebugClipSource source, outgoingSource;
        internal readonly Vector2 SourceWeights, OutgoingSourceWeights;
        internal string Library => source.Library;
        internal string Track => source.Track;
        internal string PrimaryName => source.PrimaryName;
        internal string SecondaryName => source.SecondaryName;
        internal string OutgoingLibrary => outgoingSource.Library;
        internal string OutgoingTrack => outgoingSource.Track;
        internal string OutgoingPrimaryName => outgoingSource.PrimaryName;
        internal string OutgoingSecondaryName => outgoingSource.SecondaryName;

        // 保存已接入播放图的素材、逻辑时钟和混合权重。
        public CharacterDebugClipMix(string target, float targetWeight, string outgoing, float outgoingWeight,
            int otherCount, in CharacterClipProgress progress, float rate, AnimationClip primaryClip,
            AnimationClip secondaryClip, AnimationClip outgoingPrimaryClip, AnimationClip outgoingSecondaryClip,
            in CharacterDebugClipSource source, in CharacterDebugClipSource outgoingSource,
            Vector2 sourceWeights, Vector2 outgoingSourceWeights)
        {
            Target = target;
            TargetWeight = targetWeight;
            Outgoing = outgoing;
            OutgoingWeight = outgoingWeight;
            OtherCount = otherCount;
            Phase = progress.normalizedTime;
            Seconds = progress.Seconds;
            Rate = rate;
            PrimaryClip = primaryClip;
            SecondaryClip = secondaryClip;
            OutgoingPrimaryClip = outgoingPrimaryClip;
            OutgoingSecondaryClip = outgoingSecondaryClip;
            this.source = source;
            this.outgoingSource = outgoingSource;
            SourceWeights = sourceWeights;
            OutgoingSourceWeights = outgoingSourceWeights;
        }
    }

    // 描述当前默认姿势、局部动作和共享动作的贡献。
    internal readonly struct CharacterDebugBodyMix
    {
        public readonly CharacterDebugClipMix Base, Action, Upper, Lower;
        public readonly float Coverage, UpperWeight, LowerWeight;

        // 读取双层混合与全身动作的覆盖量，供调试窗口显示接管进度。
        public CharacterDebugBodyMix(CharacterClipPlayback clips)
        {
            Base = clips.DebugMix(CharacterAnimationSource.Baseline);
            Action = clips.DebugMix(CharacterAnimationSource.FullBodyOverlay);
            Coverage = clips.LayerWeight(CharacterAnimationSource.FullBodyOverlay);
            Upper = clips.DebugMix(CharacterAnimationSource.UpperBody);
            Lower = clips.DebugMix(CharacterAnimationSource.LowerBody);
            UpperWeight = (1f - Coverage) * clips.LayerWeight(CharacterAnimationSource.UpperBody);
            LowerWeight = (1f - Coverage) * clips.LayerWeight(CharacterAnimationSource.LowerBody);
        }
    }

    /// <summary>保存一个部位当前获选的动作及本帧实际下发指令的起点。</summary>
    internal readonly struct CharacterAnimationDebugLayer
    {
        public readonly string Key;
        public readonly float? SentStart;

        // 只有实际下发播放指令时才记录起点。
        public CharacterAnimationDebugLayer(in CharacterAnimationLayerPlan plan, in CharacterAnimationPlaybackEmission emission)
        {
            Key = plan.Demand.Key;
            SentStart = plan.HasState && emission.For(plan.Demand.Source) ? plan.Start.Phase : (float?)null;
        }
    }

    /// <summary>保存当前动画运行状态和同一帧的两个部位播放结果。</summary>
    internal readonly struct CharacterAnimationDebugSnapshot
    {
        public readonly CharacterAnimationDebugStatus Status;
        public readonly string Error;
        public readonly bool SentBaselinePlayback;
        public readonly CharacterAnimationDebugLayer UpperBody, LowerBody;
        public readonly CharacterDebugBodyMix Mix;
        internal readonly VisualRootDebug Root;

        // 构造暂停、资源缺失或等待等播放状态。
        public CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus status, string error = null)
        {
            this = default;
            Status = status;
            Error = error;
        }

        // 从已提交的播放计划和实际混合构造当前播放状态。
        public CharacterAnimationDebugSnapshot(in CharacterAnimationPlaybackPlan plan,
            in CharacterAnimationPlaybackEmission emission, in CharacterDebugBodyMix mix, in VisualRootDebug root)
            : this(CharacterAnimationDebugStatus.Running)
        {
            UpperBody = new CharacterAnimationDebugLayer(plan.UpperOwner, emission);
            LowerBody = new CharacterAnimationDebugLayer(plan.LowerOwner, emission);
            SentBaselinePlayback = emission.FullBody;
            Mix = mix;
            Root = root;
        }
    }

    /// <summary>发布供编辑器读取的当前播放状态，并在读取时验证实例与模拟版本。</summary>
    internal sealed class CharacterAnimationDebugTrace
    {
        private CharacterAnimationDebugSnapshot current;
        private int sampledSimulationVersion;

        // 记录当前动画更新所采样的模拟版本。
        public void CaptureSimulation(int simulationVersion) => sampledSimulationVersion = simulationVersion;

        // 根据当前动画归属与模拟状态返回可用的播放状态。
        public CharacterAnimationDebugSnapshot Read(Animator animator, CharacterAnimationPlayer player, int stateVersion,
            int currentStateVersion, int simulationVersion, bool simulating)
        {
            if (animator == null || !animator.isActiveAndEnabled)
                return new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.AnimatorUnavailable);
            if (!simulating)
                return new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.Paused);
            if (stateVersion != currentStateVersion || player.NeedsRebind || sampledSimulationVersion != simulationVersion ||
                current.Status == CharacterAnimationDebugStatus.Paused ||
                current.Status == CharacterAnimationDebugStatus.AnimatorUnavailable)
                return default;
            return current;
        }

        // 保存提交后的实际片段混合，并将同一份快照交给匹配历史。
        public CharacterAnimationDebugSnapshot Publish(in CharacterAnimationPlaybackPlan plan,
            in CharacterAnimationPlaybackEmission emission, in CharacterDebugBodyMix mix, in VisualRootDebug root) =>
            current = new CharacterAnimationDebugSnapshot(plan, emission, mix, root);

        // 更新当前动画的暂停、停用或资源状态。
        public void SetStatus(CharacterAnimationDebugStatus status, string error = null)
        {
            current = new CharacterAnimationDebugSnapshot(status, error);
        }

        // 丢弃上一个播放绑定发布的状态。
        public void Clear() => current = default;
    }
}
#endif
