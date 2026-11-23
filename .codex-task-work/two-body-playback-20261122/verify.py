import copy, re, struct, subprocess, sys
from pathlib import Path
import yaml
root=Path.cwd(); work=root/'.codex-task-work/two-body-playback-20261122'; before=work/'before'; player=root/'Assets/player'
def read(p): return p.read_text(encoding='utf-8-sig')
def guid(p): return re.search(r'^guid: (\w+)$',read(Path(str(p)+'.meta')),re.M)[1]
def single(p): return yaml.safe_load(read(p).split('\n',3)[3])['MonoBehaviour']
def paths(s): return s.replace('Lower Body.','Lower Body.Local.').replace('Base Layer.','Lower Body.').replace('Attack.','Lower Body.Actions.')
def ctl(p):
    docs = re.split(r'^--- !u!(\d+) &(-?\d+)\s*\n',read(p),flags=re.M)[1:]
    return {int(id):next(iter(yaml.safe_load(text).values())) for k,id,text in zip(docs[::3],docs[1::3],docs[2::3])}
p=single(player/'ShieldCharacterProfile.asset'); oldP=single(before/'ShieldCharacterProfile.asset')
db=single(player/'ShieldGroundMotion.asset'); oldDB=single(before/'ShieldGroundMotion.asset')
c=ctl(player/'player.controller'); layers=c[9100000]['m_AnimatorLayers']
assert [l['m_Name'] for l in layers]==['Lower Body','Upper Body']
states={}; reachable=set()
def walk(id,path,layer):
    assert id not in reachable; reachable.add(id); v=c[id]
    for st in v['m_ChildStates']:
        sid=st['m_State']['fileID']; assert sid not in reachable; reachable.add(sid); s=c[sid]
        name=path+'.'+s['m_Name']; assert name not in states
        states[name]=(layer,s['m_Motion'],s['m_Speed'])
    for child in v['m_ChildStateMachines']:
        cid=child['m_StateMachine']['fileID']; walk(cid,path+'.'+c[cid]['m_Name'],layer)
pb=db['Playback']; masks=[]
for i,(l,b) in enumerate(zip(layers,pb['Layers'])):
    mach=l['m_StateMachine']['fileID'];walk(mach,l['m_Name'],i)
    default=c[c[mach]['m_DefaultState']['fileID']]['m_Name']
    assert b['DefaultState']==l['m_Name']+'.'+default and b['Mask']==l['m_Mask']
    mask=player/('Player_LowerBody.mask' if i==0 else 'Player_UpperBody.mask')
    assert guid(mask)==b['Mask']['guid']
    raw=bytes.fromhex(re.search(r'm_Mask: (\w+)',read(mask))[1]); masks.append(struct.unpack('<13I',raw))
assert len(reachable)==len(c)-1
assert len(pb['Layers'])==2 and pb['BodyOutputs']==1 and db['PlaybackMode']==3
assert all(a+b==1 for a,b in zip(*masks)) and all(masks[0][i] for i in (0,3,4,9,10)) and all(masks[1][i] for i in (1,2,5,6,7,8,11,12))
current={s['Path']:(s['Layer'],s['Clip'],s['Speed']) for s in pb['States']}
assert current==states and len(current)==len(pb['States'])
for s in oldDB['Playback']['States']:
    assert current[paths(s['Path'])]==(int(s['Layer']==3),s['Clip'],s['Speed'])
assert len(oldDB['Playback']['States'])==len(current) and pb['Clips']==oldDB['Playback']['Clips']
for bucket in ('Tracks','BakedTracks'):
    old=copy.deepcopy(oldDB[bucket])
    for t in old:t['States']=[paths(st) for st in t['States']]
    assert db[bucket]==old,bucket
    for t in old:
        for st in t['States']:assert states[st][1]==t['Clip'] and states[st][0]==db.get('GroundLayer',0)
pres=p['Presentation']; a={r['Key']:r for r in pres['actions']}
for field,val in pres.items():
    if field.endswith('StateName') and val: assert val in states,(field,val)
for row in pres['locomotionGaitPhases']: assert row['StateName'] in states
for key in ('walkStop','runStop','sprintStop'):
    for side in ('LeftSupport','RightSupport'):assert pres[key][side] in states
