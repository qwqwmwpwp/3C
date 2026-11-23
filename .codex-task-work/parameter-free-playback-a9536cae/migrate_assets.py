from pathlib import Path
import re, yaml, uuid
p=Path('Assets/player/player.controller')
raw=p.read_bytes(); s=raw.decode().replace('\r\n','\n')
docs={int(i):yaml.safe_load(b) for i,b in re.findall(r'(?ms)^--- !u!\d+ &(-?\d+)\n(.*?)(?=^--- !u!|\Z)',s)}
ctrl=next(x['AnimatorController'] for x in docs.values() if 'AnimatorController' in x)
assert len(ctrl['m_AnimatorParameters']) == 8
s,count=re.subn(r'  m_AnimatorParameters:\n.*?(?=  m_AnimatorLayers:)', '  m_AnimatorParameters: []\n',s,flags=re.S)
assert count==1
s=s.replace('  m_SpeedParameterActive: 1\n','  m_SpeedParameterActive: 0\n')
s=re.sub(r'(?m)^  m_SpeedParameter: (?:WalkRate|RunRate|SprintRate|DashRate|StopRate)$','  m_SpeedParameter:',s)
def ref(data):
    return '{'+', '.join(f'{k}: {v}' for k,v in data.items())+'}'
layers=[];states=[];clips={}
def collect(index,machine,name,default):
    for row in machine['m_ChildStates']:
        sid=row['m_State']['fileID']; state=docs[sid]['AnimatorState']; path=name+'.'+state['m_Name']
        motion=state['m_Motion'];assert motion['fileID']!=0
        assert not state['m_Transitions'] and not state['m_StateMachineBehaviours']
        states.append((path,motion,index,state['m_Speed']))
        clips[(motion['guid'],motion['fileID'])]=motion
        if sid==default:layers[index][3]=path
    for row in machine['m_ChildStateMachines']:
        child=docs[row['m_StateMachine']['fileID']]['AnimatorStateMachine']
        collect(index,child,name+'.'+child['m_Name'],default)
for i,l in enumerate(ctrl['m_AnimatorLayers']):
    layers.append([l['m_Mask'],l['m_BlendingMode'],l['m_DefaultWeight'],''])
    m=docs[l['m_StateMachine']['fileID']]['AnimatorStateMachine']
    collect(i,m,l['m_Name'],m['m_DefaultState']['fileID'])
body='  Playback:\n    Layers:\n'
for mask,add,weight,default in layers:
    body+=f'    - Mask: {ref(mask)}\n      Additive: {add}\n      Weight: {weight}\n      DefaultState: {default}\n'
body+='    States:\n'
for path,motion,layer,speed in states:
    body+=f'    - Path: {path}\n      Clip: {ref(motion)}\n      Layer: {layer}\n      Speed: {speed}\n'
body+='    Clips:\n'
models={}
for root in [Path('Assets/player'),Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations')]:
    for f in root.rglob('*.fbx.meta'):
        rawmeta=f.read_text(); guid=re.search(r'(?m)^guid: (\w+)',rawmeta)[1]
        if any(k[0]==guid for k in clips):models[guid]=yaml.safe_load(rawmeta)['ModelImporter']
count=0
for (guid,id),motion in clips.items():
    model=models[guid]
    name=next(r['second'] for r in model['internalIDToNameTable'] if r['first'].get(74)==id)
    clip=next(x for x in model['animations']['clipAnimations'] if x['name']==name)
    rows=clip.get('curves') or []
    body+=f'    - Clip: {ref(motion)}\n      Curves:'+ ('\n' if rows else ' []\n')
    for row in rows:
        cr=row['curve']; count+=1
        body+=f"      - Name: {row['name']}\n        InSeconds: 0\n        Curve:\n          serializedVersion: 2\n          m_Curve:\n"
        for key in cr['curve']:
            body+='          - serializedVersion: 3\n'
            for k,v in key.items():
                if k!='serializedVersion':body+=f'            {k}: {v}\n'
        for k,field in [('preInfinity','m_PreInfinity'),('postInfinity','m_PostInfinity'),('rotationOrder','m_RotationOrder')]:
            body+=f'          {field}: {cr[k]}\n'
asset=Path('Assets/player/ShieldGroundMotion.asset'); a=asset.read_bytes()
assert b'  Playback:' not in a
eol=b'\r\n' if b'\r\n' in a else b'\n'
asset.write_bytes(a.replace(b'  Format: 1'+eol,(body+'  Format: 1\n').encode().replace(b'\n',eol)))
p.write_bytes(s.encode().replace(b'\n', b'\r\n' if b'\r\n' in raw else b'\n'))
for name in ['Runtime/CharacterClipLibrary','Runtime/CharacterClipMixer','Runtime/CharacterClipPlayback','Editor/CharacterClipLibraryBaker']:
    f=Path('Assets/CharacterBehavior')/(name+'.cs.meta')
    assert not f.exists()
    template=Path('Assets/CharacterBehavior/Runtime/GroundMotionPath.cs.meta').read_bytes()
    f.write_bytes(re.sub(rb'guid: [0-9a-f]+',b'guid: '+uuid.uuid4().hex.encode(),template))
print(f'parameter-free asset: {len(layers)} layers, {len(states)} states, {len(clips)} clips, {count} imported curves')

