from pathlib import Path
import re, uuid
root=Path.cwd()
rt=root/'Assets/CharacterBehavior/Runtime'
ed=root/'Assets/CharacterBehavior/Editor'
work=root/'.codex-task-work/guard-handoff-replay-1122'
def read(p):return p.read_text(encoding='utf-8-sig')
def write(p,s):
    old=p.read_bytes() if p.exists() else b''
    p.write_bytes((b'\xef\xbb\xbf' if old.startswith(b'\xef\xbb\xbf') else b'')+s.replace('\n','\r\n' if b'\r\n' in old else '\n').encode('utf-8'))
def mod(p,a,b):
    s=read(p);assert s.count(a)==1,(p.name,a[:80],s.count(a));write(p,s.replace(a,b))
def span(p,a,b,x):
    s=read(p);i=s.index(a);j=s.index(b,i);write(p,s[:i]+x+s[j:])

import subprocess,yaml
before=work/'before'
def asset(p):return yaml.safe_load(read(p).split('\n',3)[3])['MonoBehaviour']
def ref(x):return (x.get('guid'),x['fileID'])
p=asset(root/'Assets/player/ShieldCharacterProfile.asset')
db=asset(root/'Assets/player/ShieldGroundMotion.asset')
lib=db['Playback']
states={s['Path']:ref(s['Clip']) for s in lib['States']}
curves={ref(c['Clip']):{r['Name'] for r in c.get('Curves') or [] if r['Curve'].get('m_Curve')} for c in lib['Clips']}
pres=p['Presentation'];actions={a['Key']:a for a in pres['actions']}
grips=[]
for channel in pres['IkChannels']:
    if not channel.get('GripActionKey'):continue
    a=actions[channel['GripActionKey']]
    for path in [a['StateName'], a.get('MovingStateName')]:
        if path:
            assert channel['WeightCurve'] in curves[states[path]],(path,channel)
            grips.append(path)
aliases=0
for t in db['BakedTracks']:
    assert len(t['Poses'])>=2 and t['Seconds']>0
    for state in t['States']:
        assert ref(t['Clip'])==states[state] and {'IKLeftFoot','IKRightFoot'}<=curves[states[state]], state
        aliases+=1
events=0
for a in pres['behaviorAnimations']:
    if a['Behavior']!=8:continue
    guid,_=states[actions[a['EntryAction']]['StateName']]
    paths=subprocess.check_output(['rg','-l','--fixed-strings','guid: '+guid,'Assets','-g','*.fbx.meta'],text=True,encoding='utf-8').splitlines()
    assert len(paths)==1
    meta=yaml.safe_load(read(Path(paths[0])))
    clips=meta['ModelImporter']['animations']['clipAnimations']
    assert len(clips)==1 and len(clips[0]['events'])==1,(paths,clips)
    e=clips[0]['events'][0];assert e['functionName']=='SwitchSocket' and e['time']==0
    events+=1
overrideFiles=list((root/'Assets').rglob('*.overrideController'))
print(f'Asset checks: {len(grips)} approved grip states have authored hand curves, {len(db["BakedTracks"])} tracks ({aliases} states) have matching sampled clips and both foot curves; {events} attacks have real frame-zero SwitchSocket markers. Saved override controllers: {len(overrideFiles)}.')
changed=[]
for folder in (rt,ed):
    for f in folder.glob('*.cs'):
        old=before/f.name
        if old.exists() and old.read_bytes()==f.read_bytes():continue
        changed.append(f)
        assert len(f.read_bytes().splitlines())<=500,f.name
        assert Path(str(f)+'.meta').is_file()
        if old.exists():
            run=subprocess.run(['git','-c','core.autocrlf=false','-c','core.whitespace=cr-at-eol','diff','--no-index','--check',str(old),str(f)],capture_output=True)
            assert run.returncode in (0,1) and not run.stdout.strip() and not run.stderr.strip(),(f,run.stdout[:300],run.stderr[:300])
        else:
            assert all(x.rstrip()==x for x in read(f).splitlines())
            guid=re.search(r'^guid: (\w+)',read(Path(str(f)+'.meta')),re.M)[1]
            matches=subprocess.check_output(['rg','-l','--fixed-strings','guid: '+guid,'Assets','-g','*.meta'],text=True).splitlines()
            assert len(matches)==1,matches
assert not pres.get('ReuseActionMixers',False)
print('Changed',len(changed),'C# sources;',len([f for f in changed if not (before/f.name).exists()]),'new, largest:',max((len(f.read_bytes().splitlines()),f.name) for f in changed))
bee=root/'Library/Bee/artifacts/1900b0aE.dag'
for assembly,folder in [('Assembly-CSharp',rt),('Assembly-CSharp-Editor',ed)]:
    lines=read(bee/(assembly+'.rsp')).splitlines()
    for i,line in enumerate(lines):
        if line.startswith('-out:'):lines[i]=f'-out:"{(work/(assembly+".dll")).as_posix()}"'
        elif line.startswith('-refout:'):lines[i]=f'-refout:"{(work/(assembly+".ref.dll")).as_posix()}"'
        elif assembly.endswith('Editor') and line=='-r:"Library/Bee/artifacts/1900b0aE.dag/Assembly-CSharp.ref.dll"':lines[i]=f'-r:"{(work/"Assembly-CSharp.ref.dll").as_posix()}"'
    for f in folder.glob('*.cs'):
        arg='"'+f.relative_to(root).as_posix()+'"'
        if arg not in lines:lines.append(arg)
        assert lines.count(arg)==1,f
    assert '-define:UNITY_EDITOR' in lines
    if assembly.endswith('Editor'):assert f'-r:"{(work/"Assembly-CSharp.ref.dll").as_posix()}"' in lines
    for line in lines:
        if line.startswith(('-r:"','-analyzer:"','/additionalfile:"','"Assets/')):
            path=Path(line.split('"')[1]); assert path.is_file() or path==work/'Assembly-CSharp.ref.dll',path
    (work/(assembly+'.rsp')).write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(assembly, 'final source input:',len([f for f in changed if f.parent==folder]))


