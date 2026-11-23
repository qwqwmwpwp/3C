from pathlib import Path
import json, re, copy, shutil, hashlib, yaml
import uuid
folder=Path('Assets/player')
stage=Path('.codex-task-work/ground-stop')
segments=json.loads((stage/'ranges.json').read_text())
guid='78f5e9bddcdf4f558d19848ae1755c0f'
template=Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal/SS_Run.fbx.meta').read_text()
fromstruct=yaml.safe_load(template)['ModelImporter']['animations']['clipAnimations'][0]
def curve(name,points):
 c=copy.deepcopy(fromstruct['curves'][0])
 c['name']=name
 k=c['curve']['curve'][0]
 c['curve']['curve']=[dict(k,time=t,value=v) for t,v in points]
 return c
support=[(0,1),(.67,1),(.73,0),(.90,0),(.985,1),(1,1)]
swing=[(0,0),(.55,0),(.665,1),(1,1)]
src=template.split('    clipAnimations:\n')[1].split('    isReadable:')[0]
def field(src,name,val): return re.sub('^      '+name+': .*$', '      '+name+': '+str(val),src,flags=re.M)
metaclips=[]
refs=[]; blocks=[]; children=[]
ctrlfile=folder/'player.controller'
ctrl=ctrlfile.read_text()
prototype=re.search(r'^--- !u!1102 &881100000000000007\n.*?(?=^--- !u!)',ctrl,re.M|re.S)[0]
for i,(n,first,last) in enumerate(segments):
 left=n.endswith('left')
 id=int.from_bytes(hashlib.sha256(n.encode()).digest()[:8],'big',signed=True)
 stateid=881100000000000021+i
 refs.append(f'  - first:\n      74: {id}\n    second: {n}\n')
 clip=src
 for k,v in [('name',n),('takeName','LocomotionStops'),('internalID',id),('firstFrame',first),('lastFrame',last),('loopTime',0)]: clip=field(clip,k,v)
 curves=[fromstruct['curves'][0],curve('IKLeftFoot',support if left else swing),curve('IKRightFoot',swing if left else support)]
 curve_yaml=yaml.safe_dump(curves,sort_keys=False,width=180).rstrip()
 replacement='      curves:\n'+'\n'.join('      '+l for l in curve_yaml.splitlines())+'\n      events: []\n'
 clip=clip[:clip.index('      curves:')]+replacement+clip[clip.index('      transformMask:'):]
 metaclips.append(clip)
 state=prototype.replace('&881100000000000007','&'+str(stateid)).replace('locomotion_turn_right',n).replace('m_Speed: 1.5','m_Speed: 1')
 state=re.sub(r'm_Motion: \{[^}]+\}', 'm_Motion: {fileID: '+str(id)+', guid: '+guid+', type: 3}',state)
 blocks.append(state)
 children.append(f'  - serializedVersion: 1\n    m_State: {{fileID: {stateid}}}\n    m_Position: {{x: {850 + i%2*235}, y: {50+i//2*70}, z: 0}}\n')
meta=template.replace('2336e21b03e6d85468ba775ae8809a76',guid,1)
meta=re.sub(r'(  internalIDToNameTable:\n).*?(  externalObjects:)',lambda m:m[1]+''.join(refs)+m[2],meta,count=1,flags=re.S)
meta=meta.replace(src,''.join(metaclips))
assert len(yaml.safe_load(meta)['ModelImporter']['animations']['clipAnimations'])==6
start=ctrl.index('  m_Name: Base Layer\n  m_ChildStates:')
insert=ctrl.index('  m_ChildStateMachines:',start)
ctrl=ctrl[:insert]+''.join(children)+ctrl[insert:]+''.join(blocks)
dest=folder/'LocomotionStops.fbx'
assert not dest.exists() and 'walk_stop_left' not in ctrlfile.read_text()
dest.with_suffix('.fbx.meta').write_text(meta,encoding='utf8')
shutil.copyfile(stage/'LocomotionStops.fbx',dest)
ctrlfile.write_bytes(ctrl.encode('utf8'))
