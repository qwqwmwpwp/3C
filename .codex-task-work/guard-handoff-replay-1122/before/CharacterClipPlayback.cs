using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    /// <summary>直接片段播放端。剪辑以独立时钟混合，IK 和基础动画在同一次手动求值中输出到 Animator。</summary>
    internal sealed class CharacterClipPlayback : ICharacterPlaybackBackend
    {
        private readonly Animator animator;
        private readonly CharacterMotionDatabase database;
        private readonly CharacterAnimationConfiguration config;
        private readonly Dictionary<int, CharacterClipState> states = new Dictionary<int, CharacterClipState>();
        private readonly Dictionary<AnimationClip, CharacterClipCurves> curves = new Dictionary<AnimationClip, CharacterClipCurves>();
        private readonly Dictionary<int, float> nominalSpeeds = new Dictionary<int, float>();
        private readonly HashSet<int> stops = new HashSet<int>();
        private readonly CharacterClipLibrary library;
        private readonly bool layoutReady;
        private RuntimeAnimatorController detached;
        private PlayableGraph graph;
        private AnimationPlayableOutput output;
        private CharacterBodyPlaybackGraph body;
        private readonly List<KeyValuePair<AnimationClip, AnimationClip>> overridePairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        private readonly Dictionary<AnimationClip, AnimationClip> replacements = new Dictionary<AnimationClip, AnimationClip>();
        private AnimatorOverrideController boundOverrides;
        private int lastOverrideCheck = -1;
        private float delta;

        public RuntimeAnimatorController Controller => animator && animator.runtimeAnimatorController
            ? animator.runtimeAnimatorController : detached ? detached : ExpectsBodyLibrary ? database.BakedController : null;
        public bool ExpectsBodyLibrary { get; }
        public bool Available => animator && database && library != null && library.Ready && database.BakedController &&
            (Controller == database.BakedController || ExpectsBodyLibrary && Controller is AnimatorOverrideController overrides &&
             overrides.runtimeAnimatorController == database.BakedController) &&
            layoutReady;
        public PlayableGraph Graph => graph;
        public bool ControllerReassigned => graph.IsValid() && animator && animator.runtimeAnimatorController;

        public CharacterClipPlayback(Animator animator, CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            this.config = config;
            database = config.PlaybackLibrary;
            library = database ? database.Playback : null;
            ExpectsBodyLibrary = database && database.UsesBodyOutputs;
            layoutReady = !ExpectsBodyLibrary || library?.HasBodyMasks() == true;
            if (library == null || !library.Ready) return;
            foreach (var state in library.States)
                if (state != null && state.Clip && !string.IsNullOrEmpty(state.Path))
                    states[Animator.StringToHash(state.Path)] = state;
            foreach (var clip in library.Clips)
                if (clip != null && clip.Clip) curves[clip.Clip] = clip;
            SetPace(config.WalkState, config.WalkSpeed);
            SetPace(config.WalkStartState, config.WalkSpeed);
            SetPace(config.MoveState, config.RunSpeed);
            SetPace(config.RunStartState, config.RunSpeed);
            SetPace(config.SprintState, config.SprintSpeed);
            SetPace(config.DashState, config.DashClipSpeed);
            foreach (var stop in new[] { config.WalkStop, config.RunStop, config.SprintStop })
                if (stop != null) { AddStop(stop.LeftSupport); AddStop(stop.RightSupport); }
            if (database.BakedTracks == null) return;
            foreach (var track in database.BakedTracks)
            {
                if (track == null || track.States == null) continue;
                foreach (string path in track.States)
                {
                    if (track.IsStop) AddStop(path);
                    else if (track.IsLoop) SetPace(path, track.ScalesWithSpeed ? track.ReferenceSpeed : 0f);
                }
            }
        }

        private void SetPace(string path, float speed)
        {
            if (!string.IsNullOrWhiteSpace(path)) nominalSpeeds[Animator.StringToHash(path)] = speed;
        }

        private void AddStop(string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) stops.Add(Animator.StringToHash(path));
        }

        public bool HasState(CharacterAnimationBodyRegion source, int hash)
        {
            if (!Available || !states.TryGetValue(hash, out var state)) return false;
            if (ExpectsBodyLibrary) return true; // 状态路径只寻址片段，部位由动作申请决定。
            int layer = config.LegacyLayer(source);
            return layer >= 0 && layer < library.Layers.Length && state.Layer == layer;
        }

        public bool CanPlayOnce(int hash)
        {
            var clip = StateClip(hash);
            return clip && !clip.isLooping && clip.length > 0f && states[hash].Speed > 0f;
        }

        public void BindClips()
        {
            lastOverrideCheck = Time.frameCount;
            replacements.Clear();
            boundOverrides = ExpectsBodyLibrary ? Controller as AnimatorOverrideController : null;
            if (!boundOverrides) return;
            boundOverrides.GetOverrides(overridePairs);
            foreach (var pair in overridePairs)
                if (pair.Value && pair.Value != pair.Key) replacements[pair.Key] = pair.Value;
        }

        public bool OverridesChanged()
        {
            if (!boundOverrides || lastOverrideCheck == Time.frameCount) return false;
            lastOverrideCheck = Time.frameCount;
            boundOverrides.GetOverrides(overridePairs);
            int count = 0;
            foreach (var pair in overridePairs)
            {
                if (!pair.Value || pair.Value == pair.Key) continue;
                if (!replacements.TryGetValue(pair.Key, out var clip) || clip != pair.Value) return true;
                count++;
            }
            return count != replacements.Count;
        }

        private CharacterClipLayer BodyLayer(int index) => index >= 0 && index < library.Layers.Length ? library.Layers[index] : null;

        public void Open()
        {
            if (graph.IsValid() || !Available) return;
            detached = animator.runtimeAnimatorController;
            try
            {
                animator.runtimeAnimatorController = null; // 此角色仅由下面一个图输出姿态及片段事件。
                graph = PlayableGraph.Create("角色片段播放");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                body = new CharacterBodyPlaybackGraph(graph, BodyLayer(ExpectsBodyLibrary ? 0 : config.LowerLayer),
                    BodyLayer(ExpectsBodyLibrary ? 1 : config.UpperLayer));
                output = AnimationPlayableOutput.Create(graph, "角色双层姿势", animator);
                output.SetSourcePlayable(body.Root);
                graph.Play();
            }
            catch
            {
                Close();
                throw;
            }
        }

        public void Close()
        {
            if (graph.IsValid()) graph.Destroy();
            body = null;
            delta = 0f;
            if (detached && animator && !animator.runtimeAnimatorController) animator.runtimeAnimatorController = detached;
            detached = null;
        }

        public void ConnectIk(AnimationScriptPlayable job)
        {
            job.SetInputCount(1);
            output.SetSourcePlayable(Playable.Null);
            if (!graph.Connect(body.Root, 0, job, 0))
            {
                output.SetSourcePlayable(body.Root);
                throw new InvalidOperationException("无法将分层播放输出接入最终 IK。");
            }
            job.SetInputWeight(0, 1f);
            job.SetProcessInputs(true);
            output.SetSourcePlayable(job);
        }

        public void DisconnectIk(AnimationScriptPlayable job)
        {
            if (!job.IsValid() || !graph.IsValid()) return;
            bool connected = output.GetSourcePlayable().Equals((Playable)job);
            if (connected) output.SetSourcePlayable(Playable.Null);
            if (job.GetInputCount() > 0 && job.GetInput(0).IsValid()) graph.Disconnect(job, 0);
            if (connected || !output.GetSourcePlayable().IsValid()) output.SetSourcePlayable(body.Root);
            graph.DestroyPlayable(job);
        }

