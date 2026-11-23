import os,sys,json
sys.path.insert(0,os.path.abspath('.codex-task-work/ground-stop'))
from fbx_pose import *
from mathutils import Matrix
idle=Clip('Idle')
template=Clip('Run')
rest=idle.pose(0)
dest=Path('Assets/player/LocomotionStops.fbx')
def mix(a,b,w): return {n:[p.lerp(b[n][0],w),q.slerp(b[n][1],w)] for n,(p,q) in a.items()}
def set_world_rotation(c,pose,n,desired,w):
 parent=c.parents[n]
 pose[n][1]=w[parent][1].inverted() @ desired if parent else desired
def aim(c,pose,n,src,dst,w):
 d=src.rotation_difference(dst)
 set_world_rotation(c,pose,n,d @ w[n][1],w)
def leg(c,pose,side,target,ankle):
 thigh,calf,foot=['%s_%s'%(x,side) for x in ('thigh','calf','foot')]
 w=c.world(pose)
 a,b,f=(w[n][0] for n in (thigh,calf,foot))
 axis=(target-a).normalized()
 # Preserve the authored forward bend while solving bone lengths.
 bend=b-a-axis*(b-a).dot(axis)
 if bend.length<1e-4: bend=Vector((0,0,1))-axis*axis.z
 upper,lower=(b-a).length,(f-b).length
 d=max(abs(upper-lower)+.01,min(upper+lower-.01,(target-a).length))
 along=(upper**2-lower**2+d**2)/(2*d)
 joint=a+axis*along+bend.normalized()*math.sqrt(max(0,upper**2-along**2))
 aim(c,pose,thigh,b-a,joint-a,w)
 w=c.world(pose)
 aim(c,pose,calf,w[foot][0]-w[calf][0],a+axis*d-w[calf][0],w)
 set_world_rotation(c,pose,foot,ankle,c.world(pose))
def sample_stop(c,support,frames,gait):
 W={'Walk':(.57,6.5,1.8,4.0,.115), 'Run':(.61,34.2,3.2,6.0,.16), 'Sprint':(.72,40.9,4.2,8.0,.16)}
 strength,stop_dist,compression,lift,brake=W[gait]
 contact={'Walk':(16,33),'Run':(1,10),'Sprint':(0,9)}[gait][support=='r']
 source=c.pose(contact)
 source['root']=rest['root']
 start=mix(rest,source,strength)
 restworld=c.world(rest)
 startworld=c.world(start)
 swing='r' if support=='l' else 'l'
 result=[]
 for frame in range(frames+1):
  u=frame/frames
  b=smooth(u/.90)
  pose=mix(start,rest,b)
  # Brief knee absorption and lateral weight shift precede swing foot touchdown.
  absorb=math.sin(math.pi*min(1,u/.72))**2
  pose['pelvis'][0].y-=compression*absorb
  pose['pelvis'][0].x+=(1 if support=='l' else -1)*compression*.56*absorb
  travel=stop_dist*(1-(1-min(1,frame/30/brake))**2)
  for side in (support,swing):
   foot='foot_'+side
   a,aq=startworld[foot]; z,zq=restworld[foot]
   if side==support:
    planted=a.lerp(z+Vector((0,0,stop_dist)),.50)
    planted.y=z.y
    target=planted-Vector((0,0,travel))
    # The first foot can unload only after the other foot has accepted the weight.
    settle=smooth((u-.68)/.29)
    heel_lift=min(5.0, 1.5+(z-target).length*.17)
    target=target.lerp(z,settle)
    target.y+=heel_lift*math.sin(math.pi*settle)
    q=aq.slerp(zq,smooth(u))
   else:
    land=smooth((u-.025)/.64)
    target=a.lerp(z,land)
    target.y+=lift*math.sin(math.pi*land)
    q=aq.slerp(zq,land)
   leg(c,pose,side,target,q)
  # Match idle exactly on the final keys, including its asymmetric weapon stance.
  if frame==frames: pose=rest
  result.append(pose)
 return result
segments=[]; poses=[]
for g,frames in [('Walk',11),('Run',13),('Sprint',15)]:
 c=Clip(g)
 assert c.parents==template.parents and c.parents==idle.parents
 for side in ('l','r'):
  n=g.lower()+'_stop_'+('left' if side=='l' else 'right')
  first=len(poses)
  poses+=sample_stop(c,side,frames,g)
  segments.append((n,first,len(poses)-1))
  print(n,first,len(poses)-1)
# Keep the original FBX hierarchy, bind pose, axes and object IDs. All six Unity clips
# are disjoint slices of one take. No gameplay root displacement is encoded.
end=(len(poses)-1)*tick
channels={}
for n in template.models:
 angles=None
 for pose in poses:
  p,q=pose[n]
  local=template.pre[n].inverted() @ q @ template.post[n].inverted()
  angles=local.to_euler('XYZ',angles) if angles is not None else local.to_euler('XYZ')
  for ch,vals in [(b'Lcl Translation',p),(b'Lcl Rotation',[math.degrees(v) for v in angles]),(b'Lcl Scaling',(1,1,1))]:
   for a,v in zip((b'X',b'Y',b'Z'),vals):
    channels.setdefault((n,ch,a),[]).append(v)
for key,node in template.curves.items():
 vals=channels[key]
 variable=(max(vals)-min(vals)>1e-5)
 times=[tick*i for i in range(len(poses))] if variable else [0,end]
 vals=vals if variable else [vals[0],vals[0]]
 sub(node,b'Default')[0]=float(vals[0])
 for field,code,vs in [(b'KeyTime','q',times),(b'KeyValueFloat','f',vals),(b'KeyAttrFlags','i',[4]),(b'KeyAttrDataFloat','f',[0,0,0,0]),(b'KeyAttrRefCount','i',[len(times)])]:
  sub(node,field)[0]=array.array(code,vs)
def times(n):
 if n.id==b'P':
  if n.props[0] in (b'LocalStop',b'ReferenceStop',b'TimeSpanStop'): n.props[-1]=end
  if n.props[0]==b'ActiveAnimStackName': n.props[-1]=b'LocomotionStops'
 if n.id in (b'LocalTime',b'ReferenceTime'): n.props[-1]=end
 if n.id==b'Take': n.props[0]=b'LocomotionStops'
 if n.id==b'FileName' and n.props[0]==b'SS_Run.tak': n.props[0]=b'LocomotionStops.tak'
 if n.id==b'AnimationStack': n.props[1]=b'LocomotionStops\0\1AnimStack'
 for ch in n.elems: times(ch)
times(template.root)
r=save_node(template.root)
encode_bin.write(str(stage/'LocomotionStops.fbx'),r,template.ver)
(stage/'ranges.json').write_text(json.dumps(segments))
