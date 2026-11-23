import re, numpy as np
from pathlib import Path
s=Path('.codex-task-work/ground-stop-settle')
def read(path):
 d=path.read_text()
 clips={}
 for block in re.split(r'^ID: ',d,flags=re.M):
  if not re.match(r'-?\d+ \(ClassID: 74\) AnimationClip',block):continue
  name=re.search(r'm_Name "([^"]+)"',block)[1]
  curves={}
  for piece in block.split('data  (FloatCurve)')[1:]:
   a=re.search(r'\battribute "([^"]+)"',piece)
   if not a:continue
   v=np.array([float(q) for q in re.findall(r'^\s+value ([-.e+\d]+) \(float\)',piece,re.M)])
   curves[a[1]]=v
  clips[name]=curves
 return clips
if __name__=='__main__':
 idle=read(s/'imported-idle.txt')['SS_Idle']; stops=read(s/'imported-stops.txt')
 for n,cr in stops.items():
  if '_stop_' not in n:continue
  print(n)
  for key in ('RootT','MotionT','RootQ','MotionQ','LeftFootT','RightFootT'):
   f=lambda i:[k[i] for attr,k in cr.items() if attr.startswith(key+'.')]
   goal=[k[0] for attr,k in idle.items() if attr.startswith(key+'.')]
   print(key,'end',np.round(f(-1),5),'goal',np.round(goal,5)) if n=='run_stop_left' else None
  diffs={a:abs(v[-1]-idle[a][0]) for a,v in cr.items() if a in idle and v.size and idle[a].size}
  print('worst scalar endpoints',[(k,round(diffs[k],4)) for k in sorted(diffs,key=diffs.get,reverse=True)[:5]])
