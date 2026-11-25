using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    // 编辑器采样和运行时播放共用混合节点、相位与接触曲线。
    internal sealed class CharacterMotionPlayable
    {
        private readonly AnimationClip first;
        private readonly CharacterMotionBlend blend;
        private readonly AnimationClipPlayable firstNode, secondNode;
        private readonly CharacterClipCurves firstCurves, secondCurves;
        public readonly Playable Root;
        public readonly float Length;
        public readonly bool Loop;

        public CharacterMotionPlayable(PlayableGraph graph, AnimationClip first, CharacterMotionBlend blend,
            CharacterClipCurves firstCurves = null, CharacterClipCurves secondCurves = null)
        {
            this.first = first;
            this.blend = blend;
            this.firstCurves = firstCurves;
            this.secondCurves = secondCurves;
            firstNode = Create(graph, first);
            Length = blend.Second ? blend.Seconds : first.length;
            Loop = blend.Second ? blend.Loop : first.isLooping;
            if (!blend.Second)
            {
                Root = firstNode;
                return;
            }
            secondNode = Create(graph, blend.Second);
            Root = AnimationMixerPlayable.Create(graph, 2);
            Root.SetSpeed(0d);
            Root.SetPropagateSetTime(false); // 两个输入按各自的相位与速率定位。
            firstNode.SetSpeed(0d);
            secondNode.SetSpeed(0d);
            graph.Connect(firstNode, 0, Root, 0);
            graph.Connect(secondNode, 0, Root, 1);
        }

        private static AnimationClipPlayable Create(PlayableGraph graph, AnimationClip asset)
        {
            var clip = AnimationClipPlayable.Create(graph, asset);
            clip.SetApplyFootIK(false);
            clip.SetApplyPlayableIK(false);
            return clip;
        }

        public void Seek(double seconds)
        {
            Root.SetTime(seconds);
            if (blend.Second) Sample(seconds);
        }

        public void Advance(double rate, float dt)
        {
            if (!blend.Second)
            {
                Root.SetSpeed(rate);
                return;
            }
            double previous = Root.GetTime();
            double next = previous + dt * rate;
            if (!Loop) next = System.Math.Max(0d, System.Math.Min(next, Length));
            if (next != previous) Seek(next); // 混合态手动走一次，图求值时保持两个输入的采样时间。
        }

        public double EvaluationTime(float dt) => Root.GetTime() + (blend.Second ? 0d : dt * Root.GetSpeed());

        private void Sample(double seconds)
        {
            float time = blend.Time(seconds);
            float weight = blend.Weight(time);
            firstNode.SetTime(CharacterMotionBlend.ClipTime(first, blend.FirstPhase, blend.FirstRate, time));
            secondNode.SetTime(CharacterMotionBlend.ClipTime(blend.Second, blend.SecondPhase, blend.SecondRate, time));
            Root.SetInputWeight(0, 1f - weight);
            Root.SetInputWeight(1, weight);
        }

        public bool TryRead(string name, double seconds, out float value)
        {
            value = 0f;
            if (!blend.Second) return firstCurves != null && firstCurves.TryRead(name, seconds, out value);
            float time = blend.Time(seconds);
            float a = 0f, b = 0f;
            bool hasA = firstCurves != null && firstCurves.TryRead(name,
                CharacterMotionBlend.ClipTime(first, blend.FirstPhase, blend.FirstRate, time), out a);
            bool hasB = secondCurves != null && secondCurves.TryRead(name,
                CharacterMotionBlend.ClipTime(blend.Second, blend.SecondPhase, blend.SecondRate, time), out b);
            value = hasA && hasB ? Mathf.Lerp(a, b, blend.Weight(time)) : hasA ? a : b;
            return hasA || hasB;
        }
    }
}
