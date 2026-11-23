from pathlib import Path
import json, yaml, uuid, re, copy, math, shutil
stage=Path('.codex-task-work/ground-selection-20261121')
player=Path('Assets/player'); normal=Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal')
dest=player/'LocomotionTurns.fbx'
assert not dest.exists()
shutil.copyfile(stage/dest.name,dest)
newguid=uuid.uuid4().hex
ranges=json.loads((stage/'ranges.json').read_text())
sources=json.loads((stage/'samples.json').read_text())
srcmeta=(player/'LocomotionStops.fbx.meta').read_text()
runmeta=(normal/'SS_Run.fbx.meta').read_text()
def readclips(txt):return yaml.safe_load(txt)['ModelImporter']['animations']['clipAnimations']
runclip=readclips(runmeta)[0]
def curve(name,points):
    row=copy.deepcopy(next(c for c in runclip['curves'] if c['name']==name))
    basic=row['curve']['curve'][0]
    row['curve']['curve']=[dict(basic,time=float(t),value=float(v),inSlope=0,outSlope=0) for t,v in points]
    return row
def curve_text(rows):
    return '      curves:\n'+'\n'.join('      '+line for line in yaml.safe_dump(rows,sort_keys=False,width=5000).splitlines())+'\n'
def permission(keys,t):
    if t<=keys[0]['time']:return keys[0]['value']
    for a,b in zip(keys,keys[1:]):
        if t<=b['time']:
            d=b['time']-a['time']; u=(t-a['time'])/d
            return max(0,min(1,(2*u**3-3*u*u+1)*a['value']+(u**3-2*u*u+u)*d*a['outSlope']+(-2*u**3+3*u*u)*b['value']+(u**3-u*u)*d*b['inSlope']))
    return keys[-1]['value']
def contacts(n,cycle,offset,source):
    curves=[]
    for limb,delay in [('IKLeftFoot',0),('IKRightFoot',.5)]:
        if source:
            keys=next(c for c in runclip['curves'] if c['name']==limb)['curve']['curve']
            vals=[permission(keys,((i+offset)%cycle)/cycle) for i in range(n+1)]
        else:
            # Sprint contact intervals match the authored gait phase on the prefab.
            vals=[max(0,min(1,(.32-((i/cycle-delay)%1))*14,(((i/cycle-delay+.055)%1))*14)) if ((i/cycle-delay)%1)<.32 else 0 for i in range(n+1)]
            vals[-1]=vals[0]
        curves.append(curve(limb,[(i/n,round(v,5)) for i,v in enumerate(vals)]))
    return curves
prefix,rest=srcmeta.split('    clipAnimations:\n',1)
clips=rest.split('    - serializedVersion: 16\n')
last_tail=clips[-1]; foot=last_tail.index('    isReadable:')
tail=last_tail[foot:]
proto='    - serializedVersion: 16\n'+clips[1]
idrows='';blocks='';turnids={}
for j,(name,a,b) in enumerate(ranges):
    fid=740381001100001+j
    turnids[name]=fid
    idrows+=f'  - first:\n      74: {fid}\n    second: {name}\n'
    item=proto.replace('name: walk_stop_left','name: '+name).replace('takeName: LocomotionStops','takeName: LocomotionTurns')
    for f,v in [('internalID',fid),('firstFrame',a),('lastFrame',b)]:
        item=re.sub(r'(?m)^      '+f+r': .*$',f'      {f}: {v}',item)
    curves=[runclip['curves'][0]]+contacts(b-a,20,0 if name.endswith('_l') else 10,True)
    item=re.sub(r'      curves:\n.*?(?=      events:)',lambda _:curve_text(curves),item,flags=re.S)
    blocks+=item
prefix=re.sub(r'(  internalIDToNameTable:\n).*?(?=  externalObjects:)',lambda m:m[1]+idrows,prefix,flags=re.S)
turnmeta=prefix.replace('guid: 78f5e9bddcdf4f558d19848ae1755c0f','guid: '+newguid)+'    clipAnimations:\n'+blocks+tail
Path(str(dest)+'.meta').write_text(turnmeta,encoding='utf8')
sp=normal/'SS_Sprint.fbx.meta'
sprintmeta=sp.read_text()
(stage/'before/SS_Sprint.fbx.meta').write_text(sprintmeta,encoding='utf8')
assert '      curves: []\n' in sprintmeta
sprintmeta=sprintmeta.replace('      curves: []\n',curve_text(contacts(18,18,0,False)))
sp.write_text(sprintmeta,encoding='utf8')
controller=player/'player.controller'
s=controller.read_text()
a=s.index('  m_Name: Base Layer\n');b=s.index('  m_ChildStateMachines:',a)
template=re.search(r'--- !u!1102 &881100000000000005\n.*?(?=--- !u!)',s,re.S)[0]
children='';states=''
for i,(name,_,_) in enumerate(ranges):
    stateid=881100000000000030+i
    children+=f'  - serializedVersion: 1\n    m_State: {{fileID: {stateid}}}\n    m_Position: {{x: {850+(i%2)*235}, y: {280+i//2*80}, z: 0}}\n'
    item=template.replace('881100000000000005',str(stateid)).replace('locomotion_run_start',name)
    item=item.replace('m_SpeedParameterActive: 1','m_SpeedParameterActive: 0').replace('m_SpeedParameter: RunRate','m_SpeedParameter: ')
    item=re.sub(r'm_Motion: \{.*?\}',f'm_Motion: {{fileID: {turnids[name]}, guid: {newguid}, type: 3}}',item,flags=re.S)
    states+=item