#if UNITY_EDITOR
        internal CharacterDebugClipMix DebugMix(CharacterAnimationBodyRegion source) =>
            body?.Source(source)?.ReadDebugMix() ?? default;
#endif

        public AnimationClip BakedClip(int state) => states.TryGetValue(state, out var entry) ? entry.Clip : null;
        public AnimationClip StateClip(int state)
        {
            var original = BakedClip(state);
            return original && replacements.TryGetValue(original, out var clip) ? clip : original;
        }

        public float? StateSeconds(int state)
        {
            var clip = StateClip(state);
            return clip ? clip.length : (float?)null;
        }

        public float LayerWeight(CharacterAnimationBodyRegion source) => body?.Weight(source) ?? 0f;
        public CharacterLayerObservation Observe(CharacterAnimationBodyRegion source, bool readLengths, bool readNext) =>
            body?.Source(source)?.Observe() ?? default;

        public float LayerBlendDelta(float dt)
        {
            float step = animator.updateMode == AnimatorUpdateMode.UnscaledTime ? Time.unscaledDeltaTime : dt;
            return Mathf.Max(0f, step * animator.speed);
        }

        public CharacterAnimationPlaybackEmission Apply(in CharacterAnimationPlaybackPlan plan, float dt)
        {
            Open();
            delta = LayerBlendDelta(dt);
            return body.Apply(this, plan, delta);
        }

        internal bool Advance(CharacterClipMixer mixer, in CharacterAnimationLayerPlan plan,
            in CharacterAnimationPlaybackPlan frame, float dt, bool created = false)
        {
            mixer.ClockPhase = plan.Demand.Timing.ClockPhase;
            bool played = false;
            if (plan.HasState)
            {
                bool entered = plan.Demand.HasHandoff && plan.Demand.Timing.SamplePhase >= plan.Demand.Timing.Handoff.FullFrom;
                if (plan.Command != CharacterAnimationPlaybackCommand.Keep || created || mixer.Empty)
                {
                    var state = states[plan.StateHash];
                    var clip = StateClip(plan.StateHash);
                    float duration = !entered && plan.Command == CharacterAnimationPlaybackCommand.CrossFade ?
                        plan.Blend.InSeconds(mixer.BlendedLength()) : 0f;
                    nominalSpeeds.TryGetValue(plan.StateHash, out float pace);
                    curves.TryGetValue(clip, out var permissions);
                    mixer.Enter(state, clip, permissions, plan.StateHash, pace, stops.Contains(plan.StateHash),
                        plan.Start.InSeconds(clip.length), duration, plan.ResumeLoop, plan.Demand.Timing.Resumed);
                    played = true;
                }
                else if (entered) mixer.CompleteTransition();
            }
            mixer.Advance(dt, frame.Rates.StrideSpeed, frame.StopRate);
            return played;
        }

        public CharacterCurveSample Permission(CharacterIkLimb limb, string curve)
        {
            var part = limb switch
            {
                CharacterIkLimb.LeftFoot => AvatarMaskBodyPart.LeftLeg,
                CharacterIkLimb.RightFoot => AvatarMaskBodyPart.RightLeg,
                CharacterIkLimb.LeftHand => AvatarMaskBodyPart.LeftArm,
                _ => AvatarMaskBodyPart.RightArm
            };
            return body?.Permission(part, curve, delta) ?? default;
        }

        public void Evaluate()
        {
            if (graph.IsValid()) graph.Evaluate(delta);
        }
    }
}
