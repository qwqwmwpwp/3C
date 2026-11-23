from verify import *
import math
runtime=root/'Assets/CharacterBehavior/Runtime'
names='CharacterBehavior CharacterBehaviorFlow CharacterBehaviorRules CharacterBehaviorFrameResult CharacterAttackCombo CharacterActionHandoff CharacterLightAttackBehavior CharacterMotorSettings CharacterDashBehavior CharacterGuardBehavior CharacterInputSnapshot CharacterMoveBehavior'.split()
def sources(old):
    for n in names:
        b=work/'before'/(n+'.cs')
        yield b if old and b.exists() else runtime/(n+'.cs')
rows=[]
for old in [True,False]:
    name='AttackBefore' if old else 'AttackAfter'
    harness(name,[*sources(old),work/'AttackRegression.cs'],[work/'Assembly-CSharp.dll'],['-nowarn:0436,0649'])
    raw=run(name,[mono,str(work/(name+'.exe'))], show=False)
    lines=raw.decode('utf-8-sig').replace('\ufeff','').splitlines()
    rows.append([line.split('|') for line in lines if '|' in line])
    print(lines[-1])
assert len(rows[0])==len(rows[1]) and len(rows[0])>10000
max_diff=0
for i,(a,b) in enumerate(zip(*rows)):
    assert len(a)==len(b)==41
    for j,(v,w) in enumerate(zip(a,b)):
        if v==w: continue
        # Only motion floats tolerate rounding; events, IDs, grants and rejection reasons must be exact.
        if j not in {*range(17,28),32,33,34,35,36,39,40}: raise ValueError((i,j,v,w))
        diff=abs(float(v)-float(w))
        if not math.isfinite(diff) or diff>1e-5: raise ValueError((i,j,v,w))
        max_diff=max(max_diff,diff)
print('Matched',len(rows[0]),'frames x 41 fields; largest numeric difference:',max_diff)

