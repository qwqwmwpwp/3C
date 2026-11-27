from pathlib import Path
import hashlib,csv,re
task=Path('.codex-task-work/ik-separation-20261127-73ce')
anim=Path('Assets/Code/Character/Anim')
base=task/'baseline'/anim
stage=task/'stage'
renames={'CharacterPoseJob.cs':'CharacterIkJob.cs','CharacterPosePipeline.cs':'CharacterAnimationIk.cs'}
def hashdata(p):return hashlib.sha256(p.read_bytes()).hexdigest().upper() if p and p.is_file() else ''
manifest=[]
for file in sorted(stage.rglob('*.cs')):
    rel=file.relative_to(stage)
    old=Path(renames.get(rel.as_posix(),str(rel)))
    for ext in ('','.meta'):
        b=base/(str(old)+ext); p=anim/(str(rel)+ext)
        h0=hashdata(b); h1=hashdata(p)
        mode='renamed' if rel!=old else 'added' if not h0 else 'unchanged' if h1==h0 else 'modified'
        if mode=='unchanged':continue
        manifest.append((mode,(anim/(str(old)+ext)).as_posix() if h0 else '',p.as_posix(),h0,h1,len(p.read_bytes().splitlines()) if ext=='' else ''))
        if ext=='': assert file.read_text(encoding='utf8')==p.read_text(encoding='utf-8-sig'), 'Concurrent edit: '+str(p)
with (task/'changes.csv').open('w',encoding='utf-8-sig',newline='') as f:
    w=csv.writer(f);w.writerow(('Kind','Before','After','BaselineSha256','CurrentSha256','Lines')); w.writerows(manifest)
# Compare preserved solver with working baseline, excluding enclosing type.
old=(base/'CharacterIkJob.cs').read_text(encoding='utf-8-sig')
new=(anim/'CharacterIkSkeleton.cs').read_text(encoding='utf-8-sig')
def solve(s):return s[s.index('        private static void Solve('):s.rindex('    }')].rstrip()
assert solve(old)==solve(new)
rsp=(task/'compile'/'Assembly-CSharp.rsp').read_text(encoding='utf-8-sig')
assert all('"'+(anim/f.relative_to(stage)).as_posix()+'"' in rsp for f in stage.rglob('*.cs'))
assert all(('"'+(anim/old).as_posix()+'"') not in rsp for old in renames.values())
print('All 17 source paths in actual runtime RSP; old file names absent; original Solve identical.')
print('Manifest',len(manifest),'rows: ',{t:sum(1 for row in manifest if row[0]==t) for t in ['added','modified','renamed']})
oldfiles={p.relative_to(base) for p in base.rglob('*') if p.is_file()}
actual={p.relative_to(anim) for p in anim.rglob('*') if p.is_file() and (p.name.endswith('.cs') or p.name.endswith('.cs.meta'))}
ours={Path(r[2]).relative_to(anim) for r in manifest}
removed={Path(r[1]).relative_to(anim) for r in manifest if r[0]=='renamed'}
others=sorted({p for p in actual|oldfiles if p not in ours|removed and hashdata(anim/p)!=hashdata(base/p)})
print('Concurrent external changes in Anim:', ' '.join(str(p) for p in others))
