from pathlib import Path
r=Path('Assets/CharacterBehavior/Runtime')
def change(name, pairs):
    p=r/name; raw=p.read_bytes(); s=raw.decode().replace('\r\n','\n')
    for a,b in pairs:
        assert s.count(a)==1,(name,a,s.count(a))
        s=s.replace(a,b)
    if b'\r\n' in raw:s=s.replace('\n','\r\n')
    p.write_bytes(s.encode())
change('CharacterMotionDatabase.cs',[('        public bool Loop;','        public bool Loop;\n        [Tooltip("循环片段始终以素材原速播放；默认按实际速度调整。")] public bool FixedCycleSpeed;'),('        [SerializeField, HideInInspector] internal int Format;', '        [SerializeField, HideInInspector] internal CharacterClipLibrary Playback;\n        [SerializeField, HideInInspector] internal int Format;')])
change('CharacterPlaybackObservation.cs',[
('    internal readonly struct CharacterLayerObservation','''    internal readonly struct CharacterClipProgress
    {
        public readonly int fullPathHash;
        public readonly float normalizedTime;
        public readonly bool loop;

        public CharacterClipProgress(int hash, float progress, bool looping)
        {
            fullPathHash = hash;
            normalizedTime = progress;
            loop = looping;
        }

        public CharacterClipProgress(AnimatorStateInfo state) : this(state.fullPathHash, state.normalizedTime, state.loop) { }
    }

    internal readonly struct CharacterLayerObservation'''),('public readonly AnimatorStateInfo Current, Next;', 'public readonly CharacterClipProgress Current, Next;\n        public readonly float SourceLength, DestinationLength;'),('public AnimatorStateInfo Destination', 'public CharacterClipProgress Destination'),('        public CharacterLayerObservation(Animator animator, int layer)','''        public CharacterLayerObservation(CharacterClipProgress current, CharacterClipProgress next, bool fading, float blend,
            float sourceLength, float destinationLength)
        {
            Current = current;
            Next = next;
            InTransition = fading;
            BlendProgress = blend;
            SourceLength = sourceLength;
            DestinationLength = destinationLength;
        }

        public CharacterLayerObservation(Animator animator, int layer)'''),('Current = animator.GetCurrentAnimatorStateInfo(layer);','Current = new CharacterClipProgress(animator.GetCurrentAnimatorStateInfo(layer));'),('Next = animator.GetNextAnimatorStateInfo(layer);','Next = new CharacterClipProgress(animator.GetNextAnimatorStateInfo(layer));'),('        private readonly List<AnimatorClipInfo>', '        private readonly CharacterClipPlayback playback;\n        private readonly List<AnimatorClipInfo>'),('CharacterPlaybackObserver(Animator animator, CharacterAnimationResourceCatalog resources)','CharacterPlaybackObserver(Animator animator, CharacterAnimationResourceCatalog resources, CharacterClipPlayback playback)'),('            this.resources = resources;','            this.resources = resources;\n            this.playback = playback;'),('        public CharacterPlaybackObservation Capture','''        private CharacterLayerObservation Read(int layer) => playback.Available
            ? playback.Observe(layer) : new CharacterLayerObservation(animator, layer);

        public CharacterPlaybackObservation Capture'''),('if (!animator.isInitialized)', 'if (!playback.Available && !animator.isInitialized)'),('new CharacterLayerObservation(animator, fullLayer)', 'Read(fullLayer)'),('new CharacterLayerObservation(animator, lowerLayer)', 'Read(lowerLayer)'),('new CharacterLayerObservation(animator, upperLayer)', 'Read(upperLayer)'),('float sourceLength = 0f, destinationLength = 0f;', 'float sourceLength = full.SourceLength, destinationLength = full.DestinationLength;'),('if (full.Ready && (readStop || enteringIdle))','if (!playback.Available && full.Ready && (readStop || enteringIdle))')])
