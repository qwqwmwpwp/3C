#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    internal sealed class CharacterReplayEvents
    {
        private sealed class Voice
        {
            public AnimationClip Clip;
            public AnimationClipPlayable Playable;
            public AnimationEvent[] Markers;
            public string Key;
            public long Instance;
            public double Last;
        }

        private struct Pass
        {
            public AnimationClip Clip;
            public AnimationEvent Marker;
            public string Key;
            public long Instance;
            public int Frame;
            public bool Matched;
        }

        private readonly List<Voice> voices = new List<Voice>();
        private readonly Dictionary<AnimationClip, AnimationEvent[]> clips = new Dictionary<AnimationClip, AnimationEvent[]>();
        private readonly List<Pass> expected = new List<Pass>(), actual = new List<Pass>();
        internal bool Verified { get; private set; }
        private int unsupported;
        private string unsupportedName;

        public void Enter(AnimationClip clip, AnimationClipPlayable playable, in CharacterAnimationDemand demand,
            float startSeconds, bool resumed)
        {
            if (!demand.Rule.WholeBody || demand.Playback == null) return;
            if (!clips.TryGetValue(clip, out var markers)) clips.Add(clip, markers = clip.events);
            voices.Add(new Voice { Clip = clip, Playable = playable, Markers = markers, Key = demand.Key,
                Instance = demand.InstanceId, Last = resumed ? startSeconds : -0.00001 });
        }

        public void Sample()
        {
            for (int i = voices.Count - 1; i >= 0; i--)
            {
                var voice = voices[i];
                if (!voice.Playable.IsValid()) { voices.RemoveAt(i); continue; }
                double time = voice.Playable.GetTime();
                foreach (var marker in voice.Markers)
                {
                    if (marker.time <= voice.Last || marker.time > time) continue;
                    if (marker.functionName != nameof(CharacterReplayEventProbe.SwitchSocket))
                    {
                        unsupported++;
                        unsupportedName = marker.functionName;
                        continue;
                    }
                    expected.Add(new Pass { Clip = voice.Clip, Marker = marker, Key = voice.Key, Instance = voice.Instance, Frame = Time.frameCount });
                }
                voice.Last = time;
            }
        }

        public void Received(AnimationClip clip, AnimationEvent marker)
        {
            if (clip && clips.ContainsKey(clip))
                actual.Add(new Pass { Clip = clip, Marker = marker, Frame = Time.frameCount });
        }

        private static bool Same(in Pass a, in Pass b) => a.Clip == b.Clip && a.Marker.functionName == b.Marker.functionName &&
            Mathf.Abs(a.Marker.time - b.Marker.time) <= 0.0001f && a.Marker.stringParameter == b.Marker.stringParameter &&
            a.Marker.intParameter == b.Marker.intParameter && a.Marker.floatParameter == b.Marker.floatParameter &&
            a.Marker.objectReferenceParameter == b.Marker.objectReferenceParameter;

        public bool SameReceipts(CharacterReplayEvents other)
        {
            if (!Verified || !other.Verified || actual.Count != other.actual.Count) return false;
            for (int i = 0; i < actual.Count; i++) if (!Same(actual[i], other.actual[i])) return false;
            return true;
        }

        public string FrameAt(int frame)
        {
            var text = new System.Text.StringBuilder();
            foreach (var pass in expected)
                if (pass.Frame == frame) text.Append($" 应收 {pass.Key} #{pass.Instance} {pass.Marker.functionName}@{pass.Marker.time:F3}s；");
            foreach (var receipt in actual)
                if (receipt.Frame == frame) text.Append($" 实收 {receipt.Clip.name} {receipt.Marker.functionName}@{receipt.Marker.time:F3}s；");
            return text.Length == 0 ? "" : "\n片段事件" + text;
        }

        public string Describe()
        {
            for (int i = 0; i < expected.Count; i++)
            {
                var pass = expected[i]; pass.Matched = false; expected[i] = pass;
            }
            int missing = 0, extra = 0, ambiguous = 0;
            string problem = null;
            foreach (var receipt in actual)
            {
                int best = -1, nearest = 2;
                bool tie = false;
                for (int i = 0; i < expected.Count; i++)
                {
                    var pass = expected[i];
                    int gap = Mathf.Abs(pass.Frame - receipt.Frame);
                    if (pass.Matched || gap > 1 || gap > nearest || !Same(pass, receipt)) continue;
                    if (gap == nearest && best >= 0 && (expected[best].Instance != pass.Instance || expected[best].Key != pass.Key)) { tie = true; continue; }
                    if (gap < nearest) tie = false;
                    best = i; nearest = gap;
                }
                if (best < 0)
                {
                    extra++;
                    problem ??= $"多收或超出窗口：帧 {receipt.Frame}，{receipt.Clip.name} @ {receipt.Marker.time:F3}s";
                    continue;
                }
                var found = expected[best]; found.Matched = true; expected[best] = found;
                if (tie) ambiguous++;
            }
            foreach (var pass in expected)
                if (!pass.Matched)
                {
                    missing++;
                    problem ??= $"未收到：帧 {pass.Frame}，{pass.Key} #{pass.Instance} @ {pass.Marker.time:F3}s";
                }
            Verified = expected.Count > 0 && missing == 0 && extra == 0 && ambiguous == 0 && unsupported == 0;
            return $"片段标记应收 {expected.Count}，实收 {actual.Count}，漏 {missing}，额外 {extra}，实例归属不明 {ambiguous}" +
                (unsupported == 0 ? "" : $"，另有 {unsupported} 个未监听标记（{unsupportedName}）") +
                (problem == null ? "" : "\n" + problem);
        }
    }
}
#endif
