using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存单条按归一化相位或秒读取的素材曲线。
    [Serializable]
    internal sealed class CharacterClipCurve
    {
        public string Name;
        public AnimationCurve Curve;
        public bool InSeconds;

        public float Evaluate(float phase, float clipSeconds) =>
            Mathf.Clamp01(Curve.Evaluate(InSeconds ? phase * clipSeconds : phase));
    }

    // 保存同一片段的命名曲线，并按其时间基准读取。
    [Serializable]
    internal sealed class CharacterClipCurves
    {
        public AnimationClip Clip;
        public CharacterClipCurve[] Curves;

        public CharacterClipCurve Find(string name)
        {
            if (Curves != null)
                foreach (var curve in Curves)
                    if (curve != null && curve.Name == name && curve.Curve != null && curve.Curve.length > 0) return curve;
            return null;
        }

        // 按片段的循环设置换算相位，并只读取请求的曲线。
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
        public float CoveredValue => Coverage > 0f ? Mathf.Clamp01(Value / Coverage) : 0f;

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
        [HideInInspector] public CharacterMotionBlend Blend;

        internal bool MatchesTrack(CharacterMotionTrack track, AnimationClip actual)
        {
            if (!actual || actual != track.Clip || (bool)Blend.Second != track.Mixed) return false;
            return !track.Mixed || Blend.Loop == track.Loop && Mathf.Approximately(Blend.Seconds, track.Seconds) &&
                Mathf.Approximately(Blend.FirstPhase, track.FirstPhase) && Mathf.Approximately(Blend.SecondPhase, track.SecondPhase);
        }
    }

    [Serializable]
    internal sealed class CharacterClipLayer
    {
        public AvatarMask Mask;
        public bool Additive;
        public float Weight;
        public string DefaultState;
    }

    // 保存数据组烘焙后的播放状态、上下身遮罩和命名曲线。
    [Serializable]
    internal sealed class CharacterClipLibrary
    {
        public CharacterClipLayer[] Layers;
        public CharacterClipState[] States;
        public CharacterClipCurves[] Clips;
        public bool HasBodyMasks()
        {
            if (Layers?.Length != 2 || Layers[0] == null || Layers[1] == null || Layers[0].Additive || Layers[1].Additive) return false;
            var legs = Layers[0].Mask;
            var arms = Layers[1].Mask;
            if (!legs || !arms || !legs.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Root) ||
                !legs.GetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg) || !legs.GetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg) ||
                !arms.GetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm) || !arms.GetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm)) return false;
            for (var part = AvatarMaskBodyPart.Root; part < AvatarMaskBodyPart.LastBodyPart; part++)
                if (legs.GetHumanoidBodyPartActive(part) == arms.GetHumanoidBodyPartActive(part)) return false;
            return true;
        }
        public bool Ready => Layers?.Length == 2 && States?.Length > 0 && Clips?.Length > 0;
    }
}
