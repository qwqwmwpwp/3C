from pathlib import Path
from hashlib import sha256
from collections import Counter
import json
task=Path(__file__).resolve().parent; root=task.parent.parent
old={entry['Path']:entry for entry in json.loads((task/'baseline-manifest.json').read_bytes())}
current={p.relative_to(root).as_posix():p for base in ['Assets/Code/Character','Assets/Data/Player/Shield'] for p in (root/base).rglob('*') if p.is_file()}
def digest(path): return sha256(path.read_bytes()).hexdigest().upper()
rows=[]
for key in sorted(set(old)|set(current)):
 before=old[key]['SHA256'] if key in old else None
 if before: assert digest(task/'baseline'/key)==before
 after=digest(current[key]) if key in current else None
 if before==after: continue
 status='Modified' if before and after else 'Added' if after else 'Removed'
 count=len(current[key].read_text('utf-8-sig').splitlines()) if key.endswith('.cs') and after else None
 if count is not None: assert count<=500
 if not after: assert digest(task/'retired'/key)==before
 rows.append(dict(Path=key,Status=status,BaselineSHA256=before,SHA256=after,Lines=count))
(task/'implementation-manifest.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
counts=Counter(row['Status'] for row in rows)
logs=[]
for asm,editor in [('Assembly-CSharp',False),('Assembly-CSharp-Editor',True)]:
 out=task/'compile'
 rsp=(out/(asm+'.rsp')).read_text('utf-8')
 paths={l.strip('"') for l in rsp.splitlines() if l.startswith('"Assets/Code/Character/') and l.endswith('.cs"')}
 sources={key for key in current if key.startswith('Assets/Code/Character/') and key.endswith('.cs') and ('/Editor/' in key)==editor}
 assert paths==sources
 last=max(current[key].stat().st_mtime_ns for key in paths)
 assert last <= (out/(asm+'.dll')).stat().st_mtime_ns
 logs.append(f'{asm} sources exactly match {len(paths)} current sources; DLL newer than all included .cs files.')
logs+=['No source or resource edits after the successful compile.','Changed files: '+str(counts),'Maximum changed source lines: '+str(max(r['Lines'] or 0 for r in rows))]
(task/'static-result.log').write_text('\n'.join(logs)+'\n',encoding='utf-8');print('\n'.join(logs))

