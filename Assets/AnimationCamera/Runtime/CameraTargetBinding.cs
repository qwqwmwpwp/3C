using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>向量关键帧的参考来源，可沿用轨道坐标系、直接使用世界坐标或绑定命名目标。</summary>
    public enum CameraVectorReference
    {
        Track = 0,
        World = 1,
        Target = 2
    }

    /// <summary>目标偏移使用的轴向，可沿世界轴或随目标旋转。</summary>
    public enum CameraTargetOffsetSpace
    {
        World = 0,
        Target = 1
    }

    /// <summary>将时间轴中的稳定 ID 绑定到场景节点，并提供独立的偏移缩放倍率。</summary>
    [Serializable]
    public sealed class CameraTargetBinding
    {
        [Tooltip("时间轴中 Target 关键帧使用的稳定 ID，区分大小写，须在绑定列表中唯一。")]
        public string id;
        [Tooltip("此 ID 对应的场景目标节点，求值时读取其世界位置和旋转。")]
        public Transform target;
        [Tooltip("关键帧及贝塞尔控制点偏移的显式倍率，必须为有限正数，独立于 Transform 缩放。")]
        [Min(0.0001f)] public float offsetScale = 1f;
    }

    /// <summary>目标的位置、旋转和显式偏移倍率快照，坐标换算使用独立的偏移倍率。</summary>
    public readonly struct CameraTargetPose
    {
        public readonly Vector3 position;
        public readonly Quaternion rotation;
        public readonly float offsetScale;

        /// <summary>原样保存输入值，调用方须提供单位旋转四元数及有限正数偏移倍率。</summary>
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

                // 四元数旋转向量需要单位四元数，使用双精度计算模长平方，
                // 避免有限浮点分量在有效性检查中平方溢出。
                double x = rotation.x;
                double y = rotation.y;
                double z = rotation.z;
                double w = rotation.w;
                double normSquared = x * x + y * y + z * z + w * w;
                return Math.Abs(normSquared - 1.0) <= 0.0001;
            }
        }

        /// <summary>读取目标世界位置与旋转，目标存在且姿态与偏移倍率有效时返回 true。</summary>
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

    /// <summary>一个命名目标的值快照，由采样上下文按区分大小写的 ID 查找。</summary>
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
    /// 单次镜头求值使用的快照上下文，样本数组由调用方拥有。
    /// 求值期间数组须保持稳定，需留存上下文时应使用独立数组。
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
            // 注视参考缺失时回退到锚点，对外部提供的上下文也采用同一规则。
            HasLookAt = lookAt.HasValue || anchor.HasValue;
            LookAt = lookAt ?? anchor.GetValueOrDefault();
            this.targets = targets;
        }

        /// <summary>仅当区分大小写的 ID 唯一存在时返回成功，调用方仍须检查姿态的 IsValid。</summary>
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
        /// 每个不同的 Transform 仅采集一次，并复用调用方持有的样本缓冲区。
        /// 保留无效目标姿态，由求值器在实际引用时判定失败。
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
                    error = $"目标绑定 {i} 需要非空 ID。";
                    return false;
                }

                for (int previous = 0; previous < i; previous++)
                {
                    if (!string.Equals(bindings[previous].id, binding.id, StringComparison.Ordinal))
                        continue;

                    error = $"目标绑定 ID '{binding.id}' 重复。";
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
