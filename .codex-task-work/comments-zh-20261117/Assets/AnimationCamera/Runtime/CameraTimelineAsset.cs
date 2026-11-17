// Adapted from https://github.com/0xcjy/UmaViewer
// Source commit: 12c77723169259071ce86e2eab9c871de4a0919d, LiveTimelineControl.cs and camera key data.
// Keeps destination-key interpolation and destination-relative Bezier controls;
// replaces Gallop/Director/character/database dependencies with standalone Unity data.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    public enum CameraKeyInterpolation
    {
        Hold = 0,
        Linear = 1,
        Curve = 2,
        EaseInOut = 3,
        Preset = 4
    }

    public enum CameraTrackSpace
    {
        World = 0,
        Anchor = 1
    }

    [Serializable]
    public sealed class CameraVectorKey
    {
        [Min(0f)] public float time;
        public Vector3 value;
        [Tooltip("Controls the segment arriving at this key. Hold cuts at this key's time.")]
        public CameraKeyInterpolation interpolation = CameraKeyInterpolation.Linear;
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [Tooltip("Used when Interpolation is Preset; controls the segment arriving at this key.")]
        public CameraEaseType easing;
        [Tooltip("Track keeps the existing track space. Target uses a named binding supplied by the player.")]
        public CameraVectorReference reference;
        [Tooltip("Case-sensitive ID from the player's Target Bindings. Used only for Target reference.")]
        public string targetId;
        [Tooltip("World offsets keep world axes. Target offsets rotate with the bound node. Transform scale is ignored.")]
        public CameraTargetOffsetSpace targetOffsetSpace;
        [Tooltip("Zero to fifteen offsets from this key's value, in this key's coordinate space.")]
        public Vector3[] bezierControlPoints = Array.Empty<Vector3>();
    }

    [Serializable]
    public sealed class CameraFloatKey
    {
        [Min(0f)] public float time;
        public float value;
        [Tooltip("Controls the segment arriving at this key. Hold cuts at this key's time.")]
        public CameraKeyInterpolation interpolation = CameraKeyInterpolation.Linear;
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [Tooltip("Used when Interpolation is Preset; controls the segment arriving at this key.")]
        public CameraEaseType easing;
    }

    [CreateAssetMenu(fileName = "CameraTimeline", menuName = "3C/Animation Camera Timeline")]
    public sealed class CameraTimelineAsset : ScriptableObject
    {
        public const int MaxBezierControlPoints = 15;

        [Min(0.01f)] public float duration = 5f;
        public CameraTrackSpace positionSpace;
        public CameraTrackSpace lookAtSpace;
        public List<CameraVectorKey> positionKeys = new List<CameraVectorKey>();
        public List<CameraVectorKey> lookAtKeys = new List<CameraVectorKey>();
        [Tooltip("Optional. An empty track uses a 60 degree field of view.")]
        public List<CameraFloatKey> fieldOfViewKeys = new List<CameraFloatKey>();
        [Tooltip("Optional. An empty track uses zero roll. Values are degrees, allowing full turns.")]
        public List<CameraFloatKey> rollKeys = new List<CameraFloatKey>();

        /// <summary>Checks authored data without sorting or changing the asset.</summary>
        public bool TryValidate(out string error)
        {
            if (!IsFinite(duration) || duration <= 0f)
            {
                error = "Duration must be finite and greater than zero.";
                return false;
            }

            if (!IsValidSpace(positionSpace) || !IsValidSpace(lookAtSpace))
            {
                error = "Track space must be World or Anchor.";
                return false;
            }

            return ValidateVectorKeys(positionKeys, "Position", out error)
                && ValidateVectorKeys(lookAtKeys, "Look-at", out error)
                && ValidateFloatKeys(fieldOfViewKeys, "Field of view", true, out error)
                && ValidateFloatKeys(rollKeys, "Roll", false, out error);
        }

        private bool ValidateVectorKeys(List<CameraVectorKey> keys, string track, out string error)
        {
            if (keys == null || keys.Count == 0)
            {
                error = $"{track} requires at least one key.";
                return false;
            }

            float previousTime = -1f;
            for (int i = 0; i < keys.Count; i++)
            {
                CameraVectorKey key = keys[i];
                if (key == null)
                {
                    error = $"{track} key {i} is missing.";
                    return false;
                }

                if (!ValidateKey(key.time, previousTime, key.interpolation, key.curve, key.easing, out error))
                {
                    error = $"{track} key {i}: {error}";
                    return false;
                }

                if (!IsFinite(key.value))
                {
                    error = $"{track} key {i} has a non-finite value.";
                    return false;
                }

                if (key.reference < CameraVectorReference.Track || key.reference > CameraVectorReference.Target
                    || (key.reference == CameraVectorReference.Target
                        && (string.IsNullOrWhiteSpace(key.targetId)
                            || (key.targetOffsetSpace != CameraTargetOffsetSpace.World
                                && key.targetOffsetSpace != CameraTargetOffsetSpace.Target))))
                {
                    error = $"{track} key {i} requires a valid reference mode and named target configuration.";
                    return false;
                }

                Vector3[] points = key.bezierControlPoints;
                if (points != null)
                {
                    if (points.Length > MaxBezierControlPoints)
                    {
                        error = $"{track} key {i} supports at most {MaxBezierControlPoints} Bezier control points.";
                        return false;
                    }

                    for (int j = 0; j < points.Length; j++)
                    {
                        if (!IsFinite(points[j]))
                        {
                            error = $"{track} key {i} has a non-finite Bezier control point.";
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
                    error = $"{track} key {i} is missing.";
                    return false;
                }

                if (!ValidateKey(key.time, previousTime, key.interpolation, key.curve, key.easing, out error))
                {
                    error = $"{track} key {i}: {error}";
                    return false;
                }

                if (!IsFinite(key.value) || (isFieldOfView && (key.value < 1f || key.value > 179f)))
                {
                    error = isFieldOfView
                        ? $"{track} key {i} must have a finite value between 1 and 179 degrees."
                        : $"{track} key {i} must have a finite value.";
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
                error = "Times must be finite, strictly increasing, and between zero and Duration.";
                return false;
            }

            if (interpolation < CameraKeyInterpolation.Hold || interpolation > CameraKeyInterpolation.Preset)
            {
                error = "Unknown interpolation mode.";
                return false;
            }

            if (interpolation == CameraKeyInterpolation.Curve && !ValidateCurve(curve, out error))
                return false;

            if (interpolation == CameraKeyInterpolation.Preset && !CameraEasing.IsValid(easing))
            {
                error = "Unknown easing preset.";
                return false;
            }

            error = null;
            return true;
        }

        private static bool ValidateCurve(AnimationCurve curve, out string error)
        {
            if (curve == null || curve.length == 0)
            {
                error = "Curve interpolation requires a nonempty curve.";
                return false;
            }

            float previousTime = float.NegativeInfinity;
            for (int i = 0; i < curve.length; i++)
            {
                Keyframe key = curve[i];
                // Unity uses infinite tangents for constant/step segments. NaN is never valid.
                if (!IsFinite(key.time) || !IsFinite(key.value) || key.time <= previousTime
                    || float.IsNaN(key.inTangent) || float.IsNaN(key.outTangent)
                    || !IsFinite(key.inWeight) || !IsFinite(key.outWeight)
                    || key.inWeight < 0f || key.inWeight > 1f || key.outWeight < 0f || key.outWeight > 1f
                    || ((int)key.weightedMode & ~3) != 0)
                {
                    error = "Curve keys must have finite increasing times, finite values and valid tangents/weights.";
                    return false;
                }

                previousTime = key.time;
            }

            if (!IsFinite(curve.Evaluate(0f)) || !IsFinite(curve.Evaluate(1f)))
            {
                error = "Curve must produce finite values over the normalized segment.";
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