controller.write_text(s[:b]+children+s[b:]+states,encoding='utf8')
def vec(values):
    return '{'+', '.join(f'{c}: {v:.8g}' for c,v in zip('xyz',values))+'}'
asset=player/'ShieldGroundMotion.asset'
header,old=asset.read_text().split('  Tracks:\n')
tracks=[x for x in re.split(r'(?=^  - Clip:)',old,flags=re.M) if x.strip()]
def guid(meta):return re.search(r'(?m)^guid: (\w+)',meta)[1]
def make(name,meta,states,speed,brake):
    clip=next(c for c in readclips(meta) if c['name']==name)
    fid=next(c['first'][74] for c in yaml.safe_load(meta)['ModelImporter']['internalIDToNameTable'] if c['second']==name)
    frames=sources[name];n=len(frames)-1;loop=name=='SS_Sprint'
    end= (n/30 if loop else .065 if brake else .12)
    raw=f'  - Clip: {{fileID: {fid}, guid: {guid(meta)}, type: 3}}\n    States:\n'
    raw+=''.join('    - Base Layer.'+x+'\n' for x in states)
    raw+=f'    ReferenceSpeed: {speed}\n    BrakeSeconds: {brake}\n    LastEntrySeconds: {end:.8g}\n    Seconds: {n/30:.8g}\n    Poses:\n'
    def point(f,b):
        x,y,z=[v-p for v,p in zip(f[b],f['pelvis'])];return [-x,-z,y]
    for i,f in enumerate(frames):
        before=i-1 if i else (n-1 if loop else 0)
        after=i+1 if i<n else (1 if loop else n)
        dt=(1 if not loop and i in (0,n) else 2)/30
        for side,b in [('Left','foot_l'),('Right','foot_r')]:
            raw+=('    - ' if side=='Left' else '      ')+side+': '+vec(point(f,b))+'\n'
        for side,b in [('Left','foot_l'),('Right','foot_r')]:
            raw+='      '+side+'Velocity: '+vec([(x-y)/dt for x,y in zip(point(frames[after],b),point(frames[before],b))])+'\n'
        raw+='      Contact: '+vec([permission(next(c for c in clip['curves'] if c['name']==k)['curve']['curve'],i/n) for k in ('IKLeftFoot','IKRightFoot')])+'\n'
    return raw
tracks.append(make('SS_Sprint',sprintmeta,['sprint'],6.52,0))
for g,speed,brake in [('walk',1.55,.115),('sprint',6.52,.16)]:
    for side in ['left','right']:
        name=g+'_stop_'+side
        tracks.append(make(name,srcmeta,[name],speed,brake))
for name,a,b in ranges:tracks.append(make(name,turnmeta,[name],4.98,0))
author='';baked=''
for raw in tracks:
    state=re.search(r'    - Base Layer\.(\w+)',raw)[1]
    use=4 if '_stop_' in state else 3 if '_turn_' in state else 1
    gait=1 if state.startswith('walk') else 3 if state.startswith('sprint') else 2
    side=1 if 'left' in state else 2 if 'right' in state else 0
    fields=f'    Use: {use}\n    Gait: {gait}\n    Side: {side}\n    Loop: {int(use==1)}\n    Blend: {0.12 if use==4 else 0.08}\n    MinimumSpeed: {2.5 if use==3 else 0}\n    FirstEntrySeconds: 0\n    ExitSeconds: {0.58 if use==3 else 0}\n    PathSource: {int(use==3)}\n'
    if use==3:
        sign=-1 if side==1 else 1
        fields+='    ReferencePath:\n'
        for t,x,z,yaw in [(0,0,0,0),(.08,.12,.28,32),(.16,.42,.42,68),(.24,.8,.46,88),(.4,1.59,.47,90),(.8,3.58,.47,90)]:
            fields+=f'    - Seconds: {t}\n      Position: {vec([sign*x,z])}\n      Yaw: {sign*yaw}\n'
    raw=raw.replace('    ReferenceSpeed:',fields+'    ReferenceSpeed:')
    baked+=raw
    author+=raw.split('    Poses:\n')[0]
refs=''.join(x.replace('  Avatar:','  BakedAvatar:').replace('  Controller:','  BakedController:')+'\n' for x in header.splitlines() if x.startswith(('  Avatar:','  Controller:')))
asset.write_text(header+'  Format: 1\n'+refs+'  Tracks:\n'+author+'  BakedTracks:\n'+baked,encoding='utf8')
print('samples',len(tracks),'turn clips',ranges)

