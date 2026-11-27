from pathlib import Path
import re,uuid,shutil
task=Path('.codex-task-work/ik-separation-20261127-73ce')
anim=Path('Assets/Code/Character/Anim')
base=task/'baseline'/anim
stage=task/'stage'
renames={'CharacterPoseJob.cs':'CharacterIkJob.cs','CharacterPosePipeline.cs':'CharacterAnimationIk.cs'}
def edit(name,old,new):
    p=stage/(name+'.cs')
    s=p.read_text(encoding='utf8')
    assert old in s,old
    p.write_text(s.replace(old,new),encoding='utf8')
def checked(path):
    result=path.resolve()
    assert result.is_relative_to(anim.resolve()),path
    return result
edit('CharacterPoseJob','                Transition.Apply(stream, support, Correction.ReadHand(stream, Skeleton, Correction.LeftHand).Grip, Correction.ReadHand(stream, Skeleton, Correction.RightHand).Grip);','''                var leftGrip = Correction.ReadHand(stream, Skeleton, Correction.LeftHand).Grip;
                var rightGrip = Correction.ReadHand(stream, Skeleton, Correction.RightHand).Grip;
                Transition.Apply(stream, support, leftGrip, rightGrip);''')
edit('CharacterIkInput','if (Requests.Select(CharacterIkSource.Ground, channel.ProbeGround, 0, actions, out input.Ground))','if (Requests.Select(CharacterIkSource.Ground, channel.ProbeGround, 0, actions, out var ground))\n            {\n                input.Ground = ground;')
edit('CharacterIkInput','            else input.Ground = default;','            }')
edit('CharacterIkChannels','                var binding = weights[i];','                var channelWeights = weights[i];')
edit('CharacterIkChannels','binding.ClipPermission','channelWeights.ClipPermission')
edit('CharacterIkChannels','weights[i] = binding;','weights[i] = channelWeights;')
edit('CharacterIkSkeleton','        // 核对当前动画流','\n        // 核对当前动画流')
edit('CharacterIkSkeleton','        // 读取当前骨链','\n        // 读取当前骨链')
edit('CharacterIkDecision','        internal static bool IsCurrent','        private static bool IsCurrent')
edit('CharacterIkRequests','        // 核对来源','\n        // 核对来源')
edit('CharacterGripIk','            if (string.IsNullOrEmpty(key)) return default;\n','')
for n in ['CharacterGripIk','CharacterIkCorrection']:
    edit(n,'        }\n\n    }\n}', '        }\n    }\n}')
request_file=stage/'CharacterIkInput.cs'
request_text=request_file.read_text(encoding='utf8')
request_type=request_text[request_text.index('    internal struct CharacterIkRequest'):request_text.index('    // 场景输入只传值')]
edit('CharacterIkInput',request_type,'')
request_type=request_type.replace('        public bool Matches','        // 请求有效且绑定的动作实例仍拥有某个部位时，允许该来源参与。\n        public bool Matches').replace('        private bool Owns','        // 同一动作键的不同实例分别持有各自的请求。\n        private bool Owns')
edit('CharacterIkRequests','    // 按来源和提供者','    // 保存一个来源最近一次提交的目标和所属动作。\n'+request_type+'    // 按来源和提供者')
edit('CharacterPosePipeline','using UnityEngine.Experimental.Animations;\n','')
# Preflight every source/metadata pair before a single project file is changed.
sources=sorted(stage.rglob('*.cs'))
for source in sources:
    rel=source.relative_to(stage)
    old=Path(renames.get(rel.as_posix(),str(rel)))
    for ext in ['', '.meta']:
        where=checked(anim/(str(old)+ext)); baseline=base/(str(old)+ext)
        if baseline.exists():
            assert where.read_bytes()==baseline.read_bytes(),'Concurrent edit: '+str(where)
        else:
            assert not where.exists(),'Name collision: '+str(where)
        if rel!=old:
            assert not checked(anim/(str(rel)+ext)).exists()
# Preserve original encoding and line endings on paths that already existed.
for source in sources:
    rel=source.relative_to(stage)
    old=Path(renames.get(rel.as_posix(),str(rel)))
    dest=checked(anim/rel); prev=checked(anim/old)
    if rel!=old:
        prev.rename(dest)
        checked(anim/(str(old)+'.meta')).rename(checked(anim/(str(rel)+'.meta')))
    reference=base/old
    s=source.read_text(encoding='utf8')
    raw=reference.read_bytes() if reference.is_file() else b''
    if b'\r\n' in raw and raw.count(b'\r\n')==raw.count(b'\n'): s=s.replace('\n','\r\n')
    enc='utf-8-sig' if raw.startswith(b'\xef\xbb\xbf') else 'utf8'
    dest.write_bytes(s.encode(enc))
    if not reference.exists():
        template=(base/'CharacterIkJob.cs.meta').read_bytes()
        checked(anim/(str(rel)+'.meta')).write_bytes(re.sub(rb'(?m)^guid: [a-f0-9]{32}',b'guid: '+uuid.uuid4().hex.encode(),template))
print('Installed',len(sources),'sources; two script/meta renames preserve previous GUIDs.')
