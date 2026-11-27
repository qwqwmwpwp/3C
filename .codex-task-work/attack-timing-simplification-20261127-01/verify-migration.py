from pathlib import Path
from decimal import Decimal
import yaml, re, hashlib, struct
project=Path.cwd()
task=Path(__file__).parent.resolve()
path=Path('Assets/Data/Player/Shield/02 连招表.asset')
mapping=Path('Assets/Data/Player/Shield/Modules/连招攻击.asset')
library=Path('Assets/Data/Player/Shield/盾牌地面动作库.asset')
def asset(p):
 return yaml.safe_load(p.read_text('utf-8').split('MonoBehaviour:\n',1)[1])
def f32(x):
 return struct.unpack('f',struct.pack('f',float(x)))[0]
old=asset(task/'baseline'/path); new=asset(path); olddata=old.pop('Data');newdata=new.pop('Data');assert old==new
oldmap=asset(task/'baseline'/mapping);newmap=asset(mapping)
for row in oldmap['mappings']: row['AttackStateId']=row.pop('AttackMove')
assert oldmap==newmap
text=library.read_text('utf-8')
rows={r['AttackStateId']:r for r in oldmap['mappings']}
acts={a['Key']:a for a in oldmap['actions']}
def links(rows):
 return [{k:v for k,v in r.items() if k not in ('BlendInEnd','BlendOutStart')} for r in rows]
assert links(olddata.pop('Openers'))==newdata.pop('Openers')
olddata.pop('OpeningStepDistance')
report=[]
for id,field in enumerate(('LightOne','LightTwo','Heavy','SkillTwo'),1):
 prior=olddata.pop(field); current=newdata.pop(field)
 clip=prior.pop('Clip'); assert clip==current.pop('Clip')
 guid=clip['guid']; src=acts[rows[id]['EntryAction']]['StateName']
 decl=re.search(r'^    - Path: '+re.escape(src)+r'\n(.*?)(?=^    - Path:|^    Clips:)',text,re.M|re.S)
 assert decl and guid in decl[1] and str(clip['fileID']) in decl[1]
 m=list(re.finditer(r'^    - Clip: \{fileID: '+str(clip['fileID'])+r', guid: '+guid+r',',text,re.M))[-1]
 section=re.split(r'^    - Clip:|^  \S',text[m.end():],maxsplit=1,flags=re.M)[0]
 curves=re.split(r'^      - Name: ',section,flags=re.M)[1:]
 times=[max(map(Decimal,re.findall(r'^            time: (.+)$',curve,re.M))) for curve in curves if re.search(r'^        InSeconds: 1$',curve,re.M)]
 assert len(times)==137 and len(set(times))==1
 duration=times[0]; phases=[Decimal(str(prior.pop('StrikeStart'))),Decimal(str(prior.pop('RecoveryStart')))]
 a,b,c=(Decimal(str(current.pop(k))) for k in ('EntryBlendSeconds','ActiveSeconds','RecoverySeconds'))
 assert [a,b,c]==[phases[0]*duration,(phases[1]-phases[0])*duration,(1-phases[1])*duration]
 assert abs(f32(f32(f32(a)+f32(b))+f32(c))-f32(duration))<=3e-7
 oldlinks=prior.pop('Links'); newlinks=current.pop('Links');assert links(oldlinks)==newlinks
 assert current.pop('Name')==prior.pop('Name')
 cd=prior.pop('CooldownSeconds')
 if id==4: assert current.pop('CooldownSeconds')==cd==3
 else: assert cd==0
 assert not current
 assert set(prior)=={'RecoveryMoveBlendSeconds','StartupTurnSpeed','Braking','StrikeTravel','Alignment'}
 row=f'{id} {field}: {guid}/{clip["fileID"]}, {src}, {len(times)} native curves at T={duration}, A/B/C={a}/{b}/{c}, links={len(newlinks)}, cooldown={cd}'
 report.append(row)
assert olddata==newdata
for p in (path,mapping,library):
 meta=p.with_suffix('.asset.meta')
 assert meta.read_bytes()==(task/'baseline'/meta).read_bytes()
report.append('Two openers, four ordered links, windows, early inputs, names, clip identities, interrupt mask and skill cooldown agree with the working tree baseline.')
(task/'migration-result.log').write_text('\n'.join(report)+'\n',encoding='utf-8')
print('\n'.join(report))

