// Adapted from https://github.com/0xcjy/UmaViewer
// Source commit: 12c77723169259071ce86e2eab9c871de4a0919d, LiveTimelineControl.cs and camera key data.
// Retains destination-key interpolation and relative Bezier paths; sampling is independent
// of Director, characters, previous frames, rendering, and Camera component writes.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    public struct AnimationCameraState
    {
        public Vector3 position;
        public Quaternion rotation;
        public float fieldOfView;
    }

    public static class CameraTimelineEvaluator
    {
        /// <summary>Validates and samples without changing playback, transforms, or cameras.</summary>
        public static bool TryEvaluate(CameraTimelineAsset timeline, float time, Transform anchor,
            Transform lookAtTarget, out AnimationCameraState state)
        {
            state = default;
            return timeline != null && timeline.TryValidate(out _)
                && TryEvaluateValidated(timeline, time, anchor, lookAtTarget, out state);
        }

        /// <summary>Samples caller-supplied target poses, without reading scene objects or playback history.</summary>
        public static bool TryEvaluate(CameraTimelineAsset timeline, float time,
            in CameraSampleContext context, out AnimationCameraState state)
        {
            state = default;
            return timeline != null && timeline.TryValidate(out _)
                && TryEvaluateValidated(timeline, time, context, out state);
        }

        // Playback validates once when it starts/seeks. This path never sorts or allocates key arrays.
        internal static bool TryEvaluateValidated(CameraTimelineAsset timeline, float time, Transform anchor,
            Transform lookAtTarget, out AnimationCameraState state)
        {
            state = default;
            CameraTargetSample[] buffer = null;
            return CameraSampleContext.TryCapture(anchor, lookAtTarget, null, ref buffer,
                out CameraSampleContext context, out _)
                && TryEvaluateValidated(timeline, time, context, out state);
        }

        internal static bool TryEvaluateValidated(CameraTimelineAsset timeline, float time,
            in CameraSampleContext context, out AnimationCameraState state)
        {
            state = default;
            if (timeline == null || !CameraTimelineAsset.IsFinite(time)
                || !CameraTimelineAsset.IsFinite(timeline.duration) || timeline.duration <= 0f)
                return false;

            float sampleTime = Mathf.Clamp(time, 0f, timeline.duration);
            if (!TrySampleVector(timeline.positionKeys, sampleTime, timeline.positionSpace, false,
                    context, out Vector3 position)
                || !TrySampleVector(timeline.lookAtKeys, sampleTime, timeline.lookAtSpace, true,
                    context, out Vector3 lookAt)
                || !TrySampleFloat(timeline.fieldOfViewKeys, sampleTime, 60f, out float fieldOfView)
                || !TrySampleFloat(timeline.rollKeys, sampleTime, 0f, out float roll))
                return false;

            Vector3 forward = lookAt - position;
            if (!CameraTimelineAsset.IsFinite(forward))
                return false;

            // A fixed fallback makes coincident and vertical shots deterministic for arbitrary seeks.
            float directionScale = Mathf.Max(Mathf.Abs(forward.x),
                Mathf.Max(Mathf.Abs(forward.y), Mathf.Abs(forward.z)));
            if (directionScale < 0.000001f)
                forward = Vector3.forward;
            else
            {
                // Scale first so normalization also remains finite for large world coordinates.
                forward /= directionScale;
                forward.Normalize();
            }

            Vector3 up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.999f
                ? Vector3.forward : Vector3.up;
            Quaternion rotation = Quaternion.LookRotation(forward, up)
                * Quaternion.AngleAxis(roll, Vector3.forward);
            if (!IsFinite(rotation))
                return false;

            state = new AnimationCameraState
            {
                position = position,
                rotation = rotation,
                // Authored keys are bounded; a custom curve can overshoot between those keys.
                fieldOfView = Mathf.Clamp(fieldOfView, 1f, 179f)
            };
            return true;
        }

        private static bool TryToWorld(CameraVectorKey key, Vector3 value, CameraTrackSpace trackSpace,
            bool isLookAt, in CameraSampleContext context, out Vector3 world)
        {
            world = value;
            if (!CameraTimelineAsset.IsFinite(value))
                return false;

            if (key.reference == CameraVectorReference.Target)
            {
                if (!context.TryGetTarget(key.targetId, out CameraTargetPose target) || !target.IsValid)
                    return false;

                Vector3 offset = value * target.offsetScale;
                if (key.targetOffsetSpace == CameraTargetOffsetSpace.Target)
                    offset = target.rotation * offset;
                else if (key.targetOffsetSpace != CameraTargetOffsetSpace.World)
                    return false;
                world = target.position + offset;
            }
            else if (key.reference == CameraVectorReference.Track)
            {
                if (trackSpace == CameraTrackSpace.Anchor)
                {
                    bool useLookAt = isLookAt && context.HasLookAt;
                    if (!useLookAt && !context.HasAnchor)
                        return false;
                    CameraTargetPose reference = useLookAt ? context.LookAt : context.Anchor;
                    if (!reference.IsValid)
                        return false;
                    // Legacy Anchor applies rotation and translation, never offsetScale or Transform scale.
                    world = reference.position + reference.rotation * value;
                }
                else if (trackSpace != CameraTrackSpace.World)
                    return false;
            }
            else if (key.reference != CameraVectorReference.World)
                return false;

            return CameraTimelineAsset.IsFinite(world);
        }

        private static bool TrySampleVector(List<CameraVectorKey> keys, float time, CameraTrackSpace space,
            bool isLookAt, in CameraSampleContext context, out Vector3 value)
        {
            value = default;
            int index = FindKey(keys, time);
            if (index < 0)
                return false;

            CameraVectorKey current = keys[index];
            if (!TryToWorld(current, current.value, space, isLookAt, context, out value))
                return false;
            if (index == keys.Count - 1 || time <= current.time)
                return true;

            CameraVectorKey next = keys[index + 1];
            if (next == null || !CameraTimelineAsset.IsFinite(next.time) || next.time <= current.time
                || !CameraTimelineAsset.IsFinite(next.value))
                return false;
            if (next.interpolation == CameraKeyInterpolation.Hold)
                return true;
            if (!TryInterpolate(next.interpolation, next.curve, next.easing,
                (time - current.time) / (next.time - current.time), out float t))
                return false;

            if (!TryToWorld(next, next.value, space, isLookAt, context, out Vector3 end))
                return false;

            Vector3[] points = next.bezierControlPoints;
            int count = points == null ? 0 : points.Length;
            if (count > CameraTimelineAsset.MaxBezierControlPoints)
                return false;

            if (count == 0)
                value = Vector3.LerpUnclamped(value, end, t);
            else if (!TryEvaluateBezier(value, end, next, space, isLookAt, context, t, out value))
                return false;
            return CameraTimelineAsset.IsFinite(value);
        }

        private static bool TrySampleFloat(List<CameraFloatKey> keys, float time, float fallback,
            out float value)
        {
            value = fallback;
            if (keys == null || keys.Count == 0)
                return true;

            int index = FindKey(keys, time);
            if (index < 0)
                return false;

            CameraFloatKey current = keys[index];
            value = current.value;
            if (!CameraTimelineAsset.IsFinite(value))
                return false;
            if (index == keys.Count - 1 || time <= current.time)
                return true;

            CameraFloatKey next = keys[index + 1];
            if (next == null || !CameraTimelineAsset.IsFinite(next.time) || next.time <= current.time
                || !CameraTimelineAsset.IsFinite(next.value))
                return false;
            if (next.interpolation == CameraKeyInterpolation.Hold)
                return true;
            if (!TryInterpolate(next.interpolation, next.curve, next.easing,
                (time - current.time) / (next.time - current.time), out float t))
                return false;

            value = Mathf.LerpUnclamped(value, next.value, t);
            return CameraTimelineAsset.IsFinite(value);
        }

        private static bool TryInterpolate(CameraKeyInterpolation mode, AnimationCurve curve,
            CameraEaseType easing, float time, out float t)
        {
            t = Mathf.Clamp01(time);
            switch (mode)
            {
                case CameraKeyInterpolation.Linear:
                    break;
                case CameraKeyInterpolation.Curve:
                    if (curve == null || curve.length == 0)
                        return false;
                    t = curve.Evaluate(t);
                    break;
                case CameraKeyInterpolation.EaseInOut:
                    t = t * t * (3f - 2f * t);
                    break;
                case CameraKeyInterpolation.Preset:
                    return CameraEasing.TryEvaluate(easing, t, out t);
                default:
                    return false;
            }

            return CameraTimelineAsset.IsFinite(t);
        }

        private static bool TryEvaluateBezier(Vector3 start, Vector3 end, CameraVectorKey destination,
            CameraTrackSpace space, bool isLookAt, in CameraSampleContext context, float t,
            out Vector3 value)
        {
            value = default;
            int count = destination.bezierControlPoints.Length;
            // Invocation-local storage is allocation-free and safe for nested/independent evaluations.
            Span<Vector3> work = stackalloc Vector3[CameraTimelineAsset.MaxBezierControlPoints + 2];
            work[0] = start;
            work[count + 1] = end;
            for (int i = 0; i < count; i++)
            {
                if (!TryToWorld(destination, destination.value + destination.bezierControlPoints[i],
                    space, isLookAt, context, out Vector3 point))
                    return false;
                work[i + 1] = point;
            }

            for (int remaining = count + 1; remaining > 0; remaining--)
                for (int i = 0; i < remaining; i++)
                    work[i] = Vector3.LerpUnclamped(work[i], work[i + 1], t);

            value = work[0];
            return CameraTimelineAsset.IsFinite(value);
        }

        private static int FindKey(List<CameraVectorKey> keys, float time)
        {
            if (keys == null || keys.Count == 0)
                return -1;

            int low = 0;
            int high = keys.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                CameraVectorKey key = keys[middle];
                if (key == null || !CameraTimelineAsset.IsFinite(key.time))
                    return -1;
                if (key.time <= time)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            int index = Mathf.Max(0, high);
            return keys[index] == null ? -1 : index;
        }

        private static int FindKey(List<CameraFloatKey> keys, float time)
        {
            if (keys == null || keys.Count == 0)
                return -1;

            int low = 0;
            int high = keys.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                CameraFloatKey key = keys[middle];
                if (key == null || !CameraTimelineAsset.IsFinite(key.time))
                    return -1;
                if (key.time <= time)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            int index = Mathf.Max(0, high);
            return keys[index] == null ? -1 : index;
        }

        private static bool IsFinite(Quaternion value)
        {
            return CameraTimelineAsset.IsFinite(value.x) && CameraTimelineAsset.IsFinite(value.y)
                && CameraTimelineAsset.IsFinite(value.z) && CameraTimelineAsset.IsFinite(value.w);
        }
    }
}
