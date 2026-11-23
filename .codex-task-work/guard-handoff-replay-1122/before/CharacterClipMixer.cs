using System.Collections.Generic;
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
            public AnimationClip Asset;
            public CharacterClipCurves Curves;
            public AnimationClipPlayable Clip;
            public int Hash, Port;
            public float StartWeight, Weight, NominalSpeed;
            public bool Stop;
            public CharacterClipProgress Progress => new CharacterClipProgress(Hash, Asset.length > 0f ? (float)(Clip.GetTime() / Asset.length) : 0f, Asset.isLooping);
        }

        private readonly PlayableGraph graph;
        private readonly List<Voice> voices = new List<Voice>();
        public readonly AnimationMixerPlayable Mixer;
        private float fadeTime, elapsed;
        private Voice target;
        public bool Empty => target == null;
        public float? ClockPhase { get; set; }

        public CharacterClipMixer(PlayableGraph graph)
        {
            this.graph = graph;
            Mixer = AnimationMixerPlayable.Create(graph, 0);
        }

        public void Enter(CharacterClipState state, AnimationClip asset, CharacterClipCurves curves, int hash, float nominalSpeed,
            bool stop, float start, float duration, bool resumeLoop, bool resetEvents)
        {
            fadeTime = target != null ? Mathf.Max(0f, duration) : 0f;
            elapsed = 0f;
            Voice returning = null;
            for (int i = voices.Count - 1; i >= 0; i--)
            {
                var voice = voices[i];
                if (voice.Weight <= 0.00001f)
                {
                    Remove(i);
                    continue;
                }
                voice.StartWeight = voice.Weight;
                if (resumeLoop && voice.Hash == hash && asset.isLooping &&
                    (returning == null || voice.Weight > returning.Weight)) returning = voice;
            }
            if (fadeTime <= 0f)
                for (int i = voices.Count - 1; i >= 0; i--)
                    if (voices[i] != returning) Remove(i);
            if (returning != null)
            {
                target = returning; // 保留源片段的真实时间，反向淡回当前步态。
                return;
            }
            int port = 0;
            while (port < Mixer.GetInputCount() && Mixer.GetInput(port).IsValid()) port++;
            if (port == Mixer.GetInputCount()) Mixer.SetInputCount(port + 1);
            var clip = AnimationClipPlayable.Create(graph, asset);
            clip.SetApplyFootIK(false);
            clip.SetApplyPlayableIK(false);
            clip.SetTime(start);
            if (resetEvents) clip.SetTime(start); // 同步上次求值时间，跳过恢复点之前的片段事件（Timeline 1184106）。
            graph.Connect(clip, 0, Mixer, port);
            target = new Voice { State = state, Asset = asset, Curves = curves, Clip = clip, Hash = hash, Port = port,
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
            graph.DestroyPlayable(voice.Clip);
            voices.RemoveAt(index);
        }

        public void CompleteTransition() => elapsed = Mathf.Max(elapsed, fadeTime);

        public void Advance(float dt, float? stride, float stopRate)
        {
            elapsed += dt;
            float blend = fadeTime > 0f ? Mathf.Clamp01(elapsed / fadeTime) : 1f;
            for (int i = voices.Count - 1; i >= 0; i--)
            {
                var voice = voices[i];
                if (voice != target && blend >= 1f)
                {
                    Remove(i);
                    continue;
                }
                voice.Weight = Mathf.Lerp(voice.StartWeight, voice == target ? 1f : 0f, blend);
                Mixer.SetInputWeight(voice.Port, voice.Weight);
                float rate = 1f;
                if (voice.Stop) rate = stopRate;
                else if (stride.HasValue && voice.NominalSpeed > 0f) rate = stride.Value / voice.NominalSpeed;
                if (voice == target && ClockPhase.HasValue && dt > 0f)
                {
                    double seconds = (double)ClockPhase.Value * voice.Asset.length;
                    voice.Clip.SetSpeed(System.Math.Max(0d, seconds - voice.Clip.GetTime()) / dt);
                }
                else voice.Clip.SetSpeed(voice.State.Speed * rate);
            }
        }

        public float BlendedLength()
        {
            float length = 0f;
            foreach (var voice in voices) length += voice.Weight * voice.Asset.length;
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
                length += voice.Asset.length * voice.Weight;
                weight += voice.Weight;
            }
            return new CharacterLayerObservation((primary ?? target).Progress, target.Progress, primary != null,
                target.Weight, weight > 0f ? length / weight : target.Asset.length, target.Asset.length);
        }

        public CharacterCurveSample Permission(string curve, float evaluationDelta)
        {
            float sum = 0f, coverage = 0f;
            foreach (var voice in voices)
            {
                double time = voice.Clip.GetTime() + evaluationDelta * voice.Clip.GetSpeed();
                if (voice.Weight <= 0f || voice.Curves == null || !voice.Curves.TryRead(curve, time, out float value)) continue;
                sum += voice.Weight * value;
                coverage += voice.Weight;
            }
            return new CharacterCurveSample(sum, coverage);
        }
    }
}
