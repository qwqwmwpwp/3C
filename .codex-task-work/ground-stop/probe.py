import os,sys
sys.path.insert(0,os.path.abspath('.codex-task-work/ground-stop'))
from fbx_pose import *
for name in ['Idle','Walk','Run','Sprint']:
 a=Clip(name)
 print(name,'len',a.length,'roots',[(n,a.parents[n],a.trs(n,b'Lcl Scaling',0)[:]) for n in a.models if not a.parents[n] or n in ('root','pelvis')])
 for t in ([0] if name=='Idle' else [0,0.05,.22,.47,.64,.8]):
  pose=a.pose(t*a.length); w=a.world(pose)
  print(round(t,2),{n:tuple(round(v,1) for v in w[n][0]-w['root'][0]) for n in ('pelvis','foot_l','foot_r')},'root',w['root'][0][:])
