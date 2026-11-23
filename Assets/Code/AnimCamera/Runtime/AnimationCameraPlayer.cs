// 改编自 https://github.com/0xcjy/UmaViewer
// 来源提交 12c77723169259071ce86e2eab9c871de4a0919d，参考 LiveTimelineControl.cs。
// 外部镜头管理器可读取 CurrentState 接管输出。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>管理镜头时间轴播放状态，并按配置将采样结果应用到摄像机。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/镜头时间轴播放器")]
    public sealed class AnimationCameraPlayer : MonoBehaviour
    {
        [Tooltip("要播放的镜头时间轴资产。位置与注视轨道各至少配置一个关键帧。")]
        public CameraTimelineAsset timeline;
        [Tooltip("接收镜头采样结果的摄像机。外部镜头管理器读取 CurrentState 时，关闭 Apply To Camera。")]
        public Camera outputCamera;
        [Tooltip("锚点坐标系位置轨道的原点与旋转，坐标换算使用单位缩放。")]
        public Transform anchor;
        [Tooltip("锚点坐标系注视轨道的独立参考节点，可选。留空时使用 Anchor。")]
        public Transform lookAtTarget;
        [Tooltip("关键帧引用的命名目标。在此绑定场景节点，时间轴资产通过区分大小写的 ID 引用。")]
        public List<CameraTargetBinding> targetBindings = new List<CameraTargetBinding>();
        [Tooltip("组件首次进入 Start 时自动开始播放。")]
        public bool playOnStart = true;
        [Tooltip("到达时间轴末尾后循环播放，循环期间持续推进并采样。")]
        public bool loop;
        [Tooltip("使用未缩放时间，使播放进度独立于 Time.timeScale。")]
        public bool useUnscaledTime = true;
        [Tooltip("将采样的位置、旋转和视野角写入 Output Camera。关闭后可由外部系统读取 CurrentState。")]
        public bool applyToCamera = true;
        [Min(0f), Tooltip("播放速率倍率，须为有限非负数。1 为正常速度，0 时保持当前时间。")]
        public float playbackSpeed = 1f;

        public float CurrentTime { get; private set; }
        public bool IsPlaying { get; private set; }
        public AnimationCameraState CurrentState { get; private set; }
        public bool HasState { get; private set; }
        /// <summary>非循环播放到达末尾时触发一次，订阅方应在停用或销毁时解除订阅。</summary>
        public event Action Completed;
        /// <summary>在采集镜头目标变换前通知依赖方采样角色姿态，参数为镜头时间（秒）。订阅方应在停用或销毁时解除订阅。</summary>
        public event Action<float> BeforeSample;

        private CameraTimelineAsset validatedTimeline;
        private float validatedDuration;
        private bool completionRaised;
        private CameraTargetSample[] targetSamples = Array.Empty<CameraTargetSample>();

        private void Reset()
        {
            outputCamera = GetComponent<Camera>();
        }

        private void Awake()
        {
            if (outputCamera == null)
                outputCamera = GetComponent<Camera>();
        }

        private void Start()
        {
            if (playOnStart)
                Play();
        }

        private void OnDisable()
        {
            IsPlaying = false;
        }

        /// <summary>校验配置并从当前时间播放，到达末尾后从零开始。组件须处于启用状态。</summary>
        public void Play()
        {
            if (!isActiveAndEnabled || !ValidatePlayback())
                return;

            if (CurrentTime >= timeline.duration)
                CurrentTime = 0f;

            completionRaised = false;
            IsPlaying = Sample(CurrentTime);
        }

        /// <summary>暂停时间推进，保留当前播放时间与已采样状态。</summary>
        public void Pause()
        {
            IsPlaying = false;
        }

        /// <summary>停止播放并定位到零秒，配置有效时立即采样起始姿态。</summary>
        public void Stop()
        {
            IsPlaying = false;
            CurrentTime = 0f;
            Seek(0f);
        }

        /// <summary>将时间重置为零，再校验配置并开始播放。</summary>
        public void Replay()
        {
            IsPlaying = false;
            CurrentTime = 0f;
            Play();
        }

        /// <summary>定位并采样指定时间（秒），保持当前播放或暂停状态。采样失败时停止播放，定位操作不会触发完成事件。</summary>
        public void Seek(float time)
        {
            if (float.IsNaN(time) || float.IsInfinity(time) || !ValidatePlayback())
                return;

            CurrentTime = Mathf.Clamp(time, 0f, timeline.duration);
            completionRaised = false;
            if (!Sample(CurrentTime))
                IsPlaying = false;
        }

        /// <summary>基于当前场景目标采样指定时间（秒）的镜头姿态。调用方应先更新角色姿态，此入口仅返回采样结果。</summary>
        public bool TryEvaluateAt(float time, out AnimationCameraState state)
        {
            state = default;
            return CameraSampleContext.TryCapture(anchor, lookAtTarget, targetBindings, ref targetSamples,
                    out CameraSampleContext context, out _)
                && CameraTimelineEvaluator.TryEvaluate(timeline, time, context, out state);
        }

        private void LateUpdate()
        {
            if (!IsPlaying)
                return;

            if (timeline == null || timeline != validatedTimeline || timeline.duration != validatedDuration)
            {
                FailPlayback("播放期间时间轴引用或时长发生变化，请再次调用 Play 重新校验。");
                return;
            }

            if (!IsFinite(playbackSpeed) || playbackSpeed < 0f)
            {
                FailPlayback("播放速度必须为有限非负数。");
                return;
            }

            float deltaTime = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            if (!IsFinite(deltaTime) || deltaTime < 0f)
            {
                FailPlayback("本次播放时间步长无效。");
                return;
            }

            double nextTime = CurrentTime + (double)deltaTime * playbackSpeed; // 高速跨越多个循环时，用双精度避免溢出。
            bool finished = nextTime >= timeline.duration;
            CurrentTime = finished && loop
                ? (float)(nextTime % timeline.duration)
                : (float)Math.Min(nextTime, timeline.duration);
            if (!Sample(CurrentTime))
            {
                IsPlaying = false;
                return;
            }

            if (finished && !loop)
            {
                IsPlaying = false;
                if (!completionRaised)
                {
                    completionRaised = true;
                    Completed?.Invoke();
                }
            }
        }

        private bool ValidatePlayback()
        {
            if (timeline == null)
            {
                FailPlayback("请指定镜头时间轴资产。");
                return false;
            }

            if (!timeline.TryValidate(out string error))
            {
                FailPlayback(error);
                return false;
            }

            if (!IsFinite(playbackSpeed) || playbackSpeed < 0f)
            {
                FailPlayback("播放速度必须为有限非负数。");
                return false;
            }

            if (applyToCamera && outputCamera == null)
            {
                FailPlayback("请指定输出摄像机，或关闭 Apply To Camera 以交由外部镜头管理器输出。");
                return false;
            }

            validatedTimeline = timeline;
            validatedDuration = timeline.duration;
            return true;
        }

        private bool Sample(float time)
        {
            BeforeSample?.Invoke(time);
            if (!CameraSampleContext.TryCapture(anchor, lookAtTarget, targetBindings, ref targetSamples,
                out CameraSampleContext context, out string error))
            {
                FailPlayback(error);
                return false;
            }

            if (!CameraTimelineEvaluator.TryEvaluateValidated(timeline, time, context,
                out AnimationCameraState state))
            {
                FailPlayback("时间轴采样失败，请检查关键帧、Anchor 引用及命名目标绑定。");
                return false;
            }

            CurrentState = state;
            HasState = true;
            if (applyToCamera && isActiveAndEnabled)
            {
                if (outputCamera == null)
                {
                    FailPlayback("播放期间输出摄像机已被移除。");
                    return false;
                }

                outputCamera.transform.SetPositionAndRotation(state.position, state.rotation);
                outputCamera.fieldOfView = state.fieldOfView;
            }

            return true;
        }

        private void FailPlayback(string message)
        {
            IsPlaying = false;
            HasState = false;
            Debug.LogError($"[AnimationCameraPlayer] {message}", this);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
