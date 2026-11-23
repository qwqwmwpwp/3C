from pathlib import Path
import collections,subprocess,re
s=Path(__file__).parent;before=s/'before'
changes=[]
for f in sorted(Path('Assets/CharacterBehavior').rglob('*.cs')):
    old=before/f.relative_to('Assets/CharacterBehavior')
    if old.exists() and old.read_bytes()==f.read_bytes():continue
    n=len(f.read_bytes().splitlines())
    assert n<=500,(f,n)
    assert f.with_suffix('.cs.meta').is_file()
    if not old.exists():
        guid=re.search(r'(?m)^guid: (\w+)',f.with_suffix('.cs.meta').read_text())[1]
        hits=subprocess.run(['rg','--crlf','-l','-g','*.meta','^guid: '+guid+'$','Assets'],capture_output=True,text=True).stdout.splitlines()
        assert len(hits)==1,(guid,hits)
    changes.append((f,n))
for name,editor in [('Assembly-CSharp',False),('Assembly-CSharp-Editor',True)]:
    refs=Path('.codex-task-work/ground-selection-20261121')
    rsp=(refs/(name+'.rsp')).read_text(encoding='utf-8-sig').replace(refs.as_posix(),s.as_posix())
    for f,n in changes:
        if ('Editor' in f.parts)!=editor:continue
        quoted='"'+f.as_posix()+'"'
        if quoted not in rsp:rsp+='\n'+quoted+'\n'
    for line in rsp.splitlines():
        if line.startswith(('-r:"','-analyzer:"','/additionalfile:"')) or line.startswith('"') and line.endswith('.cs"'):
            f=Path(line.split('"')[1])
            if f.parent==s and f.name=='Assembly-CSharp.ref.dll':continue
            assert f.is_file(),line
    assert '-define:UNITY_EDITOR' in rsp
    (s/(name+'.rsp')).write_text(rsp,encoding='utf8')
print(f'Changed sources {len(changes)}, max {max(n for f,n in changes)} lines, compiler includes them all.')
for f,n in changes: print(n,f.name)
for a,b in [('Runtime','Assets/CharacterBehavior/Runtime'),('Editor','Assets/CharacterBehavior/Editor'),('player.controller','Assets/player/player.controller'),('ShieldGroundMotion.asset','Assets/player/ShieldGroundMotion.asset')]:
    cmd=['git','-c','core.safecrlf=false','diff','--no-index','--check','--',str(before/a),b]
    res=subprocess.run(cmd,capture_output=True)
    assert not (res.stdout+res.stderr).strip(),(a,(res.stdout+res.stderr)[:1500])

