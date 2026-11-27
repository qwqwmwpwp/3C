from pathlib import Path
from hashlib import sha256
import json
d=Path(__file__).resolve().parent
last=d/'review-segment-baseline'
load=lambda p: {r['Path']:r for r in json.loads(p.read_bytes())}
before=load(last/'implementation-manifest.json'); after=load(d/'implementation-manifest.json')
rows=[]
for k in sorted(before.keys()|after.keys()):
 prev=before.get(k,{}).get('SHA256')
 row=after.get(k,{})
 current=row.get('SHA256')
 if prev==current: continue
 if prev: assert sha256((last/Path(k).name).read_bytes()).hexdigest().upper()==prev
 rows.append(dict(Path=k,Status='Modified' if prev else 'Added',PreviousSHA256=prev,FinalSHA256=current,Lines=row['Lines']))
p=d/'review-segment-manifest.json'
p.write_text(json.dumps(rows,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
print(len(rows), sha256(p.read_bytes()).hexdigest().upper())
for row in rows: print(row['Path'])

