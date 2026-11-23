import json, re
from pathlib import Path
from fileid import xx
p=Path('Assets/player/GameInput.inputactions')
d=json.loads(p.read_text())
w=Path('Assets/player/GameInput.cs')
s=w.read_text(encoding='utf-8-sig')
generated=json.loads(re.search(r'FromJson\(@"(.*?)"\);',s,re.S)[1].replace('""','"'))
assert d==generated, [(k,d[k]==generated[k]) for k in d]
assert all('m_Player_'+a['name']+' = m_Player.FindAction(' in s for a in d['maps'][0]['actions'])
ids=[a['id'].encode() for a in d['maps'][0]['actions']]
local_ids=[xx('Type:MonoBehaviour->'+i.decode()+'0').to_bytes(8,'little',signed=True) for i in ids]
hits=[]
for f in Path('Library/Artifacts').glob('*/*'):
    stat=f.stat()
    if stat.st_mtime < p.stat().st_mtime-120 or not (3000<stat.st_size<2000000): continue
    content=f.read_bytes()
    if all(i in content for i in ids) and all(n in content for n in local_ids): hits.append(f)
print('Generated wrapper matches the entire action asset. Physical lines:',len(s.splitlines()))
print('Imported binary assets with all five action UUIDs and matching file IDs:',*[f.as_posix() for f in hits])
print('Compiled assembly newer than wrapper and driver:',Path('.codex-task-work/player-actions-20261121/Assembly-CSharp.dll').stat().st_mtime>max(w.stat().st_mtime,Path('Assets/CharacterBehavior/Runtime/CharacterBehaviorDriver.cs').stat().st_mtime))
assert hits