change('CharacterAnimationResourceCatalog.cs',[
('        private readonly Animator animator;', '        private readonly CharacterClipPlayback playback;\n        public RuntimeAnimatorController Controller => playback.Controller;\n        public bool HasState(int layer, int hash) => playback.HasState(layer, hash);'),
('public CharacterAnimationResourceCatalog(Animator animator, CharacterAnimationConfiguration config,','public CharacterAnimationResourceCatalog(CharacterClipPlayback playback, CharacterAnimationConfiguration config,'),('            this.animator = animator;','            this.playback = playback;'),('RuntimeAnimatorController controller = animator != null ? animator.runtimeAnimatorController : null;','RuntimeAnimatorController controller = Controller;'),('layer < animator.layerCount','layer < playback.LayerCount'),('animator.HasState(layer, binding.StateHash)','HasState(layer, binding.StateHash)')])
change('CharacterMotionMatcher.cs',[('        private readonly Animator animator;', '        private readonly Animator animator;\n        private readonly CharacterAnimationResourceCatalog resources;'),('            this.animator = animator;','            this.animator = animator;\n            this.resources = resources;'),(' || layer < 0 || layer >= animator.layerCount',''),('animator.runtimeAnimatorController != database.BakedController','resources.Controller != database.BakedController'),('animator.HasState(layer, entry.Binding.StateHash)','resources.HasState(layer, entry.Binding.StateHash)')])
change('CharacterLegacyMotionMatcher.cs',[('animator.runtimeAnimatorController == (database.Format','resources.Controller == (database.Format')])
change('CharacterAnimationPlayer.cs',[
('        private readonly Animator animator;', '        private readonly Animator animator;\n        private readonly CharacterClipPlayback playback;'),
('CharacterAnimationPlayer(Animator animator, int fullLayer, int lowerLayer, int upperLayer)', 'CharacterAnimationPlayer(Animator animator, CharacterClipPlayback playback, int fullLayer, int lowerLayer, int upperLayer)'),('            this.animator = animator;','            this.animator = animator;\n            this.playback = playback;'),('            RefreshParameterCache();','            if (playback.Available) return playback.Apply(plan, deltaTime);\n            RefreshParameterCache();'),('            parameterController = null;', '            playback.Close();\n            parameterController = null;'),('            // 尚未接管','            playback.Close();\n            // 尚未接管'),('        public void Release()\n        {','        public void Release()\n        {\n            playback.Close();')])
change('CharacterLocomotionPoseSelector.cs',[('''            float? speed = null;
            if (full.Binding.NominalSpeed > 0f || idle || full.Binding.IsStop)
            {
                if (motion.IsValid) speed = InputMath.NonNegative(motion.ActualSpeed, 0f);
                else if (full.StateHash == last.FullBody.StateHash) speed = last.Rates.StrideSpeed;
            }''','''            float? speed = motion.IsValid ? InputMath.NonNegative(motion.ActualSpeed, 0f) : last.Rates.StrideSpeed;
            // 即使新入场片段维持原速，尚在淡出的走跑仍需自己的实际步频。'''),('            bool idle = owners.FullBody.Key == CharacterAnimationIntentConverter.IdleKey;\n','')])
