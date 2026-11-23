from pathlib import Path
import sys, os, json
sys.path.insert(0,os.path.abspath('.codex-task-work/ground-stop'))
from fbx_pose import *
stage=Path('.codex-task-work/ground-selection-20261121')
template=Clip('Run')
root=template.pose(0)['root']
source=Path('.codex-task-work/ground-stop-settle/author.py').read_text(encoding='utf8')
exec(source[source.index('def set_world_rotation'):source.index('def sample_stop')])
poses=[]
segments=[]
for direction,sign in [('left',-1),('right',1)]:
 for lead,offset in [('l',0),('r',10)]:
  first=len(poses)
  for i in range(25):
   pose=template.pose((i+offset)%20)
   pose['root']=root
   w=template.world(pose)
   u=i/24
   load=math.sin(math.pi*u)**1.35
   hip=pose['pelvis']
   # Shift weight into the arc, soften the knee and bank the pelvis toward the turn.
   hip[0].x+=sign*3.6*load
   hip[0].y-=4.2*load
   set_world_rotation(template,pose,'pelvis',Quaternion((0,0,1),-sign*.14*load) @ Quaternion((0,1,0),sign*.12*load) @ w['pelvis'][1],template.world(pose))
   for side in ('l','r'):
    bone='foot_'+side
    ankle,rot=w[bone]
    target=ankle.copy()
    # Preserve support width and curve the swing foot progressively into the new heading.
    phase=((i+offset+(0 if side=='l' else 10))%20)/20
    swing=math.sin(math.pi*min(1,max(0,(phase-.33)/.58)))**2
    target.x+=sign*(9*swing-2)*load
    target.z-=4*load*(1-swing)
    leg(template,pose,side,target,Quaternion((0,1,0),sign*.15*load*swing) @ rot)
   poses.append(pose)
  segments.append(('run_turn_'+direction+'_'+lead,first,len(poses)-1))
export=source[source.index('end=(len(poses)-1)*tick'):]
exec(export.replace('LocomotionStops','LocomotionTurns'))

