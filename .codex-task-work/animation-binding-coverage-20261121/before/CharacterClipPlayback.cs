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
        private RuntimeAnimatorController detached;
        private PlayableGraph graph;
        private AnimationPlayableOutput output;
        private AnimationLayerMixerPlayable layers;
        private CharacterClipMixer[] mixers;
        private float delta;

        public RuntimeAnimatorController Controller => animator && animator.runtimeAnimatorController
            ? animator.runtimeAnimatorController : detached;
        public bool Available => animator && database && library != null && library.Ready && database.BakedController &&
            Controller == database.BakedController;
        public PlayableGraph Graph => graph;
        public bool ControllerReassigned => graph.IsValid() && animator && animator.runtimeAnimatorController;
        public int LayerCount => library.Layers.Length;

        public CharacterClipPlayback(Animator animator, CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            this.config = config;
            database = config.PlaybackLibrary;
            library = database ? database.Playback : null;
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

        public bool HasState(int layer, int hash) => states.TryGetValue(hash, out var state) && state.Layer == layer;

        public bool CanPlayOnce(int hash) => states.TryGetValue(hash, out var state) &&
            state.Clip && !state.Clip.isLooping && state.Clip.length > 0f && state.Speed > 0f;

        public void Open()
        {
            if (graph.IsValid() || !Available) return;
            detached = animator.runtimeAnimatorController;
            try
            {
                animator.runtimeAnimatorController = null; // 此角色仅由下面一个图输出姿态及片段事件。
                graph = PlayableGraph.Create("角色片段播放");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                layers = AnimationLayerMixerPlayable.Create(graph, library.Layers.Length);
                mixers = new CharacterClipMixer[library.Layers.Length];
                for (int i = 0; i < mixers.Length; i++)
                {
                    var layer = library.Layers[i];
                    mixers[i] = new CharacterClipMixer(graph);
                    graph.Connect(mixers[i].Mixer, 0, layers, i);
                    layers.SetInputWeight(i, i == config.FullLayer ? 1f : layer.Weight);
                    if (layer.Mask) layers.SetLayerMaskFromAvatarMask((uint)i, layer.Mask);
                    layers.SetLayerAdditive((uint)i, layer.Additive);
                    if (i != config.ValidOverlayLayer && !string.IsNullOrEmpty(layer.DefaultState))
                        Enter(i, Animator.StringToHash(layer.DefaultState), 0f, 0f);
                }
                output = AnimationPlayableOutput.Create(graph, "角色姿态", animator);
                output.SetSourcePlayable(layers);
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
            mixers = null;
            delta = 0f;
            if (detached && animator && !animator.runtimeAnimatorController) animator.runtimeAnimatorController = detached;
            detached = null;
        }

        public void ConnectIk(AnimationScriptPlayable job)
        {
            job.SetInputCount(1);
            output.SetSourcePlayable(Playable.Null);
            if (!graph.Connect(layers, 0, job, 0))
            {
                output.SetSourcePlayable(layers);
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
            if (connected || !output.GetSourcePlayable().IsValid()) output.SetSourcePlayable(layers);
            graph.DestroyPlayable(job);
        }

#if UNITY_EDITOR
        internal CharacterDebugClipMix DebugBaseMix => ReadDebugMix(config.FullLayer);
        internal CharacterDebugClipMix DebugOverlayMix => ReadDebugMix(config.ValidOverlayLayer);

        private CharacterDebugClipMix ReadDebugMix(int layer) => mixers != null && layer >= 0 && layer < mixers.Length
            ? mixers[layer].ReadDebugMix() : default;
#endif

        public AnimationClip StateClip(int state) => states.TryGetValue(state, out var entry) ? entry.Clip : null;

        public float? StateSeconds(int state)
        {
            var clip = StateClip(state);
            return clip ? clip.length : (float?)null;
        }

        public float LayerWeight(int layer)
        {
            if (layer < 0) return 0f;
            if (layers.IsValid()) return layer < layers.GetInputCount() ? layers.GetInputWeight(layer) : 0f;
            return animator && animator.isInitialized && layer < animator.layerCount ? animator.GetLayerWeight(layer) : 0f;
        }

        public CharacterLayerObservation Observe(int layer, bool readLengths, bool readNext) => mixers != null && layer >= 0 && layer < mixers.Length
            ? mixers[layer].Observe() : default;

        public CharacterAnimationPlaybackEmission Apply(in CharacterAnimationPlaybackPlan plan, float dt)
        {
            Open();
            float step = animator.updateMode == AnimatorUpdateMode.UnscaledTime ? Time.unscaledDeltaTime : dt;
            delta = Mathf.Max(0f, step * animator.speed);
            bool full = ApplyLayer(config.FullLayer, plan.FullBody, true);
            bool overlay = ApplyLayer(config.ValidOverlayLayer, plan.Overlay, false);
            bool lower = ApplyLayer(config.LowerLayer, plan.LowerBody, false);
            bool upper = ApplyLayer(config.UpperLayer, plan.UpperBody, false);
            foreach (var mixer in mixers) mixer.Advance(delta, plan.Rates.StrideSpeed, plan.StopRate);
            return new CharacterAnimationPlaybackEmission(fullBody: full, lowerBody: lower, upperBody: upper, overlay: overlay);
        }

        private bool ApplyLayer(int layer, in CharacterAnimationLayerPlan plan, bool full)
        {
            if (layer < 0 || layer >= mixers.Length || !full && layer == config.FullLayer) return false;
            mixers[layer].ClockPhase = plan.Demand.Timing.SamplePhase;
            if (!full)
            {
                float target = plan.HasState ? plan.Weight : 0f;
                float weight = plan.LayerBlendSeconds > 0f
                    ? Mathf.MoveTowards(layers.GetInputWeight(layer), target, delta / plan.LayerBlendSeconds) : target;
                layers.SetInputWeight(layer, weight);
            }
            if (!plan.HasState) return false;
            var timing = plan.Demand.Timing;
            bool fullyEntered = !full && timing.Handoff.Valid && timing.SamplePhase >= timing.Handoff.FullFrom;
            if (plan.Command == CharacterAnimationPlaybackCommand.Keep)
            {
                if (fullyEntered) mixers[layer].CompleteTransition();
                return false;
            }
            var state = states[plan.StateHash];
            float start = plan.Start.InSeconds(state.Clip.length);
            float duration = 0f;
            if (plan.Command == CharacterAnimationPlaybackCommand.CrossFade)
                duration = plan.Blend.IsSeconds ? plan.Blend.Value : plan.Blend.InSeconds(mixers[layer].BlendedLength());
            return Enter(layer, plan.StateHash, start, fullyEntered ? 0f : duration, plan.ResumeLoop, timing.Resumed);
        }

        private bool Enter(int layer, int hash, float start, float duration, bool resumeLoop = false, bool resetEvents = false)
        {
            if (!states.TryGetValue(hash, out var state) || state.Layer != layer) return false;
            nominalSpeeds.TryGetValue(hash, out float speed);
            curves.TryGetValue(state.Clip, out var permissions);
            mixers[layer].Enter(state, permissions, hash, speed, stops.Contains(hash), start, duration, resumeLoop, resetEvents);
            return true;
        }

        public CharacterCurveSample Permission(CharacterIkLimb limb, string curve)
        {
            if (mixers == null || config.FullLayer < 0 || config.FullLayer >= mixers.Length) return default;
            var sample = mixers[config.FullLayer].Permission(curve, delta);
            var part = limb switch
            {
                CharacterIkLimb.LeftFoot => AvatarMaskBodyPart.LeftLeg,
                CharacterIkLimb.RightFoot => AvatarMaskBodyPart.RightLeg,
                CharacterIkLimb.LeftHand => AvatarMaskBodyPart.LeftArm,
                _ => AvatarMaskBodyPart.RightArm
            };
            for (int i = config.FullLayer + 1; i < mixers.Length; i++)
            {
                float weight = layers.GetInputWeight(i);
                var layer = library.Layers[i];
                if (weight <= 0f || layer.Mask && !layer.Mask.GetHumanoidBodyPartActive(part)) continue;
                var local = mixers[i].Permission(curve, delta);
                sample = layer.Additive
                    ? new CharacterCurveSample(sample.Value + local.Value * weight, sample.Coverage)
                    : new CharacterCurveSample(Mathf.Lerp(sample.Value, local.Value, weight),
                        Mathf.Lerp(sample.Coverage, local.Coverage, weight));
            }
            return sample;
        }

        public void Evaluate()
        {
            if (graph.IsValid()) graph.Evaluate(delta);
        }
    }
}
