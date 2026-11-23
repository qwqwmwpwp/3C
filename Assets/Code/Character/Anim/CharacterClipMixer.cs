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
        private sealed class Voice
        {
            public CharacterClipState State;
            public CharacterMotionPlayable Motion;
            public Playable Clip => Motion.Root;
            public string ActionKey;
            public long InstanceId;
            public int Hash, Port;
            public Vector3 InertiaSuppression;
            public float StartWeight, StartWeightSpeed, Weight, WeightSpeed, NominalSpeed;
            public bool Stop;
            public CharacterClipProgress Progress => new CharacterClipProgress(Hash, Motion.Length > 0f ? (float)(Clip.GetTime() / Motion.Length) : 0f, Motion.Loop);
        }

        private readonly PlayableGraph graph;
        private static readonly ProfilerMarker CreateNode = new ProfilerMarker("3C.Animation.CreateNode");
        internal static readonly ProfilerMarker DestroyNode = new ProfilerMarker("3C.Animation.DestroyNode");
        private readonly List<Voice> voices = new List<Voice>();
        public readonly AnimationMixerPlayable Mixer;
        private float fadeTime, elapsed, carrySeconds;
        private float routeClockDuration, routeEntryCoverage;
        private bool smoothFade;
        private Voice target;
        public bool Empty => target == null;
        public float? ClockPhase { get; set; }

        public CharacterClipMixer(PlayableGraph graph)
        {
            this.graph = graph;
            using (CreateNode.Auto()) Mixer = AnimationMixerPlayable.Create(graph, 0);
        }

        public void Enter(CharacterClipState state, AnimationClip asset, CharacterClipCurves curves, CharacterClipCurves secondary, int hash, float nominalSpeed,
            bool stop, float start, float duration, bool resumeLoop, bool resetEvents, in CharacterAnimationDemand demand)
        {
            fadeTime = target != null ? Mathf.Max(0f, duration) : 0f;
            routeClockDuration = demand.HasHandoff && demand.Timing.Handoff.UsesRouteBlend ? demand.Timing.Handoff.Duration : 0f;
            routeEntryCoverage = demand.EntryCoverage;
            if (routeClockDuration > 0f && fadeTime > 0f)
                fadeTime = routeClockDuration * demand.Timing.Handoff.FullFrom;
            elapsed = 0f;
            carrySeconds = fadeTime / 3f;
            smoothFade = routeClockDuration > 0f || demand.Region == CharacterAnimationBodyRegion.UpperBody ||
                demand.Region == CharacterAnimationBodyRegion.FullBodyOverlay;
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
                returning.InstanceId = demand.InstanceId;
                target = returning; // 保留源片段的真实时间，反向淡回当前步态。
                return;
            }
            int port = 0;
            while (port < Mixer.GetInputCount() && Mixer.GetInput(port).IsValid()) port++;
            if (port == Mixer.GetInputCount()) Mixer.SetInputCount(port + 1);
            CharacterMotionPlayable motion;
            using (CreateNode.Auto()) motion = new CharacterMotionPlayable(graph, asset, state.Blend, curves, secondary);
            motion.Seek(start);
            if (resetEvents) motion.Seek(start); // 同步上次求值时间，跳过恢复点之前的片段事件（Timeline 1184106）。
            graph.Connect(motion.Root, 0, Mixer, port);
            target = new Voice { ActionKey = demand.Key, InstanceId = demand.InstanceId, State = state, Motion = motion, Hash = hash, Port = port,
                NominalSpeed = nominalSpeed, Stop = stop, Weight = fadeTime > 0f ? 0f : 1f };
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
            ClockPhase = null;
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

        public void Advance(float dt, float? stride, float stopRate)
        {
            bool routeBlend = routeClockDuration > 0f && ClockPhase.HasValue;
            elapsed = routeBlend ? ClockPhase.Value * routeClockDuration : elapsed + dt;
            var blend = CharacterBlendCurve.Sample(elapsed, fadeTime, smoothFade, carrySeconds);
            if (routeBlend) blend = blend.WithinLayer(routeEntryCoverage);
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
                float rate = 1f;
                if (voice.Stop) rate = stopRate;
                else if (stride.HasValue && voice.NominalSpeed > 0f) rate = stride.Value / voice.NominalSpeed;
                if (voice == target && ClockPhase.HasValue && dt > 0f)
                {
                    double seconds = (double)ClockPhase.Value * voice.Motion.Length;
                    voice.Motion.Advance(System.Math.Max(0d, seconds - voice.Clip.GetTime()) / dt, dt);
                }
                else voice.Motion.Advance(voice.State.Speed * rate, dt);
            }
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
