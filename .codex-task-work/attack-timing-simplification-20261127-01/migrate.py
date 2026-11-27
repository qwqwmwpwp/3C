from pathlib import Path
import re
from decimal import Decimal
task=Path(__file__).parent
asset=Path('Assets/Data/Player/Shield/02 连招表.asset')
src=(task/'baseline'/asset).read_text('utf-8')
durations=dict(zip(('LightOne','LightTwo','Heavy','SkillTwo'),map(Decimal,('1.5000001','2.1000001','1.8333334','2.1333334'))))
s=re.sub(r'^    OpeningStepDistance:.*\n|^ +Blend(?:InEnd|OutStart):.*\n','',src,flags=re.M)
chunks=re.split(r'^    (LightOne|LightTwo|Heavy|SkillTwo):\n',s,flags=re.M)
out=chunks[0]
for slot,chunk in zip(chunks[1::2],chunks[2::2]):
 duration=durations[slot]
 strike=Decimal(re.search(r'^      StrikeStart: (.+)$',chunk,re.M)[1]);rec=Decimal(re.search(r'^      RecoveryStart: (.+)$',chunk,re.M)[1])
 times=[duration*strike,duration*(rec-strike),duration*(1-rec)]
 cooldown=re.search(r'^      CooldownSeconds: (.+)$',chunk,re.M)[1]
 out+='    '+slot+':\n'+chunk[:chunk.index('      StrikeStart:')]+''.join('      '+field+': '+str(time)+'\n' for field,time in zip(('EntryBlendSeconds','ActiveSeconds','RecoverySeconds'),times))
 if slot=='SkillTwo': out+='      CooldownSeconds: '+cooldown+'\n'
 print(slot,duration,*times)
def save(path,content):
 crlf=b'\r\n' in path.read_bytes()
 path.write_bytes(content.replace('\n','\r\n' if crlf else '\n').encode('utf-8'))
save(asset,out)
module=Path('Assets/Data/Player/Shield/Modules/连招攻击.asset');s=module.read_text('utf-8')
assert s.count('    AttackMove:')==4
save(module,s.replace('    AttackMove:', '    AttackStateId:'))

