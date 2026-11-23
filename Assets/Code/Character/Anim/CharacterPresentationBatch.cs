using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ThreeC.CharacterBehavior
{
    internal readonly struct CharacterPresentationInput
    {
        internal readonly CharacterControllerFrameResult Frame;
        internal readonly CharacterAnimationMotion Motion;
        internal readonly CharacterMovementForecast Forecast;
        internal readonly CharacterPlaybackObservation Observation;
        internal readonly CharacterFootObservation Feet;
        internal readonly double Time;
        internal readonly float DeltaTime, FixedRemainder;
        internal readonly bool ValidGrant;
        internal readonly long GuardGrant;

        internal CharacterPresentationInput(CharacterControllerFrameResult frame, CharacterAnimationMotion motion,
            CharacterMovementForecast forecast, CharacterPlaybackObservation observation, CharacterFootObservation feet,
            float dt, double now, bool validGrant, long grant)
        {
            Frame = frame;
            Motion = motion;
            Forecast = forecast;
            Observation = observation;
            Feet = feet;
            DeltaTime = dt;
            Time = now;
            FixedRemainder = Mathf.Clamp((float)(now - UnityEngine.Time.fixedTimeAsDouble), 0f, UnityEngine.Time.fixedDeltaTime);
            ValidGrant = validGrant;
            GuardGrant = grant;
        }
    }

    /// <summary>角色 Update 发布私有快照，所有 Update 结束后、引擎动画求值前统一提交。</summary>
    internal static class CharacterPresentationBatch
    {
        internal static readonly ProfilerMarker Planning = new ProfilerMarker("3C.Animation.Plan");
        internal static readonly ProfilerMarker Capture = new ProfilerMarker("3C.Animation.Capture");
        internal static readonly ProfilerMarker Wait = new ProfilerMarker("3C.Animation.Wait");
        private static readonly ProfilerMarker submit = new ProfilerMarker("3C.Animation.Submit");
        private static readonly List<CharacterBehaviorAnimator> pending = new List<CharacterBehaviorAnimator>();
        private static bool installed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            pending.Clear();
            installed = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            installed = Insert(ref loop);
            if (installed) PlayerLoop.SetPlayerLoop(loop);
        }

        internal static void Queue(CharacterBehaviorAnimator actor)
        {
            if (installed) pending.Add(actor);
            else actor.FinishPresentation();
        }

        private static bool Insert(ref PlayerLoopSystem parent)
        {
            var children = parent.subSystemList;
            if (children == null) return false;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].type == typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate))
                {
                    if (i + 1 < children.Length && children[i + 1].type == typeof(CharacterPresentationBatch)) return true;
                    var expanded = new PlayerLoopSystem[children.Length + 1];
                    Array.Copy(children, expanded, i + 1);
                    expanded[i + 1] = new PlayerLoopSystem {
                        type = typeof(CharacterPresentationBatch), updateDelegate = Finish
                    };
                    Array.Copy(children, i + 1, expanded, i + 2, children.Length - i - 1);
                    parent.subSystemList = expanded;
                    return true;
                }
                if (Insert(ref children[i])) return true;
            }
            return false;
        }

        private static void Finish()
        {
            using var sample = submit.Auto();
            CharacterForecastRetirement.Collect();
            try
            {
                foreach (var actor in pending)
                {
                    if (!actor || !actor.isActiveAndEnabled) continue;
                    try { actor.FinishPresentation(); }
                    catch (Exception error) { Debug.LogException(error, actor); }
                }
            }
            finally { pending.Clear(); }
        }
    }
}
