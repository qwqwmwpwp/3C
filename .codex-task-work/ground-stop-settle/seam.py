import re, json, numpy as np, math
from pathlib import Path
stage=Path('.codex-task-work/ground-stop-settle')
def read(p):
 clips={}
 for block in re.split(r'^ID: ',p.read_text(),flags=re.M):
  if not re.match(r'-?\d+ \(ClassID: 74\) AnimationClip',block): continue
  c={}
  for curve in block.split('data  (FloatCurve)')[1:]:
   prev=curve.split('attribute "',1)
   if len(prev)!=2:continue
   keys=re.findall(r'\btime (\S+) \(float\)\s+value (\S+) \(float\)\s+inSlope (\S+) \(float\)\s+outSlope (\S+)',prev[0])
   c[prev[1].split('"',1)[0]]=np.array(keys,dtype=float)
  clips[re.search(r'm_Name "([^"]+)"',block)[1]]=c
 return clips
cs=read(stage/'imported-stops.txt')
idle=read(stage/'imported-idle.txt')['SS_Idle']
names=[n for n,_,_ in json.loads((stage/'ranges.json').read_text())]
for i,n in enumerate(names):
 c=cs[n]
 qcurves=[c['RootQ.'+a] for a in 'xyzw']
 def hermite(k,u):
  t0,y0,_,s0=k[-2];t1,y1,s1,_=k[-1]
  return (2*u**3-3*u**2+1)*y0+(u**3-2*u**2+u)*(t1-t0)*s0+(-2*u**3+3*u**2)*y1+(u**3-u**2)*(t1-t0)*s1
 def angle(q,b):
  return np.rad2deg(2*np.arccos(min(1,abs(np.dot(q,b)/np.linalg.norm(q)/np.linalg.norm(b)))))
 end=[k[-1,1] for k in qcurves]
 angles=[angle(np.array([hermite(k,u) for k in qcurves]),end) for u in np.linspace(0,1,65)]
 k=c['RootQ.y']
 target=cs[names[i+1]]['RootQ.y'][0,1] if i<len(names)-1 else None
 expected=(target-k[-2,1])*15 if target!=None else None
 print(n,'last two key degrees',round(angles[0],3),'last interval peak deg',round(max(angles),3),'out tangent',round(k[-1,2],4),'central tangent from NEXT clip',expected and round(expected,4))
