// 改编自 https://github.com/0xcjy/UmaViewer
// 来源提交 12c77723169259071ce86e2eab9c871de4a0919d，参考 LiveTimelineControl.cs 与镜头关键帧数据。
// 保留由终点关键帧控制插值及相对终点的贝塞尔控制点语义，
// 使用独立 Unity 数据描述镜头时间轴。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>前一关键帧到当前关键帧的插值方式，由当前终点关键帧配置。</summary>
    public enum CameraKeyInterpolation
    {
        Hold = 0,
        Linear = 1,
        Curve = 2,
        EaseInOut = 3,
        Preset = 4
    }

    /// <summary>轨道默认坐标系，可使用世界坐标或播放器提供的锚点参考。</summary>
    public enum CameraTrackSpace
    {
        World = 0,
        Anchor = 1
    }

    /// <summary>位置或注视点关键帧，保存时间、坐标参考及到达本帧的插值配置。</summary>
    [Serializable]
    public sealed class CameraVectorKey
    {
        [Min(0f), Tooltip("关键帧时间，单位为秒，须在时间轴时长内且按严格升序排列。")]
        public float time;
        [Tooltip("当前参考坐标系中的位置或偏移，单位为 Unity 单位。Target 模式下相对绑定目标计算。")]
        public Vector3 value;
        [Tooltip("控制前一关键帧到本关键帧之间的插值。Hold 在本关键帧时刻直接切换。")]
        public CameraKeyInterpolation interpolation = CameraKeyInterpolation.Linear;
        [Tooltip("Interpolation 为 Curve 时生效，横轴为归一化段时间 0～1，纵轴为插值进度，允许超调。")]
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [Tooltip("Interpolation 为 Preset 时生效，控制前一关键帧到本关键帧的缓动。")]
        public CameraEaseType easing;
        [Tooltip("Track 使用轨道坐标系，World 使用世界坐标，Target 使用播放器提供的命名目标。")]
        public CameraVectorReference reference;
        [Tooltip("播放器 Target Bindings 中区分大小写的 ID，仅在 Reference 为 Target 时生效。")]
        public string targetId;
        [Tooltip("World 偏移沿世界轴，Target 偏移随绑定节点旋转。偏移大小由绑定的 Offset Scale 控制。")]
        public CameraTargetOffsetSpace targetOffsetSpace;
        [Tooltip("最多 15 个相对本关键帧 Value 的贝塞尔控制点偏移，使用本关键帧的坐标系。")]
        public Vector3[] bezierControlPoints = Array.Empty<Vector3>();
    }

    /// <summary>视野角或翻滚角关键帧，数值单位为度。</summary>
    [Serializable]
    public sealed class CameraFloatKey
    {
        [Min(0f), Tooltip("关键帧时间，单位为秒，须在时间轴时长内且按严格升序排列。")]
        public float time;
        [Tooltip("角度值，单位为度。视野角范围为 1～179，翻滚角支持整圈旋转。")]
        public float value;
        [Tooltip("控制前一关键帧到本关键帧之间的插值。Hold 在本关键帧时刻直接切换。")]
        public CameraKeyInterpolation interpolation = CameraKeyInterpolation.Linear;
        [Tooltip("Interpolation 为 Curve 时生效，横轴为归一化段时间 0～1，纵轴为插值进度，允许超调。")]
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [Tooltip("Interpolation 为 Preset 时生效，控制前一关键帧到本关键帧的缓动。")]
        public CameraEaseType easing;
    }

    /// <summary>独立镜头时间轴资产，保存位置、注视点、视野角与翻滚角轨道，由播放器绑定场景目标。</summary>
    [CreateAssetMenu(fileName = "CameraTimeline", menuName = "3C/镜头时间轴")]
    public sealed class CameraTimelineAsset : ScriptableObject
    {
        public const int MaxBezierControlPoints = 15;

        [Min(0.01f), Tooltip("时间轴总时长，单位为秒，须为有限正数。所有关键帧时间均须落在此范围内。")]
        public float duration = 5f;
        [Tooltip("位置轨道的默认坐标系，仅对 Reference 为 Track 的关键帧生效。")]
        public CameraTrackSpace positionSpace;
        [Tooltip("注视轨道的默认坐标系，Anchor 模式优先使用 Look At Target，留空时回退到 Anchor。")]
        public CameraTrackSpace lookAtSpace;
        [Tooltip("镜头位置关键帧，至少一个，时间须严格递增。插值方式配置在每段终点关键帧上。")]
        public List<CameraVectorKey> positionKeys = new List<CameraVectorKey>();
        [Tooltip("镜头注视点关键帧，至少一个，时间须严格递增。")]
        public List<CameraVectorKey> lookAtKeys = new List<CameraVectorKey>();
        [Tooltip("可选视野角轨道，单位为度，有效范围为 1～179。空轨道使用 60 度。")]
        public List<CameraFloatKey> fieldOfViewKeys = new List<CameraFloatKey>();
        [Tooltip("可选翻滚角轨道，单位为度，支持整圈旋转。空轨道使用 0 度。")]
        public List<CameraFloatKey> rollKeys = new List<CameraFloatKey>();

        /// <summary>只读校验时间轴数据，成功时返回 true 且错误为空，失败时返回中文原因。关键帧须预先按时间严格升序排列。</summary>
        public bool TryValidate(out string error)
        {
            if (!IsFinite(duration) || duration <= 0f)
            {
                error = "时长必须为有限正数，单位为秒。";
                return false;
            }

            if (!IsValidSpace(positionSpace) || !IsValidSpace(lookAtSpace))
            {
                error = "轨道坐标系必须为 World 或 Anchor。";
                return false;
            }

            return ValidateVectorKeys(positionKeys, "位置轨道", out error)
                && ValidateVectorKeys(lookAtKeys, "注视轨道", out error)
                && ValidateFloatKeys(fieldOfViewKeys, "视野角轨道", true, out error)
                && ValidateFloatKeys(rollKeys, "翻滚角轨道", false, out error);
        }

        private bool ValidateVectorKeys(List<CameraVectorKey> keys, string track, out string error)
        {
            if (keys == null || keys.Count == 0)
            {
                error = $"{track}至少需要一个关键帧。";
                return false;
            }

            float previousTime = -1f;
            for (int i = 0; i < keys.Count; i++)
            {
                CameraVectorKey key = keys[i];
                if (key == null)
                {
                    error = $"{track}的关键帧 {i} 缺失。";
                    return false;
                }

                if (!ValidateKey(key.time, previousTime, key.interpolation, key.curve, key.easing, out error))
                {
                    error = $"{track}的关键帧 {i} 校验失败，{error}";
                    return false;
                }

                if (!IsFinite(key.value))
                {
                    error = $"{track}的关键帧 {i} 包含非有限数值。";
                    return false;
                }

                if (key.reference < CameraVectorReference.Track || key.reference > CameraVectorReference.Target
                    || (key.reference == CameraVectorReference.Target
                        && (string.IsNullOrWhiteSpace(key.targetId)
                            || (key.targetOffsetSpace != CameraTargetOffsetSpace.World
                                && key.targetOffsetSpace != CameraTargetOffsetSpace.Target))))
                {
                    error = $"{track}的关键帧 {i} 需要有效的参考模式及命名目标配置。";
                    return false;
                }

                Vector3[] points = key.bezierControlPoints;
                if (points != null)
                {
                    if (points.Length > MaxBezierControlPoints)
                    {
                        error = $"{track}的关键帧 {i} 最多支持 {MaxBezierControlPoints} 个贝塞尔控制点。";
                        return false;
                    }

                    for (int j = 0; j < points.Length; j++)
                    {
                        if (!IsFinite(points[j]))
                        {
                            error = $"{track}的关键帧 {i} 包含非有限数值的贝塞尔控制点。";
                            return false;
                        }
                    }
                }

                previousTime = key.time;
            }

            error = null;
            return true;
        }

        private bool ValidateFloatKeys(List<CameraFloatKey> keys, string track, bool isFieldOfView,
            out string error)
        {
            float previousTime = -1f;
            for (int i = 0; keys != null && i < keys.Count; i++)
            {
                CameraFloatKey key = keys[i];
                if (key == null)
                {
                    error = $"{track}的关键帧 {i} 缺失。";
                    return false;
                }

                if (!ValidateKey(key.time, previousTime, key.interpolation, key.curve, key.easing, out error))
                {
                    error = $"{track}的关键帧 {i} 校验失败，{error}";
                    return false;
                }

                if (!IsFinite(key.value) || (isFieldOfView && (key.value < 1f || key.value > 179f)))
                {
                    error = isFieldOfView
                        ? $"{track}的关键帧 {i} 必须为 1～179 度之间的有限数值。"
                        : $"{track}的关键帧 {i} 必须为有限数值。";
                    return false;
                }

                previousTime = key.time;
            }

            error = null;
            return true;
        }

        private bool ValidateKey(float time, float previousTime, CameraKeyInterpolation interpolation,
            AnimationCurve curve, CameraEaseType easing, out string error)
        {
            if (!IsFinite(time) || time < 0f || time > duration || time <= previousTime)
            {
                error = "关键帧时间必须为有限数值、严格递增，并位于 0 到 Duration 之间，单位为秒。";
                return false;
            }

            if (interpolation < CameraKeyInterpolation.Hold || interpolation > CameraKeyInterpolation.Preset)
            {
                error = "未知的插值模式。";
                return false;
            }

            if (interpolation == CameraKeyInterpolation.Curve && !ValidateCurve(curve, out error))
                return false;

            if (interpolation == CameraKeyInterpolation.Preset && !CameraEasing.IsValid(easing))
            {
                error = "未知的缓动预设。";
                return false;
            }

            error = null;
            return true;
        }

        private static bool ValidateCurve(AnimationCurve curve, out string error)
        {
            if (curve == null || curve.length == 0)
            {
                error = "曲线插值需要至少包含一个关键帧的曲线。";
                return false;
            }

            float previousTime = float.NegativeInfinity;
            for (int i = 0; i < curve.length; i++)
            {
                Keyframe key = curve[i];
                // Unity 以无穷切线表示常量或阶跃段，NaN 切线属于无效数据。
                if (!IsFinite(key.time) || !IsFinite(key.value) || key.time <= previousTime
                    || float.IsNaN(key.inTangent) || float.IsNaN(key.outTangent)
                    || !IsFinite(key.inWeight) || !IsFinite(key.outWeight)
                    || key.inWeight < 0f || key.inWeight > 1f || key.outWeight < 0f || key.outWeight > 1f
                    || ((int)key.weightedMode & ~3) != 0)
                {
                    error = "曲线关键帧需要有限且严格递增的时间、有限数值，以及有效的切线和权重。";
                    return false;
                }

                previousTime = key.time;
            }

            if (!IsFinite(curve.Evaluate(0f)) || !IsFinite(curve.Evaluate(1f)))
            {
                error = "曲线在归一化区间的两个端点必须产生有限数值。";
                return false;
            }

            error = null;
            return true;
        }

        private static bool IsValidSpace(CameraTrackSpace space)
        {
            return space == CameraTrackSpace.World || space == CameraTrackSpace.Anchor;
        }

        internal static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }
    }
}
