using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    /// <summary>一层的所有在播片段。打断淡入时从已经显示的混合权重开始下一次过渡。</summary>
    internal sealed class CharacterClipMixer
    {
        // 保存单一逻辑片段的时钟、贡献权重和动作归属。
        private sealed class Voice
        {
            public CharacterClipState State;
            public CharacterMotionPlayable Motion;
            public Playable Clip => Motion.Root;
            public string ActionKey;
            public double StepStart, StepEnd; // 本次求值实际覆盖的素材逻辑区间，不含切换跳转。
            public bool Locomotion;
            public long InstanceId, PlaybackId;
            public int Hash, ClipId, Port;
            public Vector3 InertiaSuppression;
            public float StartWeight, StartWeightSpeed, Weight, WeightSpeed, LogicalRate;
            public bool Attack;
            // 单次素材只有在图时钟实际走到末端时才报告完成，避免浮点转换提前结算末尾事件。
            public CharacterClipProgress Progress
            {
                get
                {
                    double seconds = Clip.GetTime();
                    double phase = Motion.Length > 0f ? seconds / Motion.Length : 0d;
                    float observed = Attack && phase < 1d ? Mathf.Min(0.99999994f, (float)phase) : (float)phase;
                    return new CharacterClipProgress(Hash, observed, Motion.Loop, ClipId, PlaybackId, seconds);
                }
            }
        }

        private readonly PlayableGraph graph;
        private static readonly ProfilerMarker CreateNode = new ProfilerMarker("3C.Animation.CreateNode");
        internal static readonly ProfilerMarker DestroyNode = new ProfilerMarker("3C.Animation.DestroyNode");
        private readonly List<Voice> voices = new List<Voice>();
        public readonly AnimationMixerPlayable Mixer;
        private float fadeTime, elapsed, carrySeconds;
        private float attackEntryCoverage;
        private bool smoothFade;
        private Voice target;
        internal CharacterTransitionProfile EntryProfile, ExitProfile;
        public bool Empty => target == null;

        public CharacterClipMixer(PlayableGraph graph)
        {
            this.graph = graph;
            using (CreateNode.Auto()) Mixer = AnimationMixerPlayable.Create(graph, 0);
        }

        // 切换目标片段并根据上身、全身与接管时钟选择淡入曲线。
        public void Enter(CharacterClipState state, AnimationClip asset, CharacterClipCurves curves, CharacterClipCurves secondary, int hash,
            float start, float duration, bool resumeLoop, bool resetEvents, in CharacterAnimationDemand demand)
        {
            fadeTime = target != null ? Mathf.Max(0f, duration) : 0f;
            bool attack = demand.HasHandoff && demand.Timing.Handoff.IsAttack;
            attackEntryCoverage = demand.EntryCoverage;
            if (attack && fadeTime > 0f)
                fadeTime = demand.Timing.Handoff.Duration * demand.Timing.Handoff.FullFrom;
            elapsed = 0f;
            carrySeconds = fadeTime / 3f;
            smoothFade = attack || demand.Source == CharacterAnimationSource.UpperBody ||
                demand.Source == CharacterAnimationSource.FullBodyOverlay;
            Voice returning = null;
            for (int i = voices.Count - 1; i >= 0; i--)
            {
                var voice = voices[i];
                if (voice.Weight <= 0f && voice.WeightSpeed <= 0f)
                {
                    Remove(i);
                    continue;
                }
                voice.StartWeight = voice.Weight;
                voice.StartWeightSpeed = voice.WeightSpeed;
                smoothFade |= voice.WeightSpeed != 0f;
                carrySeconds = CharacterBlendCurve.LimitCarry(carrySeconds, voice.Weight, voice.WeightSpeed);
                if (resumeLoop && voice.Hash == hash && voice.Motion.Loop &&
                    (returning == null || voice.Weight > returning.Weight)) returning = voice;
            }
            if (fadeTime <= 0f)
                for (int i = voices.Count - 1; i >= 0; i--)
                    if (voices[i] != returning) Remove(i);
            if (returning != null)
            {
                returning.ActionKey = demand.Key;
                returning.Locomotion = demand.IsLocomotion;
                returning.InstanceId = demand.InstanceId;
                returning.PlaybackId = demand.Playback?.PlaybackId ?? 0;
                target = returning; // 保留源片段的真实时间，反向淡回当前步态。
                return;
            }
            int port = 0;
            while (port < Mixer.GetInputCount() && Mixer.GetInput(port).IsValid()) port++;
            if (port == Mixer.GetInputCount()) Mixer.SetInputCount(port + 1);
            CharacterMotionPlayable motion;
            using (CreateNode.Auto()) motion = new CharacterMotionPlayable(graph, asset, state.Blend, curves, secondary);
            int clipId = asset.GetInstanceID();
            double startSeconds = start;
            if (resetEvents && demand.Playback?.ResumeCursor is CharacterClipProgress cursor)
                startSeconds = cursor.ClipId == clipId ? cursor.Seconds : (double)motion.Length * demand.Timing.ClipPhase.GetValueOrDefault();
            motion.Seek(startSeconds);
            if (resetEvents) motion.Seek(startSeconds); // 同步上次求值时间，跳过恢复点之前的片段事件（Timeline 1184106）。
            graph.Connect(motion.Root, 0, Mixer, port);
            target = new Voice { ActionKey = demand.Key, Locomotion = demand.IsLocomotion, InstanceId = demand.InstanceId, State = state, Motion = motion, Hash = hash, Port = port,
                Attack = attack, ClipId = clipId, PlaybackId = demand.Playback?.PlaybackId ?? 0,
                Weight = fadeTime > 0f ? 0f : 1f };
            voices.Add(target);
            Mixer.SetInputWeight(port, target.Weight);
        }

#if UNITY_EDITOR
        internal CharacterDebugClipMix ReadDebugMix()
        {
            if (target == null) return default;
            Voice outgoing = null;
            foreach (var voice in voices)
                if (voice != target && (outgoing == null || voice.Weight > outgoing.Weight)) outgoing = voice;
            return new CharacterDebugClipMix(target.State.Path, target.Weight, outgoing?.State.Path,
                outgoing?.Weight ?? 0f, Mathf.Max(0, voices.Count - 2), target.Progress.normalizedTime);
        }
#endif

        private void Remove(int index)
        {
            var voice = voices[index];
            graph.Disconnect(Mixer, voice.Port);
            using (DestroyNode.Auto()) graph.DestroySubgraph(voice.Clip);
            voices.RemoveAt(index);
        }

        public void Release(bool keepMixer)
        {
            for (int i = voices.Count - 1; i >= 0; i--) Remove(i);
            target = null;
            elapsed = fadeTime = 0f;
            if (keepMixer) Mixer.SetInputCount(0);
            else
            {
                using (DestroyNode.Auto()) graph.DestroyPlayable(Mixer);
            }
        }

        public void CompleteTransition() => elapsed = Mathf.Max(elapsed, fadeTime);

        public void SetInertiaSuppression(Vector3 suppression)
        {
            if (target != null) target.InertiaSuppression = suppression;
        }

        public Vector3 InertiaSuppression()
        {
            Vector3 sum = Vector3.zero;
            foreach (var voice in voices) sum += voice.Weight * voice.InertiaSuppression;
            return sum;
        }

        // 攻击素材跟随独立片段相位，进入权重跟随状态秒数；已中断的旧攻击只贡献最后姿势。
        public void Advance(float dt, CharacterClipPlayback playback, in CharacterAnimationPlaybackPlan frame, in CharacterActionTiming timing)
        {
            bool attackBlend = timing.Handoff.IsAttack;
            elapsed = attackBlend ? (float)timing.AttackElapsed : elapsed + dt;
            var blend = CharacterBlendCurve.Sample(elapsed, fadeTime, smoothFade, carrySeconds);
            if (attackBlend) blend = blend.WithinLayer(attackEntryCoverage);
            for (int i = voices.Count - 1; i >= 0; i--)
            {
                var voice = voices[i];
                if (voice != target && blend.Complete)
                {
                    Remove(i);
                    continue;
                }
                voice.Weight = blend.Apply(voice.StartWeight, voice.StartWeightSpeed, voice == target ? 1f : 0f,
                    out voice.WeightSpeed);
                Mixer.SetInputWeight(voice.Port, voice.Weight);
                double rate = voice.Attack ? 0d : playback.Pace(voice.Hash).Rate(frame.StrideSpeed, frame.StartSpeed, frame.StopRate, out _);
                if (voice == target && timing.ClipPhase.HasValue && dt > 0f)
                {
                    double seconds = (double)timing.ClipPhase.Value * voice.Motion.Length;
                    rate = System.Math.Max(0d, seconds - voice.Clip.GetTime()) / dt;
                }
                voice.LogicalRate = (float)rate;
                voice.StepStart = voice.Clip.GetTime();
                voice.Motion.Advance(rate, dt);
                voice.StepEnd = voice.Motion.EvaluationTime(dt);
            }
        }

        // 依据当前求值相位及各循环声部贡献，合成近窗素材路径方向。
        internal Vector2 CycleDirection(Dictionary<int, CharacterMotionTrack> tracks, float dt, float nearSeconds, out float coverage)
        {
            Vector2 direction = default;
            coverage = 0f;
            foreach (var voice in voices)
            {
                if (!voice.Locomotion || voice.Weight <= 0f || !tracks.TryGetValue(voice.Hash, out var track) ||
                    track.Use != GroundMotionUse.Cycle || !track.IsLoop) continue;
                float time = Mathf.Repeat((float)voice.Motion.EvaluationTime(dt), track.Seconds);
                var path = GroundMotionPath.Relative(track, time, nearSeconds * voice.LogicalRate);
                if (path.Position.sqrMagnitude <= 0.000001f) continue;
                direction += path.Position.normalized * voice.Weight;
                coverage += voice.Weight;
            }
            return direction;
        }

        // 将实际淡入时长与进入策略合成为该部位的切换强度和限制。
        internal TransitionPart EntryImpulse(AvatarMaskBodyPart part) => target == null || EntryProfile == null ? default :
            EntryProfile.Part(part, EntryProfile.Strength(fadeTime));

        // 合成声部各自实际走过的局部位移与转角，权重使用本次动画求值的真实贡献。
        internal GroundPathKey ReferenceStep(Dictionary<int, CharacterMotionTrack> tracks, out float coverage, out GroundMotionUse use)
        {
            var step = default(GroundPathKey);
            use = default;
            float largest = 0f;
            coverage = 0f;
            foreach (var voice in voices)
            {
                if (!voice.Locomotion || voice.Weight <= 0f || !tracks.TryGetValue(voice.Hash, out var track)) continue;
                float start = track.IsLoop ? Mathf.Repeat((float)(voice.StepStart % track.Seconds), track.Seconds) :
                    Mathf.Clamp((float)voice.StepStart, 0f, track.Seconds);
                float advance = (float)(voice.StepEnd - voice.StepStart);
                if (!track.IsLoop) advance = Mathf.Min(advance, track.Seconds - start);
                if (advance <= 0f) continue;
                if (voice.Weight > largest) { largest = voice.Weight; use = track.Use; }
                var delta = GroundMotionPath.Relative(track, start, advance);
                step.Position += delta.Position * voice.Weight;
                step.Yaw += delta.Yaw * voice.Weight;
                coverage += voice.Weight;
            }
            return step;
        }

        public float BlendedLength()
        {
            float length = 0f;
            foreach (var voice in voices) length += voice.Weight * voice.Motion.Length;
            return length;
        }

        public CharacterLayerObservation Observe()
        {
            if (target == null) return default;
            Voice primary = null;
            float length = 0f, weight = 0f;
            foreach (var voice in voices)
            {
                if (voice == target) continue;
                if (primary == null || voice.Weight > primary.Weight) primary = voice;
                length += voice.Motion.Length * voice.Weight;
                weight += voice.Weight;
            }
            return new CharacterLayerObservation((primary ?? target).Progress, target.Progress, primary != null,
                target.Weight, weight > 0f ? length / weight : target.Motion.Length, target.Motion.Length);
        }

        internal CharacterFootPlantFrame ReadFootPlant(in CharacterAnimationLayerPlan layer, float evaluationDelta)
        {
            if (target == null || target.Hash != layer.StateHash || target.Motion.Length <= 0f)
                return new CharacterFootPlantFrame(layer, layer.Start.Phase, true);
            double time = target.Motion.EvaluationTime(evaluationDelta);
            return new CharacterFootPlantFrame(layer, (float)(time / target.Motion.Length), voices.Count > 1, target.Motion, time);
        }

        public CharacterCurveSample Permission(string curve, float evaluationDelta, string action, long? instance)
        {
            float sum = 0f, coverage = 0f;
            foreach (var voice in voices)
            {
                if (action != null && (voice.ActionKey != action || instance.HasValue && voice.InstanceId != instance.Value)) continue;
                if (action != null) coverage += voice.Weight; // 实例为空时统计指定键的所有在播片段，即使素材没有 IK 曲线。
                if (curve == null || voice.Weight <= 0f) continue;
                double time = voice.Motion.EvaluationTime(evaluationDelta);
                if (!voice.Motion.TryRead(curve, time, out float value)) continue;
                sum += voice.Weight * value;
                if (action == null) coverage += voice.Weight;
            }
            return new CharacterCurveSample(sum, coverage);
        }
    }
}