for key,v in a.items():
    assert v['StateName'] in states and (not v.get('MovingStateName') or v['MovingStateName'] in states)
    assert all(other in a for other in v.get('Partners',{}).get('Keys',[]))
for r in pres['behaviorAnimations']:
    assert all(r.get(f) in a for f in ('EntryAction','HoldAction') if r.get(f))
combo=p['LightAttack']['Combo']; moves={1:p['LightAttack'],2:combo['LightTwo'],3:combo['Heavy']}
for r in pres['behaviorAnimations']:
    if r['Behavior']==8:
        act=a[r['EntryAction']]; assert act['Region']==0 and states[act['StateName']][1]==moves[r['AttackMove']]['Clip']
assert p['LightAttack']==oldP['LightAttack']
assert guid(player/'player.controller')==db['BakedController']['guid']
assert guid(player/'ShieldGroundMotion.asset')==p['PlaybackLibrary']['guid']
assert guid(player/'ShieldCharacterProfile.asset') in read(player/'player 1.prefab')
assert guid(player/'player 1.prefab') in read(root/'Assets/Scenes/角色测试.unity')
sources=[]; modified=[]
for old in before.iterdir():
    if old.suffix=='.cs':
        f=next((root/'Assets/CharacterBehavior').rglob(old.name))
    elif old.suffix=='.unity': f=root/'Assets/Scenes'/old.name
    else: f=player/old.name
    if f.read_bytes()==old.read_bytes():continue
    modified.append(f)
    if f.suffix=='.cs': sources.append(f)
    run=subprocess.run(['git','-c','core.autocrlf=false','-c','core.whitespace=cr-at-eol','diff','--no-index','--check',str(old),str(f)],capture_output=True)
    assert run.returncode in (0,1) and not run.stdout.strip() and not run.stderr.strip(),(f,run.stdout[:400],run.stderr[:200])
new=root/'Assets/CharacterBehavior/Runtime/CharacterBodyPlaybackGraph.cs';sources.append(new)
assert all(len(s.read_bytes().splitlines())<=500 for s in sources)
assert all(Path(str(s)+'.meta').exists() for s in sources)
assert not any(line!=line.rstrip() for line in read(new).splitlines())
assert sum(1 for f in (root/'Assets/CharacterBehavior').rglob('*.meta') if guid(new) in read(f))==1
print(f'2 controller layers; 13 disjoint humanoid mask groups; {len(states)} authored/baked states; {len(a)} action keys; {len(db["BakedTracks"])} unchanged baked motion tracks; all 3 attacks reference their original clips.')
print('Changed sources',len(sources),'largest',max((len(s.read_bytes().splitlines()),s.name) for s in sources))
bee=root/'Library/Bee/artifacts/1900b0aE.dag'
for name in ('Assembly-CSharp','Assembly-CSharp-Editor'):
    lines=read(bee/(name+'.rsp')).splitlines()
    for i,line in enumerate(lines):
        if line.startswith('-out:'):lines[i]=f'-out:"{(work/(name+".dll")).as_posix()}"'
        elif line.startswith('-refout:'):lines[i]=f'-refout:"{(work/(name+".ref.dll")).as_posix()}"'
        elif name.endswith('Editor') and line=='-r:"Library/Bee/artifacts/1900b0aE.dag/Assembly-CSharp.ref.dll"':lines[i]=f'-r:"{(work/"Assembly-CSharp.ref.dll").as_posix()}"'
    if name=='Assembly-CSharp':
        add='"'+new.relative_to(root).as_posix()+'"'
        if add not in lines:lines.append(add)
    expected=[s for s in sources if ('Editor' in s.parts)==name.endswith('Editor')]
    for s in expected: assert '"'+s.relative_to(root).as_posix()+'"' in lines,s
    for line in lines:
        if line.startswith(('-r:"','-analyzer:"','/additionalfile:"','"Assets/')):
            path=Path(line.split('"')[1]); assert path.is_file() or path==work/'Assembly-CSharp.ref.dll',path
    assert '-define:UNITY_EDITOR' in lines
    if name.endswith('Editor'):assert f'-r:"{(work/"Assembly-CSharp.ref.dll").as_posix()}"' in lines
    (work/(name+'.rsp')).write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(name,'covered',len(expected),'changed sources')

