from pathlib import Path
import re, yaml, math, json
root=Path.cwd()
stage=root/'.codex-task-work/ground-stop'
files=list((root/'Assets/CharacterBehavior/Runtime').glob('*.cs'))+list((root/'Assets/CharacterBehavior/Editor').glob('*.cs'))
changed={'CharacterRigidbodyMotor','CharacterBehaviorDriver','CharacterAnimationLocomotion','CharacterLocomotionGaitPhase',
'CharacterBehaviorAnimator','CharacterAnimationPipeline','CharacterAnimationResourceCatalog','CharacterAnimationResourceResolver','CharacterAnimationPlayer','CharacterBehaviorAnimatorEditor'}
files=[p for p in files if p.stem in changed]
assert len(files)==10
for p in files:
 s=p.read_text(encoding='utf-8-sig'); lines=s.splitlines()
 assert len(lines)<=500 and '<<<<<<<' not in s
 rsp='Assembly-CSharp-Editor.rsp' if 'Editor' in p.parts else 'Assembly-CSharp.rsp'
 assert p.relative_to(root).as_posix() in (stage/rsp).read_text(encoding='utf-8-sig')
 print('source',p.name,len(lines))
def unity_docs(p):
 return [(int(n),yaml.safe_load(body)) for n,body in re.findall(r'^--- !u!\d+ &(-?\d+)[^\n]*\n(.*?)(?=^--- !u!|\Z)',p.read_text(encoding='utf-8-sig'),re.M|re.S)]
folder=root/'Assets/player'
meta=yaml.safe_load((folder/'LocomotionStops.fbx.meta').read_text())
importer=meta['ModelImporter']
ct=dict(unity_docs(folder/'player.controller'))
base=next(v['AnimatorStateMachine'] for v in ct.values() if 'AnimatorStateMachine' in v and v['AnimatorStateMachine']['m_Name']=='Base Layer')
states={ct[ref['m_State']['fileID']]['AnimatorState']['m_Name']:ct[ref['m_State']['fileID']]['AnimatorState'] for ref in base['m_ChildStates']}
ids={x['second']:x['first'][74] for x in importer['internalIDToNameTable']}
pf=unity_docs(folder/'player 1.prefab')
components=[v['MonoBehaviour'] for _,v in pf if 'MonoBehaviour' in v]
motor=next(c['motor'] for c in components if 'motor' in c)
driver=next(c for c in components if 'motor' in c)
config=next(c for c in components if 'minimumStopTravel' in c)
assert config['minimumStopTravel']==.09
ranges=json.loads((stage/'ranges.json').read_text())
assert len(ranges)==len(ids)==len(importer['animations']['clipAnimations'])==6
for (name,first,last),clip in zip(ranges,importer['animations']['clipAnimations']):
 gait,_,side=name.split('_')
 assert config[gait+'Stop'][side.title()+'Support']=='Base Layer.'+name
 state=states[name]
 assert state['m_Speed']==1 and state['m_SpeedParameterActive']==0
 assert state['m_Motion']==dict(fileID=ids[name],guid=meta['guid'],type=3)
 assert (clip['internalID'],clip['name'],clip['takeName'],clip['firstFrame'],clip['lastFrame'])==(ids[name],name,'LocomotionStops',first,last)
 assert not clip['loopTime'] and not clip['events']
 curves={c['name']:[(k['time'],k['value']) for k in c['curve']['curve']] for c in clip['curves']}
 support=curves['IK'+side.title()+'Foot']
 swing=curves['IK'+('Right' if side=='left' else 'Left')+'Foot']
 assert support[0][1]==1 and swing[0][1]==0 and support[-1]==swing[-1]==(1,1)
 assert min(t for t,v in swing if v==1) <= max(t for t,v in support if v==1 and t<.8)
 assert {p['WeightCurve'] for p in config['ikChannels']}==set(curves)
scene=(root/'Assets/Scenes/角色测试.unity').read_text(encoding='utf8')
for asset in ['player 1.prefab','player.controller']:
 guid=yaml.safe_load((folder/(asset+'.meta')).read_text())['guid']
 assert guid in scene
assert not re.search('propertyPath: (motor\.|walkStop|runStop|sprintStop|minimumStopTravel)',scene)
dt=yaml.safe_load((root/'ProjectSettings/TimeManager.asset').read_text().split('TimeManager:\n',1)[1])['Fixed Timestep']
for gait,speed in [('walk','WalkSpeed'),('run','MoveSpeed'),('sprint','SprintSpeed')]:
 v=driver['settings'][speed]; distance=0.; ticks=0
 while v>0:
  v=max(0,v-motor[gait+'Braking']*dt); distance+=v*dt; ticks+=1
 print(gait,'stop distance',round(distance,4),'m;',ticks,'steps')
print('Verified 6 non-looping controller states, imported IDs, clip ranges, foot gates, full path from scene and compile coverage.')
