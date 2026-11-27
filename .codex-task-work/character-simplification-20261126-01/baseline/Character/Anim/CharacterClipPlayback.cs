using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ThreeC.CharacterBehavior
{
    /// <summary>直接片段播放端。剪辑以独立时钟混合，IK 和基础动画在同一次手动求值中输出到 Animator。</summary>
    internal sealed class CharacterClipPlayback
    {
        private readonly Animator animator;
        private readonly CharacterMotionDatabase database;
        private readonly Dictionary<int, CharacterClipState> states = new Dictionary<int, CharacterClipState>();
        private readonly Dictionary<AnimationClip, CharacterClipCurves> curves = new Dictionary<AnimationClip, CharacterClipCurves>();
        private readonly Dictionary<int, GroundPlaybackPace> paces = new Dictionary<int, GroundPlaybackPace>();
        private readonly Dictionary<int, CharacterMotionTrack> groundTracks = new Dictionary<int, CharacterMotionTrack>();
        private readonly CharacterClipLibrary library;
        private readonly string libraryError;
        private bool reuseMixers;
        private PlayableGraph graph;
        private AnimationPlayableOutput output;
        private CharacterBodyPlaybackGraph body;
        private float delta, clockScale = 1f;

        internal bool Available => ValidateBinding() == null;
        internal bool ReuseMixers => reuseMixers;
        public PlayableGraph Graph => graph;
        public Vector3 InertiaAllowance => body?.InertiaAllowance() ?? Vector3.one;

        // 返回阻止 Animator 使用双层片段图的原因。
        internal string ValidateBinding()
        {
            if (libraryError != null) return libraryError;
            if (!database) return "动画配置的播放库已经丢失。";
            if (!animator) return "缺少用于双层片段播放的 Animator。";
            if (animator.runtimeAnimatorController) return "双层片段播放需要将 Animator 的 Controller 留空。";
            if (animator.avatar != database.BakedAvatar) return "Animator 的 Avatar 与角色动画库的烘焙 Avatar 不一致。";
            return null;
        }

        // 建立烘焙状态和素材曲线的按键索引。
        public CharacterClipPlayback(Animator animator, CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            database = config.PlaybackLibrary;
            library = database ? database.Playback : null;
            libraryError = database ? database.ValidatePlayback() : "动画配置缺少由基础动画数据组烘焙的播放库。";
            if (libraryError != null) return;
            foreach (var state in library.States)
            {
                if (state == null || !state.Clip || string.IsNullOrWhiteSpace(state.Path))
                {
                    libraryError = "动画播放库包含缺少片段或状态路径的记录，请重新烘焙基础动画数据组。";
                    states.Clear();
                    return;
                }
                int hash = Animator.StringToHash(state.Path);
                if (hash == 0 || states.ContainsKey(hash))
                {
                    libraryError = $"动画播放库包含无法区分的状态路径 {state.Path}，请检查基础动画数据组并重新烘焙。";
                    states.Clear();
                    return;
                }
                states.Add(hash, state);
            }
            foreach (var clip in library.Clips)
                if (clip != null && clip.Clip) curves[clip.Clip] = clip;
            ConfigurePaces(config);
        }

        // 根据步态配置和烘焙采样确定用于步频和制动调速的状态。
        internal void ConfigurePaces(CharacterAnimationConfiguration config)
        {
            reuseMixers = config.ReuseActionMixers;
            paces.Clear();
            groundTracks.Clear();
            foreach (var pair in states)
                paces[pair.Key] = new GroundPlaybackPace(default, default, 0f, pair.Value.Speed, null);
            SetPace(config.WalkState, GroundMotionUse.Cycle, GroundTrackGait.Walk, config.WalkSpeed);
            SetPace(config.WalkStartState, GroundMotionUse.Start, GroundTrackGait.Walk, config.WalkSpeed);
            SetPace(config.MoveState, GroundMotionUse.Cycle, GroundTrackGait.Run, config.RunSpeed);
            SetPace(config.RunStartState, GroundMotionUse.Start, GroundTrackGait.Run, config.RunSpeed);
            SetPace(config.SprintState, GroundMotionUse.Cycle, GroundTrackGait.Sprint, config.SprintSpeed);
            SetPace(config.DashState, default, default, config.DashClipSpeed);
            foreach (var stop in new[] { config.WalkStop, config.RunStop, config.SprintStop })
                if (stop != null)
                {
                    SetPace(stop.LeftSupport, GroundMotionUse.Stop, default, 0f);
                    SetPace(stop.RightSupport, GroundMotionUse.Stop, default, 0f);
                }
            if (!database || database.BakedTracks == null) return;
            foreach (var track in database.BakedTracks)
            {
                if (track == null || track.States == null) continue;
                foreach (string path in track.States)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    int hash = Animator.StringToHash(path);
                    if (!MatchesTrack(hash, track)) continue;
                    float nominal = track.IsLoop && !track.FixedCycleSpeed && track.ScalesWithSpeed ? track.ReferenceSpeed : 0f;
                    SetPace(path, track.Use, track.Gait, nominal);
                    if (track.ReadyForSearch) groundTracks[hash] = track;
                }
            }

            // 用实际播放状态的基础倍率建立可在规划线程只读的逻辑时钟规则。
            void SetPace(string path, GroundMotionUse use, GroundTrackGait gait, float nominal)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                int hash = Animator.StringToHash(path);
                if (states.TryGetValue(hash, out var state))
                    paces[hash] = new GroundPlaybackPace(use, gait, nominal, state.Speed, config.PaceLimits);
            }
        }

        // 读取与实际播放状态一致的逻辑倍率策略。
        internal GroundPlaybackPace Pace(int hash) => paces.TryGetValue(hash, out var pace) ? pace : default;

        // 根据实际基础声部和全身、下身覆盖读取可校正的参考方向。
        internal Vector2 CycleDirection(float nearSeconds, out float coverage)
        {
            coverage = 0f;
            if (body == null) return default;
            var direction = body.Source(CharacterAnimationSource.Baseline).CycleDirection(groundTracks, delta, nearSeconds * clockScale, out coverage);
            coverage *= (1f - body.Weight(CharacterAnimationSource.LowerBody)) * (1f - body.Weight(CharacterAnimationSource.FullBodyOverlay));
            return direction;
        }

        // 依据基础步态实际剩余的双腿份额，对素材增量和可比较的真实运动使用同一权重。
        internal GroundPathKey ReferenceStep(out float coverage, out GroundMotionUse use)
        {
            coverage = 0f;
            use = default;
            if (body == null) return default;
            var step = body.Source(CharacterAnimationSource.Baseline).ReferenceStep(groundTracks, out coverage, out use);
            float visible = (1f - body.Weight(CharacterAnimationSource.LowerBody)) * (1f - body.Weight(CharacterAnimationSource.FullBodyOverlay));
            step.Position *= visible;
            step.Yaw *= visible;
            coverage *= visible;
            return step;
        }

        // 读取本次真实播放切换在各遮罩部位上的可见贡献。
        internal CharacterTransitionSignal Transition => body?.Transition ?? default;

        // 状态路径只寻址片段，身体范围由动作申请决定。
        public bool HasState(int hash) => states.ContainsKey(hash);

        public bool CanPlayOnce(int hash)
        {
            var clip = StateClip(hash);
            if (!clip) return false;
            var state = states[hash];
            return !(state.Blend.Second ? state.Blend.Loop : clip.isLooping) && StateSeconds(hash) > 0f && state.Speed > 0f;
        }

        // 在 Animator 上连接下身、上身和共用的全身接管输出。
        public void Open()
        {
            if (graph.IsValid() || !Available) return;
            try
            {
                graph = PlayableGraph.Create("角色片段播放");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                body = new CharacterBodyPlaybackGraph(graph, library.Layers[0], library.Layers[1]);
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

        // 丢弃瞬移和外控前的片段与时间差，后续从新基线建声部。
        internal void ClearMotionSources()
        {
            body?.ClearMotionSources();
            delta = 0f;
        }

        // 销毁本角色的播放节点并清除本帧动画时间。
        public void Close()
        {
            if (graph.IsValid())
            {
                using (CharacterClipMixer.DestroyNode.Auto()) graph.Destroy();
            }
            body = null;
            delta = 0f;
        }

        // 将最终 IK 接入片段图，并接管 Animator 的姿态输出。
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

        // 移除 IK 节点并恢复由身体混合器直接输出姿势。
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
        internal CharacterDebugClipMix DebugMix(CharacterAnimationSource source) =>
            body?.Source(source)?.ReadDebugMix() ?? default;
#endif

        // 核对播放状态和烘焙地面轨道是否引用相同素材和混合配方。
        public bool MatchesTrack(int state, CharacterMotionTrack track) =>
            states.TryGetValue(state, out var entry) && entry.MatchesTrack(track, entry.Clip);

        // 读取状态的主片段，供姿态窗口定位正在播放的素材。
        public AnimationClip StateClip(int state) => states.TryGetValue(state, out var entry) ? entry.Clip : null;

        public float? StateSeconds(int state)
        {
            var clip = StateClip(state);
            if (!clip) return null;
            var blend = states[state].Blend;
            return blend.Second ? blend.Seconds : clip.length;
        }

        public float LayerWeight(CharacterAnimationSource source) => body?.Weight(source) ?? 0f;
        // 采样部位混合器已经实际提交的播放状态。
        public CharacterLayerObservation Observe(CharacterAnimationSource source) =>
            body?.Source(source)?.Observe() ?? default;

        internal CharacterFootPlantFrame ReadFootPlant(in CharacterAnimationLayerPlan layer) =>
            body?.Source(layer.Demand.Source)?.ReadFootPlant(layer, delta) ?? new CharacterFootPlantFrame(layer, layer.Start.Phase, true);

        public float LayerBlendDelta(float dt)
        {
            float step = animator.updateMode == AnimatorUpdateMode.UnscaledTime ? Time.unscaledDeltaTime : dt;
            return Mathf.Max(0f, step * animator.speed);
        }

        // 推进各部位混合及全身动作的接管，记录实际创建的播放。
        public CharacterAnimationPlaybackEmission Apply(in CharacterAnimationPlaybackPlan plan, float dt)
        {
            Open();
            delta = LayerBlendDelta(dt);
            clockScale = dt > 0f ? delta / dt : 0f;
            return body?.Apply(this, plan, delta) ?? default;
        }

        // 按入点、淡入、步频和制动计划推进一个片段混合源。
        internal bool Advance(CharacterClipMixer mixer, in CharacterAnimationLayerPlan plan,
            in CharacterAnimationPlaybackPlan frame, float dt, bool created = false)
        {
            mixer.ClockPhase = plan.Demand.Timing.ClockPhase;
            bool played = false;
            if (plan.HasState)
            {
                mixer.ExitProfile = plan.ExitProfile;
                bool entered = plan.Demand.HasHandoff && plan.Demand.Timing.SamplePhase >= plan.Demand.Timing.Handoff.FullFrom;
                if (plan.Command != CharacterAnimationPlaybackCommand.Keep || created || mixer.Empty)
                {
                    var state = states[plan.StateHash];
                    var clip = StateClip(plan.StateHash);
                    float duration = !entered && plan.Command == CharacterAnimationPlaybackCommand.CrossFade ?
                        plan.Blend.InSeconds(mixer.BlendedLength()) : 0f;
                    curves.TryGetValue(clip, out var permissions);
                    CharacterClipCurves secondary = null;
                    if (state.Blend.Second) curves.TryGetValue(state.Blend.Second, out secondary);
                    mixer.EntryProfile = plan.EntryProfile;
                    mixer.Enter(state, clip, permissions, secondary, plan.StateHash,
                        plan.Start.InSeconds(state.Blend.Second ? state.Blend.Seconds : clip.length), duration, plan.ResumeLoop, plan.Demand.Timing.Resumed, plan.Demand);
                    played = true;
                }
                else if (entered) mixer.CompleteTransition();
                mixer.SetInertiaSuppression(plan.Demand.Rule.InertiaSuppression);
            }
            mixer.Advance(dt, this, frame);
            return played;
        }

        public CharacterCurveSample Permission(CharacterIkLimb limb, string curve, string action = null, long? instance = null)
        {
            var part = limb switch
            {
                CharacterIkLimb.LeftFoot => AvatarMaskBodyPart.LeftLeg,
                CharacterIkLimb.RightFoot => AvatarMaskBodyPart.RightLeg,
                CharacterIkLimb.LeftHand => AvatarMaskBodyPart.LeftArm,
                _ => AvatarMaskBodyPart.RightArm
            };
            return body?.Permission(part, curve, delta, action, instance) ?? default;
        }

        public void Evaluate()
        {
            if (!graph.IsValid()) return;
            graph.Evaluate(delta);
        }
    }
}
