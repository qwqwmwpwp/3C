using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [Serializable]
    public sealed class CharacterStopAnimationSettings
    {
        [Tooltip("左脚承重、右脚收回的非循环全身状态完整路径。")]
        public string LeftSupport;
        [Tooltip("右脚承重、左脚收回的非循环全身状态完整路径。")]
        public string RightSupport;
        [Min(0f), Tooltip("无对应烘焙收步轨道时使用的制动标记（秒）。0 表示整段按原速播放。")]
        public float BrakingClipSeconds;
    }

    /// <summary>同一收步实例的入场动量、承重侧和承重进度，来自实际获选的全身片段。</summary>
    internal readonly struct CharacterStopPoseSample
    {
        public const float SupportTransferPhase = 0.67f; // 六段收步素材中，回收脚落地承重的近似相位。
        public long InstanceId { get; }
        public bool IsValid => InstanceId > 0;
        public bool LeftSupport { get; }
        public Vector3 EntryVelocity { get; }
        public float Load { get; }

        public CharacterStopPoseSample(long instanceId, bool leftSupport, Vector3 entryVelocity, float load)
        {
            InstanceId = instanceId;
            LeftSupport = leftSupport;
            EntryVelocity = entryVelocity;
            Load = load;
        }

        public static float EvaluateLoad(float clipTime, float clipLength, float brakeMarker,
            float travelPhase, float previousLoad)
        {
            float touchdown = clipLength * SupportTransferPhase;
            float peak = touchdown > 0f ? Mathf.Min(brakeMarker, touchdown * 0.9f) : brakeMarker;
            if (clipTime <= peak)
            {
                float rise = Mathf.Clamp01(clipTime / peak);
                // 收步刚淡入时刚体可能已减速数拍。允许实际制动带动承重，峰值仍落在素材标记。
                rise = Mathf.Lerp(rise, Mathf.Max(rise, travelPhase), 0.5f * (1f - rise));
                return Mathf.Max(previousLoad, Mathf.SmoothStep(0f, 1f, rise));
            }
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(peak, touchdown, clipTime));
        }
    }

    /// <summary>记录循环走跑素材的左右脚接触点，用作全身状态切换的相位参照。</summary>
    [Serializable]
    public sealed class CharacterLocomotionGaitPhaseSettings
    {
        [Tooltip("循环走跑状态的完整路径。复用循环素材的起步状态也可单独配置。")]
        public string StateName;
        [Range(0f, 1f), Tooltip("左脚接触地面时的归一化进度。0 与 1 表示同一循环位置。")]
        public float LeftContact;
        [Range(0f, 1f), Tooltip("右脚接触地面时的归一化进度，必须与左脚接触点不同。")]
        public float RightContact;
        [Range(0.01f, 1f), Tooltip("单侧触地到对侧触地之间，当前脚承担支撑的时间占比。离地后选即将触地的对侧脚。")]
        public float SupportFraction = 0.64f;
    }

    /// <summary>以左右脚接触点划分一个循环，分别匹配两段接触之间的进度。</summary>
    internal readonly struct CharacterLocomotionGaitPhase
    {
        private readonly float leftContact;
        private readonly float leftToRight;
        private readonly float supportFraction;

        public bool IsValid => leftToRight > 0f && leftToRight < 1f;

        public CharacterLocomotionGaitPhase(float left, float right, float supportFraction)
        {
            this.supportFraction = Mathf.Clamp(InputMath.NonNegative(supportFraction, 0.64f), 0.01f, 1f);
            leftContact = 0f;
            leftToRight = 0f;
            if (!(left >= 0f && left <= 1f && right >= 0f && right <= 1f))
                return;

            leftContact = Mathf.Repeat(left, 1f);
            leftToRight = Mathf.Repeat(right - left, 1f);
        }

        // 晚于离地的空档采用下一只即将着地的脚。
        public bool IsLeftSupport(float normalizedTime)
        {
            float t = Mathf.Repeat(normalizedTime - leftContact, 1f);
            return t < leftToRight ? t < leftToRight * supportFraction :
                t - leftToRight >= (1f - leftToRight) * supportFraction;
        }

        // 仅在脚刚落地、尚有足够承重时间时允许提早接入收步。
        public bool IsEarlyStance(float normalizedTime)
        {
            float t = Mathf.Repeat(normalizedTime - leftContact, 1f);
            float contactWindow = 0.25f * supportFraction;
            return t < leftToRight ? t < leftToRight * contactWindow :
                t - leftToRight < (1f - leftToRight) * contactWindow;
        }

        public float ContactTime(bool left) => left ? leftContact : Mathf.Repeat(leftContact + leftToRight, 1f);

        /// <summary>把源状态的归一化时间映射到目标循环的对应支撑脚段，失败时由调用方使用配置起点。</summary>
        public bool TryMap(float normalizedTime, CharacterLocomotionGaitPhase target, out float start)
        {
            start = 0f;
            if (!IsValid || !target.IsValid || float.IsNaN(normalizedTime) || float.IsInfinity(normalizedTime))
                return false;

            float progress = Mathf.Repeat(Mathf.Repeat(normalizedTime, 1f) - leftContact, 1f);
            float targetProgress;
            if (progress < leftToRight)
                targetProgress = progress / leftToRight * target.leftToRight;
            else
                targetProgress = target.leftToRight + (progress - leftToRight) / (1f - leftToRight) *
                    (1f - target.leftToRight);

            start = Mathf.Repeat(target.leftContact + targetProgress, 1f);
            return true;
        }
    }
}
