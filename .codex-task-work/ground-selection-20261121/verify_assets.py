from pathlib import Path
import math,yaml,re,json,collections
import numpy as np
P=Path('Assets/player')
S=Path('.codex-task-work/ground-selection-20261121')
def doc(f):return yaml.safe_load(f.read_text().split('\n',3)[3])['MonoBehaviour']
db=doc(P/'ShieldGroundMotion.asset');before=doc(S/'before/ShieldGroundMotion.asset')
assert db['Format']==1 and db['Avatar']==db['BakedAvatar'] and db['Controller']==db['BakedController']
assert len(db['BakedTracks'])==len(db['Tracks'])==13
def st(t):return t['States'][0].split('.')[-1]
tracks={st(t):t for t in db['BakedTracks']}
for t in before['Tracks']:
    n=tracks[st(t)]
    assert all(t[k]==n[k] for k in t),st(t)
ctrl=(P/'player.controller').read_text()
docs={int(id):yaml.safe_load(block) for id,block in re.findall(r'(?ms)^--- !u!\d+ &(-?\d+)\n(.*?)(?=^--- !u!|\Z)',ctrl)}
statemachine=next(d['AnimatorStateMachine'] for d in docs.values() if 'AnimatorStateMachine' in d and d['AnimatorStateMachine']['m_Name']=='Base Layer')
bindings={docs[i['m_State']['fileID']]['AnimatorState']['m_Name']:docs[i['m_State']['fileID']]['AnimatorState'] for i in statemachine['m_ChildStates']}
normal=Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal')
models=[*P.glob('Locomotion*.fbx.meta'), *[normal/('SS_'+n+'.fbx.meta') for n in ('Run','Walk','Sprint')]]
meta={m['guid']:m['ModelImporter'] for f in models for m in [yaml.safe_load(f.read_text())]}
def v(a):return np.array([float(x) for x in a.values()])
for a,t in zip(db['Tracks'],db['BakedTracks']):
    assert all(t[k]==val for k,val in a.items())
    assert all(bindings[x[11:]]['m_Motion']==t['Clip'] for x in t['States'])
    m=meta[t['Clip']['guid']]
    name=next(x['second'] for x in m['internalIDToNameTable'] if x['first'][74]==t['Clip']['fileID'])
    clip=next(x for x in m['animations']['clipAnimations'] if x['name']==name)
    ps=t['Poses'];assert len(ps)==clip['lastFrame']-clip['firstFrame']+1
    assert abs((len(ps)-1)/30-t['Seconds'])<1e-5
    assert t['Loop']==clip['loopTime']
    for state in t['States']:
        binding=bindings[state[11:]]
        assert binding['m_Speed']==1 and not binding['m_Mirror'] and binding['m_CycleOffset']==0
        assert not any(binding[k] for k in ('m_MirrorParameterActive','m_TimeParameterActive','m_CycleOffsetParameterActive'))
        active=binding['m_SpeedParameterActive']
        assert active==(t.get('ScalesWithSpeed',0) or t['Use']==4)
        if t['Use']==4: assert binding['m_SpeedParameter']=='StopRate'

    assert set(x['name'] for x in clip['curves'])>={'IKLeftFoot','IKRightFoot'}
    for pose in ps:
        assert all(np.isfinite(v(row)).all() for row in pose.values())
        assert np.all((v(pose['Contact'])>=0)&(v(pose['Contact'])<=1))
    if t['Use']==3:
        assert t['LastEntrySeconds']+.3<=t['Seconds'] and t['ExitSeconds']>t['LastEntrySeconds']
        assert t['ReferencePath'][-1]['Seconds']>=t['Seconds']
        assert t['Loop']==0 and clip['events']==[]
        assert t['Side']==(1 if 'left' in name else 2)
        assert np.linalg.norm(v(ps[0]['LeftVelocity'])-(v(ps[1]['Left'])-v(ps[0]['Left']))*30)<1e-4
# Static separability of the shipped database at the expected first solved step of a lateral query.
def rotate(v,deg):
    a=math.radians(deg);return np.array([math.cos(a)*v[0]+math.sin(a)*v[1],-math.sin(a)*v[0]+math.cos(a)*v[1]])
def features(tr,i,near,far,speed):
    if tr['Use']==1:return [(np.array([0,speed*h]),0) for h in (near,far)]
    keys=tr['ReferencePath']; ts=[k['Seconds'] for k in keys];t=i/30
    scalar=lambda t,vs:np.interp(t,ts,vs)
    def at(t):return np.array([scalar(t,[k['Position'][a] for k in keys]) for a in ('x','y')]),scalar(t,[k['Yaw'] for k in keys])
    p,y=at(t)
    return [(rotate(q-p,-y),a-y) for h in (near,far) for q,a in [at(t+h)]]
results={}
for side,sgn in [('left',-1),('right',1)]:
    target=np.array([sgn*5.,0]);start=np.array([0.,5.]);d=target-start
    current=start+d/np.linalg.norm(d)*.8; speed=np.linalg.norm(current)
    def forecast(h):
        d=target-current;change=min(h,np.linalg.norm(d)/40)
        return rotate((current+current+d/np.linalg.norm(d)*change*40)*.5*change+target*(h-change),-sgn*12)
    goal=[(forecast(h),sgn*min(78,600*h)) for h in (.15,.30)]
    def cost(tr,i,p):
        q=tr['Poses'][i]
        a,b=v(p['Contact']); c,d=v(q['Contact'])
        if (a>=.7 and b<.45 and d>=.7 and c<.45) or (b>=.7 and a<.45 and c>=.7 and d<.45):return math.inf
        rate=speed/tr['ReferenceSpeed'] if tr['Loop'] else 1
        score=sum(np.linalg.norm(v(q[a])*r-v(p[a]))**2/div for a,r,div in [('Left',1,.2),('Right',1,.2),('LeftVelocity',rate,40),('RightVelocity',rate,40),('Contact',1,1)])
        for (a,b),(g,y) in zip(features(tr,i,.15,.30,speed),goal):
            score+=np.linalg.norm(a-g)**2/.25+((b-y)/45)**2
        return score
    wins=collections.Counter()
    for i,p in enumerate(tracks['move']['Poses'][:-1]):
        best=min((cost(tr,k,p),name) for name,tr in tracks.items() if name.startswith('run_turn_'+side) for k in range(4))
        wins[best[1] if best[0]+.12<cost(tracks['move'],i,p) else 'continue']+=1
    assert all(wins['run_turn_'+side+'_'+f]>0 for f in ('l','r')),wins
    results[side]=dict(wins)
print('13 candidate bindings; 4 legacy tracks identical; all 6 stops, four one-shots, contact keys and endpoint velocities validated.')
print('20 discrete source phases per direction, STATIC candidate comparison:',results)

