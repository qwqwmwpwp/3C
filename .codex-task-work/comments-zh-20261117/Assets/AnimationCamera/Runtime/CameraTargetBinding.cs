using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    public enum CameraVectorReference
    {
        Track = 0,
        World = 1,
        Target = 2
    }

    public enum CameraTargetOffsetSpace
    {
        World = 0,
        Target = 1
    }

    [Serializable]
    public sealed class CameraTargetBinding
    {
        [Tooltip("Stable, case-sensitive ID used by Target keys in timeline assets.")]
        public string id;
        public Transform target;
        [Tooltip("Explicit multiplier for key offsets and Bezier offsets; does not use Transform scale.")]
        [Min(0.0001f)] public float offsetScale = 1f;
    }

    /// <summary>A value snapshot of a target; transform scale is deliberately excluded.</summary>
    public readonly struct CameraTargetPose
    {
        public readonly Vector3 position;
        public readonly Quaternion rotation;
        public readonly float offsetScale;

        /// <summary>Stores values unchanged; callers must provide a unit rotation quaternion.</summary>
        public CameraTargetPose(Vector3 position, Quaternion rotation, float offsetScale)
        {
            this.position = position;
            this.rotation = rotation;
            this.offsetScale = offsetScale;
        }

        public bool IsValid
        {
            get
            {
                if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z)
                    || !IsFinite(rotation.x) || !IsFinite(rotation.y)
                    || !IsFinite(rotation.z) || !IsFinite(rotation.w)
                    || !IsFinite(offsetScale) || offsetScale <= 0f)
                    return false;

                // Quaternion-vector rotation requires a unit quaternion. Double arithmetic keeps
                // squared finite float components from overflowing during this validity check.
                double x = rotation.x;
                double y = rotation.y;
                double z = rotation.z;
                double w = rotation.w;
                double normSquared = x * x + y * y + z * z + w * w;
                return Math.Abs(normSquared - 1.0) <= 0.0001;
            }
        }

        public static bool TryCapture(Transform target, float offsetScale, out CameraTargetPose pose)
        {
            pose = default;
            if (target == null)
                return false;

            pose = new CameraTargetPose(target.position, target.rotation, offsetScale);
            return pose.IsValid;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public readonly struct CameraTargetSample
    {
        public readonly string id;
        public readonly CameraTargetPose pose;

        public CameraTargetSample(string id, CameraTargetPose pose)
        {
            this.id = id;
            this.pose = pose;
        }
    }

    /// <summary>
    /// Values used by one camera evaluation. The caller owns the sample array and must keep it
    /// unchanged while sampling; retain a separate array when keeping a context for later use.
    /// </summary>
    public readonly struct CameraSampleContext
    {
        public bool HasAnchor { get; }
        public bool HasLookAt { get; }
        public CameraTargetPose Anchor { get; }
        public CameraTargetPose LookAt { get; }

        private readonly CameraTargetSample[] targets;

        public CameraSampleContext(CameraTargetPose? anchor, CameraTargetPose? lookAt,
            CameraTargetSample[] targets = null)
        {
            HasAnchor = anchor.HasValue;
            Anchor = anchor.GetValueOrDefault();
            // Match the legacy look-at reference fallback, including externally supplied contexts.
            HasLookAt = lookAt.HasValue || anchor.HasValue;
            LookAt = lookAt ?? anchor.GetValueOrDefault();
            this.targets = targets;
        }

        /// <summary>Returns whether the ID exists exactly once; a found pose can still be invalid.</summary>
        public bool TryGetTarget(string id, out CameraTargetPose pose)
        {
            pose = default;
            if (string.IsNullOrWhiteSpace(id) || targets == null)
                return false;

            bool found = false;
            for (int i = 0; i < targets.Length; i++)
            {
                if (!string.Equals(targets[i].id, id, StringComparison.Ordinal))
                    continue;

                if (found)
                {
                    pose = default;
                    return false;
                }

                pose = targets[i].pose;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Captures each distinct Transform once and reuses a caller-owned sample buffer.
        /// Invalid target poses are retained for the evaluator to reject only when referenced.
        /// </summary>
        public static bool TryCapture(Transform anchor, Transform lookAtTarget,
            IReadOnlyList<CameraTargetBinding> bindings, ref CameraTargetSample[] buffer,
            out CameraSampleContext context, out string error)
        {
            context = default;
            error = null;
            int count = bindings != null ? bindings.Count : 0;
            for (int i = 0; i < count; i++)
            {
                CameraTargetBinding binding = bindings[i];
                if (binding == null || string.IsNullOrWhiteSpace(binding.id))
                {
                    error = $"Target binding {i} requires a non-empty ID.";
                    return false;
                }

                for (int previous = 0; previous < i; previous++)
                {
                    if (!string.Equals(bindings[previous].id, binding.id, StringComparison.Ordinal))
                        continue;

                    error = $"Target binding ID '{binding.id}' is duplicated.";
                    return false;
                }
            }

            if (buffer == null || buffer.Length != count)
                buffer = count == 0 ? Array.Empty<CameraTargetSample>() : new CameraTargetSample[count];

            CameraTargetPose? anchorPose = null;
            CameraTargetPose? lookAtPose = null;
            if (anchor != null)
            {
                CameraTargetPose.TryCapture(anchor, 1f, out CameraTargetPose capturedAnchor);
                anchorPose = capturedAnchor;
            }

            if (lookAtTarget != null)
            {
                if (ReferenceEquals(lookAtTarget, anchor))
                    lookAtPose = anchorPose;
                else
                {
                    CameraTargetPose.TryCapture(lookAtTarget, 1f, out CameraTargetPose capturedLookAt);
                    lookAtPose = capturedLookAt;
                }
            }

            for (int i = 0; i < count; i++)
            {
                CameraTargetBinding binding = bindings[i];
                CameraTargetPose pose = default;
                if (binding.target != null)
                {
                    bool captured = false;
                    if (ReferenceEquals(binding.target, anchor) && anchorPose.HasValue)
                    {
                        pose = anchorPose.Value;
                        captured = true;
                    }
                    else if (ReferenceEquals(binding.target, lookAtTarget) && lookAtPose.HasValue)
                    {
                        pose = lookAtPose.Value;
                        captured = true;
                    }
                    else
                    {
                        for (int previous = 0; previous < i; previous++)
                        {
                            if (!ReferenceEquals(bindings[previous].target, binding.target))
                                continue;

                            pose = buffer[previous].pose;
                            captured = true;
                            break;
                        }
                    }

                    if (!captured)
                        CameraTargetPose.TryCapture(binding.target, binding.offsetScale, out pose);

                    pose = new CameraTargetPose(pose.position, pose.rotation, binding.offsetScale);
                }

                buffer[i] = new CameraTargetSample(binding.id, pose);
            }

            context = new CameraSampleContext(anchorPose, lookAtPose, buffer);
            return true;
        }
    }
}
