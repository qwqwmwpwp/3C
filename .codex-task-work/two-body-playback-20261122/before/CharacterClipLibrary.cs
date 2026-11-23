using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [Serializable]
    internal sealed class CharacterClipCurve
    {
        public string Name;
        public AnimationCurve Curve;
        public bool InSeconds;

        public float Evaluate(float phase, float clipSeconds) =>
            Mathf.Clamp01(Curve.Evaluate(InSeconds ? phase * clipSeconds : phase));
    }

    [Serializable]
    internal sealed class CharacterClipCurves
    {
        public AnimationClip Clip;
        public CharacterClipCurve[] Curves;

        public CharacterClipCurve Find(string name)
        {
            if (Curves != null)
                foreach (var curve in Curves)
                    if (curve != null && curve.Name == name && curve.Curve != null) return curve;
            return null;
        }

        public bool TryRead(string name, double time, out float value)
        {
            value = 0f;
            var curve = Find(name);
            if (curve == null || !Clip || Clip.length <= 0f) return false;
            float phase = (float)(time / Clip.length);
            phase = Clip.isLooping ? Mathf.Repeat(phase, 1f) : Mathf.Clamp01(phase);
            value = curve.Evaluate(phase, Clip.length);
            return true;
        }
    }

    internal readonly struct CharacterCurveSample
    {
        public readonly float Value, Coverage; // 权重值以及具有该曲线的混合贡献比例。
        public bool Known => Coverage >= 0.9999f;

        public CharacterCurveSample(float value, float coverage)
        {
            Value = Mathf.Clamp01(value);
            Coverage = Mathf.Clamp01(coverage);
        }
    }

    [Serializable]
    internal sealed class CharacterClipState
    {
        public string Path;
        public AnimationClip Clip;
        public int Layer;
        public float Speed = 1f;
    }

    [Serializable]
    internal sealed class CharacterClipLayer
    {
        public AvatarMask Mask;
        public bool Additive;
        public float Weight;
        public string DefaultState;
    }

    /// <summary>控制器是编辑时的状态目录。烘焙后的路径、分层遮罩和素材曲线可供 Player 直接使用。</summary>
    [Serializable]
    internal sealed class CharacterClipLibrary
    {
        public CharacterClipLayer[] Layers;
        public CharacterClipState[] States;
        public CharacterClipCurves[] Clips;
        public bool Ready => Layers != null && Layers.Length > 0 && States != null && States.Length > 0 && Clips != null;
    }
}