change('CharacterAnimationPipeline.cs',[
('        private readonly CharacterAnimationPlayer player;','        private readonly CharacterAnimationPlayer player;\n        private readonly CharacterClipPlayback clips;'),('            resources = new CharacterAnimationResourceCatalog(animator,','            clips = new CharacterClipPlayback(animator, configuration);\n            resources = new CharacterAnimationResourceCatalog(clips,'),('new CharacterPlaybackObserver(animator, resources);','new CharacterPlaybackObserver(animator, resources, clips);'),('new CharacterAnimationPlayer(animator, configuration.FullLayer,','new CharacterAnimationPlayer(animator, clips, configuration.FullLayer,'),('new CharacterAnimationIk(animator, logContext.transform','new CharacterAnimationIk(animator, clips, logContext.transform'),('                    Ik.Invalidate();\n                }','                    Ik.Invalidate();\n                    player.InvalidateController();\n                }'),('            Ik.Prepare(selection, motion, plan); // 目标许可取自基础层实际混合的素材曲线。','            Ik.Prepare(selection, motion, plan); // 目标许可取自基础层实际混合的素材曲线。\n            clips.Evaluate();'),('            player.ResetPlayback();\n            Ik.Reset();','            Ik.Reset();\n            player.ResetPlayback();'),('                player.InvalidateController();\n                Ik.Invalidate();','                Ik.Invalidate();\n                player.InvalidateController();'),('debugController = animator != null ? animator.runtimeAnimatorController : null;','debugController = clips.Controller;'),('debugController == animator.runtimeAnimatorController','debugController == clips.Controller')])
change('CharacterIkInput.cs',[('素材附加曲线及控制器浮点参数的名称，默认参数值应为零。','素材许可曲线名称。零参数播放从烘焙素材曲线读取，缺失时权重为零。')])
change('CharacterIkJob.cs',[('        public float PositionWeight, RotationWeight, BlendSeconds;', '''        public byte ClipDriven;
        public float ClipPermission;
        public float PositionWeight, RotationWeight, BlendSeconds;'''),('Tip.IsValid(stream) && Curve.IsValid(stream);','Tip.IsValid(stream) && (ClipDriven != 0 || Curve.IsValid(stream));'),('float value = Curve.GetFloat(stream);','float value = ClipDriven != 0 ? ClipPermission : Curve.GetFloat(stream);')])
change('CharacterAnimationIk.cs',[
('        private readonly Animator animator;', '        private readonly Animator animator;\n        private readonly CharacterClipPlayback clips;\n        private bool sharedGraph;'),('CharacterAnimationIk(Animator animator, Transform owner,','CharacterAnimationIk(Animator animator, CharacterClipPlayback clips, Transform owner,'),('            this.animator = animator;','            this.animator = animator;\n            this.clips = clips;'),('                if (bindings[i].Valid) inputs[i] = sources[i].Sample(selection, sceneCandidates, ref cursor);','''            {
                var binding = bindings[i];
                if (!binding.Valid) continue;
                if (sharedGraph)
                {
                    binding.ClipPermission = clips.Permission(channels[i].WeightCurve);
                    bindings[i] = binding;
                }
                inputs[i] = sources[i].Sample(selection, sceneCandidates, ref cursor);
            }'''),('                var parameters = animator.parameters;', '                sharedGraph = clips.Available;\n                var parameters = sharedGraph ? null : animator.parameters;'),('                        Curve = animator.BindStreamProperty','                        ClipDriven = (byte)(sharedGraph ? 1 : 0),\n                        Curve = sharedGraph ? default : animator.BindStreamProperty'),('''                graph = PlayableGraph.Create("角色动画 IK");
                graph.SetTimeUpdateMode''','''                graph = sharedGraph ? clips.Graph : PlayableGraph.Create("角色动画 IK");
                if (!sharedGraph) graph.SetTimeUpdateMode'''),('                var output = AnimationPlayableOutput.Create(graph, "最终角色姿态", animator);','''                if (sharedGraph)
                {
                    clips.ConnectIk(playable);
                    return;
                }
                var output = AnimationPlayableOutput.Create(graph, "最终角色姿态", animator);'''),('            if (!string.IsNullOrWhiteSpace(c.WeightCurve))','            if (sharedGraph) return string.IsNullOrWhiteSpace(c.WeightCurve) ? "未配置素材许可曲线" : null;\n            if (!string.IsNullOrWhiteSpace(c.WeightCurve))'),('            if (graph.IsValid()) graph.Destroy();','''            if (sharedGraph) clips.DisconnectIk(playable);
            else if (graph.IsValid()) graph.Destroy();
            sharedGraph = false;
            graph = default;
            playable = default;''')])
p=r/'CharacterAnimationIk.cs';b=p.read_bytes().replace(b'animator.runtimeAnimatorController',b'clips.Controller');p.write_bytes(b)

