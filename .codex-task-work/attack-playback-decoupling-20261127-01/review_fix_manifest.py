from pathlib import Path
import json,hashlib,re
task=Path(__file__).resolve().parent
def read(name): return {r['Path']:r for r in json.loads((task/name).read_bytes())}
base=read('baseline-manifest.json')
initial=read('initial-review-manifest.json')
current=read('implementation-manifest.json')
def get(rows,path): return rows.get(path,base.get(path,{})).get('SHA256')
out=[]
for k in sorted(initial.keys()|current.keys()):
 a=get(initial,k); b=get(current,k)
 if a!=b:
  out.append({'Path':k,'Status':('Modified' if a and b else 'Added' if b else 'Removed'),'InitialReviewSHA256':a,'FinalSHA256':b,'Lines':current.get(k,{}).get('Lines')})
name='review-fix-manifest.json';p=task/name
p.write_text(json.dumps(out,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
for row in out: print(row['Status'],row['Path'],row['Lines'])
for f in [name,'implementation-manifest.json','initial-review-manifest.json','baseline-manifest.json','compile-result.log']:
 print(f,hashlib.sha256((task/f).read_bytes()).hexdigest().upper())

