from pathlib import Path
import re,hashlib,collections
p=Path('.codex-task-work/ground-selection-20261121')
before=p/'before'
target=Path('Assets/CharacterBehavior')
changes=[]
for f in sorted(target.rglob('*.cs')):
    old=before/f.relative_to(target)
    if old.exists() and old.read_bytes()==f.read_bytes():continue
    text=f.read_text(encoding='utf-8-sig').splitlines()
    assert len(text)<=500,(f,len(text))
    assert f.with_suffix('.cs.meta').exists()
    print(f'{len(text):3}  {f.name}')
    changes.append(f)
stage=p.as_posix();src=Path('.codex-task-work/run-motion-match')
for name,editor in [('Assembly-CSharp',False),('Assembly-CSharp-Editor',True)]:
    rsp=(src/(name+'.rsp')).read_text(encoding='utf-8-sig').replace(src.as_posix(),stage)
    for f in changes:
        if ('Editor' in f.parts)!=editor:continue
        item='"'+f.as_posix()+'"'
        if item not in rsp:rsp+='\n'+item+'\n'
    for line in rsp.splitlines():
        if line.startswith(('-r:"','-analyzer:"','/additionalfile:"')) or line.startswith('"') and line.endswith('.cs"'):
            fp=Path(line.split('"')[1])
            if fp.parent==p and fp.name=='Assembly-CSharp.ref.dll':continue
            assert fp.is_file(),line
    assert '-define:UNITY_EDITOR' in rsp
    (p/(name+'.rsp')).write_text(rsp,encoding='utf8')
print(f'{len(changes)} changed C# files covered by Editor compiler inputs.')
# Unity source/script and resource identities: new GUIDs unique project-wide.
ids={re.search(r'(?m)^guid: (\w+)',q.read_text())[1] for q in [target/'Runtime/CharacterLegacyMotionMatcher.cs.meta',target/'Runtime/GroundMotionPath.cs.meta',Path('Assets/player/LocomotionTurns.fbx.meta')]}
import subprocess
for key in ids:
    hits=subprocess.run(['rg','--crlf','-l','-g','*.meta','^guid: '+key+'$','Assets'],capture_output=True,text=True).stdout.splitlines()
    assert len(hits)==1,(key,hits)


