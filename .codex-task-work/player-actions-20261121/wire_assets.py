import json, re, uuid
from pathlib import Path
from shutil import copyfile
from fileid import xx
work=Path(__file__).parent
asset=Path('Assets/player/GameInput.inputactions')
prefab=Path('Assets/player/player 1.prefab')
driver=Path('Assets/CharacterBehavior/Runtime/CharacterBehaviorDriver.cs')
wrapper=Path('Assets/player/GameInput.cs')
for path in (asset, prefab, driver, wrapper):
    copyfile(path, work/('before-'+path.name))
assert json.loads(asset.read_text())['maps']==[]
def guid():return str(uuid.uuid4())
names=['Move','Walk','Sprint','Dash','Guard']
actions=[dict(name=name,type='Value' if name=='Move' else 'Button',id=guid(),expectedControlType='Vector2' if name=='Move' else 'Button',processors='',interactions='',initialStateCheck=name!='Dash') for name in names]
bindings=[]
def bind(name, path, action, group='', composite=False, part=False):
    bindings.append(dict(name=name,id=guid(),path=path,interactions='',processors='',groups=group,action=action,isComposite=composite,isPartOfComposite=part))
bind('Keyboard','2DVector','Move',composite=True)
for part,keys in [('up',['w','upArrow']),('down',['s','downArrow']),('left',['a','leftArrow']),('right',['d','rightArrow'])]:
    for key in keys: bind(part,'<Keyboard>/'+key,'Move','Keyboard',part=True)
bind('','<Gamepad>/leftStick','Move','Gamepad')
for action,key in zip(names[1:],['leftCtrl','r','leftShift','g']):
    bind('','<Keyboard>/'+key,action,'Keyboard')
schemes=[dict(name=device,bindingGroup=device,devices=[dict(devicePath=f'<{device}>',isOptional=False,isOR=False)]) for device in ['Keyboard','Gamepad']]
data=dict(version=0,name='GameInput',maps=[dict(name='Player',id=guid(),actions=actions,bindings=bindings)],controlSchemes=schemes)
assert xx('Type:MonoBehaviour-><root>0')==-944628639613478452
assert xx('Type:MonoBehaviour->UI/Point0')==1054132383583890850
assert xx('Type:MonoBehaviour->UI/Click0')==8056856818456041789
source=prefab.read_bytes()
eol=b'\r\n' if b'\r\n' in source else b'\n'
old=eol.join(row.encode() for row in ['  horizontalAxis: Horizontal','  verticalAxis: Vertical','  walkKey: 306','  sprintKey: 114','  dashKey: 304','  guardKey: 103'])
assert source.count(old)==1
assetguid=re.search(r'^guid: (\w+)$',Path(str(asset)+'.meta').read_text(),re.M)[1]
rows=[]
for action in actions:
    local=xx('Type:MonoBehaviour->'+action['id']+'0')
    rows.append(f"  {action['name'].lower()}Action: {{fileID: {local}, guid: {assetguid}, type: 3}}".encode())
prefab.write_bytes(source.replace(old,eol.join(rows)))
asset.write_text(json.dumps(data,indent=4)+'\n',encoding='utf-8',newline='\n')
print('Bound', len(actions), 'actions and',len(bindings),'bindings.')
