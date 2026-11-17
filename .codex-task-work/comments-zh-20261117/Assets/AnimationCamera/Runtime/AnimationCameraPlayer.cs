// Adapted from https://github.com/0xcjy/UmaViewer
// Source commit: 12c77723169259071ce86e2eab9c871de4a0919d, LiveTimelineControl.cs.
// Independent playback/output shell for the adapted camera tracks; no Director, UI,
// character or database dependency. A future manager can consume CurrentState directly.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>Owns camera timeline playback and optionally applies its sampled state.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/Animation Camera Player")]
    public sealed class AnimationCameraPlayer : MonoBehaviour
    {
        public CameraTimelineAsset timeline;
        [Tooltip("Main camera for this test. Disable Apply To Camera when a camera manager consumes CurrentState.")]
        public Camera outputCamera;
        [Tooltip("Origin and rotation for Anchor position tracks. Scale is ignored.")]
        public Transform anchor;
        [Tooltip("Optional separate origin and rotation for Anchor look-at tracks.")]
        public Transform lookAtTarget;
        [Tooltip("Named targets for individual keys. Bind scene nodes here; timeline assets only store IDs.")]
        public List<CameraTargetBinding> targetBindings = new List<CameraTargetBinding>();
        public bool playOnStart = true;
        public bool loop;
        public bool useUnscaledTime = true;
        public bool applyToCamera = true;
        [Min(0f)] public float playbackSpeed = 1f;

        public float CurrentTime { get; private set; }
        public bool IsPlaying { get; private set; }
        public AnimationCameraState CurrentState { get; private set; }
        public bool HasState { get; private set; }
        public event Action Completed;
        /// <summary>Samples dependent character poses before capturing camera target transforms.</summary>
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

        public void Play()
        {
            if (!isActiveAndEnabled || !ValidatePlayback())
                return;

            if (CurrentTime >= timeline.duration)
                CurrentTime = 0f;

            completionRaised = false;
            IsPlaying = Sample(CurrentTime);
        }

        public void Pause()
        {
            IsPlaying = false;
        }

        public void Stop()
        {
            IsPlaying = false;
            CurrentTime = 0f;
            Seek(0f);
        }

        public void Replay()
        {
            IsPlaying = false;
            CurrentTime = 0f;
            Play();
        }

        /// <summary>Seeks without changing the paused/playing state or firing completion.</summary>
        public void Seek(float time)
        {
            if (float.IsNaN(time) || float.IsInfinity(time) || !ValidatePlayback())
                return;

            CurrentTime = Mathf.Clamp(time, 0f, timeline.duration);
            completionRaised = false;
            if (!Sample(CurrentTime))
                IsPlaying = false;
        }

        /// <summary>Samples a pose without advancing playback or modifying a Camera.</summary>
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
                FailPlayback("The timeline changed during playback. Call Play again to validate it.");
                return;
            }

            if (!IsFinite(playbackSpeed) || playbackSpeed < 0f)
            {
                FailPlayback("Playback speed must be finite and nonnegative.");
                return;
            }

            float deltaTime = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            if (!IsFinite(deltaTime) || deltaTime < 0f)
            {
                FailPlayback("The playback delta time became invalid.");
                return;
            }

            // Double arithmetic avoids overflow when a large valid speed crosses several loops.
            double nextTime = CurrentTime + (double)deltaTime * playbackSpeed;
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
                FailPlayback("Assign a camera timeline asset.");
                return false;
            }

            if (!timeline.TryValidate(out string error))
            {
                FailPlayback(error);
                return false;
            }

            if (!IsFinite(playbackSpeed) || playbackSpeed < 0f)
            {
                FailPlayback("Playback speed must be finite and nonnegative.");
                return false;
            }

            if (applyToCamera && outputCamera == null)
            {
                FailPlayback("Assign an output camera, or disable Apply To Camera for managed output.");
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
                FailPlayback("Cannot sample this timeline. Check its keys, Anchor references and named target bindings.");
                return false;
            }

            CurrentState = state;
            HasState = true;
            if (applyToCamera && isActiveAndEnabled)
            {
                if (outputCamera == null)
                {
                    FailPlayback("The output camera was removed during playback.");
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
