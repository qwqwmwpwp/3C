from pathlib import Path
r=Path('Assets/CharacterBehavior/Runtime')
def edit(name,ops):
    p=r/name
    raw=p.read_bytes()
    st=raw.decode().replace('\r\n','\n')
    for a,b in ops:
        assert st.count(a)==1,(name,a)
        st=st.replace(a,b)
    p.write_bytes(st.encode().replace(b'\n',b'\r\n' if b'\r\n' in raw else b'\n'))
edit('CharacterBehaviorAnimator.cs',[
('        [Header("地面动作匹配")]', '''        [Header("片段播放")]
        [SerializeField, Tooltip("提供零参数播放用的状态、遮罩和片段曲线。留空时使用地面动作数据库。")]
        private CharacterMotionDatabase playbackLibrary;

        [Header("地面动作匹配")]'''),
('                MatchTurnAngle = matchTurnAngle, MatchDatabase = motionDatabase,', '                PlaybackLibrary = playbackLibrary ? playbackLibrary : motionDatabase,\n                MatchTurnAngle = matchTurnAngle, MatchDatabase = motionDatabase,')])
edit('CharacterAnimationPipeline.cs',[
('        public CharacterMotionDatabase MatchDatabase;', '        public CharacterMotionDatabase PlaybackLibrary, MatchDatabase;'),
('                    player.InvalidateController();\n                }','                    clips.Close();\n                }'),
('            Ik.Prepare(selection, motion, plan); // 目标许可取自基础层实际混合的素材曲线。','            Ik.Prepare(selection, motion, plan); // 目标许可与当前片段混合、分层遮罩同步。')])
edit('CharacterClipPlayback.cs',[
('using System;\n',''),
('            database = config.MatchDatabase;', '            database = config.PlaybackLibrary;'),
('        public PlayableGraph Graph => graph;', '''        public PlayableGraph Graph => graph;
        public bool ControllerReassigned => graph.IsValid() && animator && animator.runtimeAnimatorController;'''),
('            delta = Mathf.Max(0f, dt * animator.speed);', '''            float step = animator.updateMode == AnimatorUpdateMode.UnscaledTime ? Time.unscaledDeltaTime : dt;
            delta = Mathf.Max(0f, step * animator.speed);'''),
('''        public float Permission(string curve)
        {
            if (mixers == null || config.FullLayer < 0 || config.FullLayer >= mixers.Length) return 0f;
            return mixers[config.FullLayer].Permission(curve, delta);
        }''','''        public float Permission(CharacterIkChannel channel)
        {
            if (mixers == null || config.FullLayer < 0 || config.FullLayer >= mixers.Length) return 0f;
            float value = mixers[config.FullLayer].Permission(channel.WeightCurve, delta);
            var part = channel.Limb switch
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
                float local = mixers[i].Permission(channel.WeightCurve, delta);
                value = layer.Additive ? Mathf.Clamp01(value + local * weight) : Mathf.Lerp(value, local, weight);
            }
            return value;
        }''')])
edit('CharacterAnimationResourceCatalog.cs',[
('controllerChanged = !hasCheckedController || checkedController != controller;', 'controllerChanged = !hasCheckedController || checkedController != controller || playback.ControllerReassigned;')])
edit('CharacterAnimationIk.cs',[
('/// <summary>在已有控制器输出之后追加一次姿态求值，播放状态仍由原控制器持有。</summary>','/// <summary>在片段混合之后求解惯性和 IK，兼容已有控制器输出。</summary>'),
('clips.Permission(channels[i].WeightCurve)','clips.Permission(channels[i])')])
edit('CharacterClipMixer.cs',[('float rate = voice.Stop ? stopRate : stride.HasValue && voice.NominalSpeed > 0f\n                    ? stride.Value / voice.NominalSpeed : 1f;','''float rate = 1f;
                if (voice.Stop) rate = stopRate;
                else if (stride.HasValue && voice.NominalSpeed > 0f) rate = stride.Value / voice.NominalSpeed;''')])
p=Path('Assets/player/player 1.prefab');raw=p.read_bytes()
import re
assert raw.count(b'  motionDatabase:')==1
raw=re.sub(rb'(?m)^(  )motionDatabase: ([^\r\n]+)(\r?\n)',rb'\1playbackLibrary: \2\3\1motionDatabase: \2\3',raw)
p.write_bytes(raw)

