from pathlib import Path
from decimal import Decimal
import hashlib,json,re,yaml
task=Path(__file__).parent.resolve(); project=task.parent.parent
p=Path('Assets/Data/Player/Shield/02 连招表.asset'); lib=Path('Assets/Data/Player/Shield/盾牌地面动作库.asset')
def load(path): return yaml.safe_load(path.read_text('utf-8').split('MonoBehaviour:\n',1)[1])
before=load(task/'baseline'/p);after=load(project/p)
raw=(project/lib).read_text('utf-8')
lines=[]
for slot in ('LightOne','LightTwo','Heavy','SkillTwo'):
 move=after['Data'][slot]; duration=Decimal(str(move.pop('AnimationDurationSeconds'))); clip=move['Clip']; match=list(re.finditer(r'^    - Clip: \{fileID: '+str(clip['fileID'])+r', guid: '+clip['guid']+',',raw,re.M))[-1]
 section=re.split(r'^    - Clip:|^  \S',raw[match.end():],maxsplit=1,flags=re.M)[0]
 curves=re.split(r'^      - Name: ',section,flags=re.M)[1:]
 ends=[max(map(Decimal,re.findall(r'^            time: (.+)$',curve,re.M))) for curve in curves if re.search(r'^        InSeconds: 1$',curve,re.M)]
 assert len(ends)==137 and set(ends)=={duration}
 fields=[move[k] for k in ('EntryBlendSeconds','ActiveSeconds','RecoverySeconds')]
 lines.append(f"{slot}: clip={clip['guid']}/{clip['fileID']}, animation={duration}, native curves={len(ends)}, existing state seconds={fields}, links={len(move['Links'])}")
assert before==after
assert (project/p.with_suffix('.asset.meta')).read_bytes()==(task/'baseline'/p.with_suffix('.asset.meta')).read_bytes()
assert (project/lib).read_bytes()==(task/'baseline'/lib).read_bytes()
lines.append('After removing the four new fields, the entire parsed asset equals the baseline, including root metadata and routing.')
(task/'migration-result.log').write_text('\n'.join(lines)+'\n',encoding='utf-8');print('\n'.join(lines))

