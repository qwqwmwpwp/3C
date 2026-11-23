from pathlib import Path
import json, re, uuid
from fileid import xx
root=Path('.')
work=Path(__file__).parent
def load(p):return Path(p).read_text(encoding='utf-8-sig')
def meta(p):return re.search(r'^guid: ([0-9a-f]{32})$',load(str(p)+'.meta'),re.M)[1]
def lid(id):return xx('Type:MonoBehaviour->'+id+'0')
pkg=root/'Library/PackageCache/com.unity.inputsystem@1.14.2'
# Cross-check IDs against Unity-authored action references (both ID- and name-based) in its shipped samples.
candidates={}
for p in pkg.rglob('*.inputactions'):
    candidates[meta(p)]={lid(name):kind for m in json.loads(load(p))['maps'] for a in m['actions'] for name,kind in [(a['id'],'uuid'),(m['name']+'/'+a['name'],'name')]}
known=set()
for mask in ['*.prefab','*.unity','*.asset']:
    for p in (pkg/'Samples~').rglob(mask):
        for n,g in re.findall(r'fileID: (-?\d+), guid: ([0-9a-f]{32})',load(p)):
            kind=candidates.get(g,{}).get(int(n))
            if kind:known.add((kind,g,n))
assert any(k=='uuid' for k,g,n in known), known
print('Unity sample cross-check:', {key:sum(kind==key for kind,g,n in known) for key in ['uuid','name']})
asset=root/'Assets/player/GameInput.inputactions'
guid=meta(asset)
data=json.loads(load(asset))
m,=data['maps']
assert (data['version'],m['name'])==(1,'Player')
actions={a['name']:a for a in m['actions']}
assert list(actions)==['Move','Walk','Sprint','Dash','Guard']
assert [a['type'] for a in actions.values()]==['Value']+['Button']*4
assert [a['expectedControlType'] for a in actions.values()]==['Vector2']+['Button']*4
assert actions['Dash']['initialStateCheck']==False
ids=[x['id'] for x in [m,*m['actions'],*m['bindings']]]
assert len(ids)==len({uuid.UUID(x) for x in ids})
b=m['bindings']
assert [(x['name'],x['path']) for x in b if x['isPartOfComposite']]==[(d,'<Keyboard>/'+k) for d,keys in [('up',['w','upArrow']),('down',['s','downArrow']),('left',['a','leftArrow']),('right',['d','rightArrow'])] for k in keys]
assert [(x['action'],x['path']) for x in b if not x['isPartOfComposite']]==[('Move','2DVector'),('Move','<Gamepad>/leftStick'),('Walk','<Keyboard>/leftCtrl'),('Sprint','<Keyboard>/r'),('Dash','<Keyboard>/leftShift'),('Guard','<Keyboard>/g')]
assert b[0]['isComposite'] and all(x['isPartOfComposite'] and x['action']=='Move' for x in b[1:9])
assert {g['name'] for g in data['controlSchemes']}=={x['groups'] for x in b if x['groups']}
prefab=root/'Assets/player/player 1.prefab'
driver=root/'Assets/CharacterBehavior/Runtime/CharacterBehaviorDriver.cs'
pref=load(prefab)
driverblock=next(block for block in pref.split('--- !u!') if meta(driver) in block)
code=load(driver)
for name,a in actions.items():
    field=name.lower()+'Action'
    assert re.search(r'InputActionReference '+field+r'\s*;', code)
    expected=f'{field}: {{fileID: {lid(a["id"])}, guid: {guid}, type: 3}}'
    assert driverblock.count(expected)==1,expected
before=load(work/'before-player 1.prefab')
def omit(pat,s):return re.sub(pat,'',s,flags=re.M)
assert omit(r'^  (move|walk|sprint|dash|guard)Action:.*\n',pref)==omit(r'^  (horizontalAxis|verticalAxis|walkKey|sprintKey|dashKey|guardKey):.*\n',before)
assert re.search(r'm_SourcePrefab: \{fileID: 100100000, guid: '+meta(prefab),load('Assets/Scenes/角色测试.unity'))
assert re.search(r'com.unity.input.settings.actions: \{fileID: '+str(lid('<root>'))+', guid: '+guid,load('ProjectSettings/EditorBuildSettings.asset'))
assert 'generateWrapperCode: 1' in load(str(asset)+'.meta')
assert not re.search(r'\bInput\.(GetAxisRaw|GetKey|GetKeyDown)|\bKeyCode\b',code)
assert 'activeInputHandler: 2' in load('ProjectSettings/ProjectSettings.asset')
assert len(code.splitlines())<=500
print('Verified scene -> prefab -> 5 action references -> project-wide asset, 14 bindings, 20 unique IDs.')
print('Driver:',len(code.splitlines()),'lines. Prefab change limited to input wiring.')
