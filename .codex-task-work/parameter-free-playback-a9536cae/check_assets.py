from pathlib import Path
import re,yaml,collections,math
stage=Path(__file__).parent; p=Path('Assets/player'); r=Path('Assets/CharacterBehavior/Runtime')
def read(path):return path.read_text(encoding='utf-8-sig')
def parse_ctrl(s):return {int(i):yaml.safe_load(b) for i,b in re.findall(r'(?ms)^--- !u!\d+ &(-?\d+)\n(.*?)(?=^--- !u!|\Z)',s)}
def parse_so(s):return yaml.safe_load(s.split('\n',3)[3])['MonoBehaviour']
def walk(ds):
    c=next(x['AnimatorController'] for x in ds.values() if 'AnimatorController' in x)
    states={};layers=[]
    for i,layer in enumerate(c['m_AnimatorLayers']):
        m=ds[layer['m_StateMachine']['fileID']]['AnimatorStateMachine']
        assert not m['m_ChildStateMachines']
        for item in m['m_ChildStates']:
            a=ds[item['m_State']['fileID']]['AnimatorState']
            states[layer['m_Name']+'.'+a['m_Name']]=(i,a)
        initial=ds[m['m_DefaultState']['fileID']]['AnimatorState']['m_Name']
        layers.append((layer['m_Mask'],layer['m_BlendingMode'],layer['m_DefaultWeight'],layer['m_Name']+'.'+initial))
    return c,layers,states
old=read(stage/'before/player.controller');new=read(p/'player.controller')
expected=re.sub(r'(?s)  m_AnimatorParameters:\n.*?(?=  m_AnimatorLayers:)','  m_AnimatorParameters: []\n',old)
expected=expected.replace('  m_SpeedParameterActive: 1\n','  m_SpeedParameterActive: 0\n')
expected=re.sub(r'(?m)^  m_SpeedParameter: (?:WalkRate|RunRate|SprintRate|DashRate|StopRate)$','  m_SpeedParameter:',expected)
assert new==expected
oldCtrl,oldLayers,oldStates=walk(parse_ctrl(old)); ctrl,layers,states=walk(parse_ctrl(new))
assert len(oldCtrl['m_AnimatorParameters'])==8 and ctrl['m_AnimatorParameters']==[] and oldLayers==layers
assert all(not st['m_SpeedParameter'] and not st['m_SpeedParameterActive'] and not st['m_TimeParameterActive'] and not st['m_CycleOffsetParameterActive'] and not st['m_MirrorParameterActive'] for i,st in states.values())
oldDb=parse_so(read(stage/'before/ShieldGroundMotion.asset'));db=parse_so(read(p/'ShieldGroundMotion.asset'))
directory=db.pop('Playback')
assert db==oldDb and len(db['BakedTracks'])==13
assert layers==[(l['Mask'],l['Additive'],l['Weight'],l['DefaultState']) for l in directory['Layers']]
assert {row['Path']:(row['Layer'],row['Clip'],row['Speed']) for row in directory['States']}=={name:(i,s['m_Motion'],s['m_Speed']) for name,(i,s) in states.items()}
cliprefs={tuple(sorted(s['m_Motion'].items())) for _,s in states.values()}
assert len(cliprefs)==len(directory['Clips']) and cliprefs=={tuple(sorted(x['Clip'].items())) for x in directory['Clips']}
guids={c['Clip']['guid'] for c in directory['Clips']}
sources={}
for root in [p,Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations')]:
    for f in root.rglob('*.fbx.meta'):
        raw=read(f); guid=re.search(r'(?m)^guid: (\w+)',raw)[1]
        if guid in guids:sources[guid]=yaml.safe_load(raw)['ModelImporter']
curveCount=0; keys=0
for item in directory['Clips']:
    ref=item['Clip'];model=sources[ref['guid']]
    clipName=next(row['second'] for row in model['internalIDToNameTable'] if row['first'].get(74)==ref['fileID'])
    clip=next(row for row in model['animations']['clipAnimations'] if row['name']==clipName)
    crs=clip.get('curves') or []
    assert len(crs)==len(item['Curves'])
    for row,f in zip(item['Curves'],crs):
        assert not row['InSeconds'] and row['Name']==f['name']
        a=row['Curve'];b=f['curve']
        assert all(a[d]==b[o] for d,o in [('m_Curve','curve'),('m_PreInfinity','preInfinity'),('m_PostInfinity','postInfinity'),('m_RotationOrder','rotationOrder')])
        curveCount+=1;keys+=len(a['m_Curve'])
pref=read(p/'player 1.prefab')
def field(key):return re.search(r'(?m)^  '+key+r': (.+)$',pref)[1]
assert field('motionDatabase')==field('playbackLibrary') and db['BakedController']['guid']==re.search(r'(?m)^guid: (\w+)',read(p/'player.controller.meta'))[1]
speeds={field(key):rate for key,rate in [('walkStateName','WalkRate'),('walkStartStateName','WalkRate'),('moveStateName','RunRate'),('runStartStateName','RunRate'),('sprintStateName','SprintRate'),('dashStateName','DashRate')]}
for t in db['BakedTracks']:
    if t['Use']==4:
        for name in t['States']:speeds[name]='StopRate'
    if t['Loop']:assert t['ScalesWithSpeed']==1 and t['ReferenceSpeed']>0
assert speeds=={name:s['m_SpeedParameter'] for name,(_,s) in oldStates.items() if s['m_SpeedParameterActive']}
print('Controller: 8 -> 0 parameters; all 27 state assignments, 3 layer masks/defaults, 12 speed bindings, 13 matching tracks and prefab references verified.')
print('Source clips:',len(cliprefs),'permissions:',curveCount,'identical source keys/slopes:',keys)
# Current normal entry points and lifecycle ordering.
code=read(r/'CharacterAnimationPipeline.cs')
for a,b in [('player.Apply(plan, deltaTime)','Ik.Prepare(selection, motion, plan)'),('Ik.Prepare(selection, motion, plan)','clips.Evaluate()'),('Ik.Dispose();','player.Release();')]:
    assert code.index(a)<code.index(b)
assert 'if (playback.Available) return playback.Apply' in read(r/'CharacterAnimationPlayer.cs')
assert 'sharedGraph ? null : animator.parameters' in read(r/'CharacterAnimationIk.cs')
assert 'job.SetInputWeight(0, 1f)' in read(r/'CharacterClipPlayback.cs')

