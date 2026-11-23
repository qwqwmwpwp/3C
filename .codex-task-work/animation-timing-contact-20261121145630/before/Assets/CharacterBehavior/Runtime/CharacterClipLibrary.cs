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
    }

    [Serializable]
    internal sealed class CharacterClipCurves
    {
        public AnimationClip Clip;
        public CharacterClipCurve[] Curves;

        public float Read(string name, double time)
        {
            if (!Clip || Clip.length <= 0f || Curves == null) return 0f;
            float phase = (float)(time / Clip.length);
            phase = Clip.isLooping ? Mathf.Repeat(phase, 1f) : Mathf.Clamp01(phase);
            foreach (var curve in Curves)
                if (curve.Name == name && curve.Curve != null)
                    return Mathf.Clamp01(curve.Curve.Evaluate(curve.InSeconds ? phase * Clip.length : phase));
            return 0f;
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
