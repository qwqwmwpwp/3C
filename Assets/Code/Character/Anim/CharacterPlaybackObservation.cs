using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 标识实际求值的片段、句柄与精确素材秒数，归一化相位供选姿和结束判定。
    public readonly struct CharacterClipProgress
    {
        internal readonly int fullPathHash;
        internal readonly float normalizedTime;
        internal readonly bool loop;
        public float NormalizedTime => normalizedTime;
        public bool Looping => loop;
        public readonly int ClipId;
        public readonly long PlaybackId;
        public readonly double Seconds;
        public readonly bool AtNaturalEnd;

        // 比对策略装配时取得的状态和此声部当前播放的状态。
        public bool Plays(CharacterPoseHandle state) => state.IsValid && fullPathHash == state.Hash;

        // 捕获素材声部的实际时钟，不从行为目标或上一帧计划反推。
        internal CharacterClipProgress(int hash, float progress, bool looping, int clipId, long playbackId, double seconds, bool atNaturalEnd = false)
        {
            fullPathHash = hash;
            normalizedTime = progress;
            loop = looping;
            ClipId = clipId;
            PlaybackId = playbackId;
            Seconds = seconds;
            AtNaturalEnd = atNaturalEnd;
        }

    }

    // 保存一个片段混合源的当前姿态、淡入目标和对应长度。
    public readonly struct CharacterLayerObservation
    {
        public readonly CharacterClipProgress Current, Next;
        public readonly float SourceLength, DestinationLength;
        public readonly bool InTransition;
        public readonly float BlendProgress;
        public CharacterClipProgress Destination => InTransition ? Next : Current;
        public bool Ready => Current.fullPathHash != 0;

        // 记录混合器当前播放和正在进入的片段。
        public CharacterLayerObservation(CharacterClipProgress current, CharacterClipProgress next, bool fading, float blend,
            float sourceLength, float destinationLength)
        {
            Current = current;
            Next = next;
            InTransition = fading;
            BlendProgress = blend;
            SourceLength = sourceLength;
            DestinationLength = destinationLength;
        }
    }

    /// <summary>同一渲染帧、下发任何播放指令前取得的播放观测。</summary>
    public readonly struct CharacterPlaybackObservation
    {
        internal readonly CharacterLayerObservation Full, Lower, Upper, Overlay;
        public CharacterLayerObservation Baseline => Full;
        public CharacterLayerObservation LowerBody => Lower;
        public CharacterLayerObservation UpperBody => Upper;
        public CharacterLayerObservation FullBodyOverlay => Overlay;
        public readonly bool? LeftSupport;
        public readonly bool EarlyStance;

        // 按策略需要保留或隐藏承重脚观测，其他播放游标来自同一帧。
        internal CharacterPlaybackObservation WithFootObservation(bool include)
        {
            return new CharacterPlaybackObservation(Full, Lower, Upper, Overlay, include ? LeftSupport : null, include && EarlyStance);
        }

        // 固定本次采集的基础层、三个动作层和承重脚观测。
        public CharacterPlaybackObservation(CharacterLayerObservation full, CharacterLayerObservation lower,
            CharacterLayerObservation upper, CharacterLayerObservation overlay, bool? leftSupport, bool earlyStance)
        {
            Full = full;
            Lower = lower;
            Upper = upper;
            Overlay = overlay;
            LeftSupport = leftSupport;
            EarlyStance = earlyStance;
        }
    }

    // 从已提交的片段和脚姿计算基础步态的承重脚及各覆盖源的播放进度。
    internal sealed class CharacterPlaybackObserver
    {
        private readonly Animator animator;
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly CharacterClipPlayback clips;

        // 绑定用于采样分层状态和脚姿的资源及片段图。
        public CharacterPlaybackObserver(Animator animator, CharacterAnimationResourceCatalog resources, CharacterClipPlayback clips)
        {
            this.animator = animator;
            this.resources = resources;
            this.clips = clips;
        }

        // 在新计划下发之前采样所有播放源和可用的脚姿。
        public CharacterPlaybackObservation Capture(in CharacterAnimationLayerPlan previousBase,
            in CharacterFootObservation feet)
        {
            var full = clips.Observe(CharacterAnimationSource.Baseline);
            var lower = clips.Observe(CharacterAnimationSource.LowerBody);
            var upper = clips.Observe(CharacterAnimationSource.UpperBody);
            bool? left = null;
            bool early = false;
            if (full.Ready)
            {
                var state = full.Current;
                bool mapped = resources.TryGetPhase(state.fullPathHash, out var gait);
                if (full.InTransition && (!mapped || full.BlendProgress >= 0.5f) &&
                    resources.TryGetPhase(full.Next.fullPathHash, out var nextGait))
                {
                    gait = nextGait;
                    state = full.Next;
                    mapped = true;
                }
                bool ownsFeet = clips.LayerWeight(CharacterAnimationSource.LowerBody) > 0f ||
                    clips.LayerWeight(CharacterAnimationSource.FullBodyOverlay) > 0f;
                if (mapped && !ownsFeet)
                {
                    left = gait.IsLeftSupport(state.normalizedTime);
                    early = gait.IsEarlyStance(state.normalizedTime);
                }
                else if (previousBase.Demand.IsLocomotion && feet.Count > 0)
                {
                    var support = feet.Support;
                    left = support == GroundTrackSide.Either ? feet.Pose.Left.y <= feet.Pose.Right.y : support == GroundTrackSide.Left;
                }
                else if (previousBase.Demand.IsLocomotion && animator.isHuman)
                {
                    var a = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                    var b = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                    if (a && b) left = Vector3.Dot(a.position - b.position, animator.transform.up) <= 0f;
                }
            }
            var overlay = clips.Observe(CharacterAnimationSource.FullBodyOverlay);
            return new CharacterPlaybackObservation(full: full, lower: lower, upper: upper, overlay: overlay,
                leftSupport: left, earlyStance: early);
        }
    }
}
