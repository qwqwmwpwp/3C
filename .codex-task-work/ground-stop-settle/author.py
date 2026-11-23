import os,sys,json
sys.path.insert(0,os.path.abspath('.codex-task-work/ground-stop'))
from fbx_pose import *
from mathutils import Matrix
stage=Path('.codex-task-work/ground-stop-settle')
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
 animated_axis=(f-a).normalized()
 animated_bend=b-a-animated_axis*(b-a).dot(animated_axis)
 assert animated_bend.length > 0.02
 bend=animated_axis.rotation_difference(axis) @ animated_bend
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
 hip,knee,ankle=[n+'_'+support for n in ('thigh','calf','foot')]
 h0,k0,f0=(startworld[n][0] for n in (hip,knee,ankle))
 upper,lower=(h0-k0).length,(k0-f0).length
 neutral=restworld[ankle][0]+Vector((0,0,stop_dist))
 offset=f0-neutral
 offset.y=0
 planted=neutral+offset*min(.5,7/max(.001,offset.length))
 def knee_angle(distance):
  return math.acos(max(-1,min(1,(distance**2-upper**2-lower**2)/(2*upper*lower))))
 entry=knee_angle((planted-h0).length)
 end_bend=knee_angle((restworld[ankle][0]-restworld[hip][0]).length)
 peak=max(entry,math.radians({'Walk':34,'Run':45,'Sprint':60}[gait]))
 result=[]
 prepared=[]
 for frame in range(frames+1):
  u=frame/frames
  x=min(1,u/.93)
  b=.35*smooth(u/.9)+.65*(1-(1-x)**4*(1+4*x))
  pose=mix(start,rest,b)
  # Brief knee absorption and lateral weight shift precede swing foot touchdown.
  absorb=math.sin(math.pi*min(1,u/.72))**2
  pose['pelvis'][0].y-=compression*absorb
  pose['pelvis'][0].x+=(1 if support=='l' else -1)*compression*.56*absorb
  travel=stop_dist*(1-(1-min(1,frame/30/brake))**2)
  targets={}
  height_caps=[]
  for side in (support,swing):
   foot='foot_'+side
   a,aq=startworld[foot]; z,zq=restworld[foot]
   if side==support:
    target=planted-Vector((0,0,travel))
    # The first foot can unload only after the other foot has accepted the weight.
    settle=smooth((u-.68)/.29)
    heel_lift=min(.8, .12+(z-target).length*.055)
    target=target.lerp(z,settle)
    target.y+=heel_lift*math.sin(math.pi*settle)
    q=aq.slerp(zq,smooth(u))
    # Choose pelvis height from the planted leg. Absorb once, then extend
    # continuously as the other foot takes the load (centimetres in this rig).
    bend=(entry+(peak-entry)*smooth(u/.26) if u<.26 else
          peak+(end_bend-peak)*smooth((u-.36)/.62))
    reach_sq=upper**2+lower**2+2*upper*lower*math.cos(bend)
    current_hip=c.world(pose)[hip][0]
    horizontal=(current_hip.x-target.x)**2+(current_hip.z-target.z)**2
    height_caps.append(target.y+math.sqrt(max(0,reach_sq-horizontal))-current_hip.y)
   else:
    land=smooth((u-.025)/.64)
    target=a.lerp(z,land)
    target.y+=lift*math.sin(math.pi*land)
    q=aq.slerp(zq,land)
    # After touchdown both heels share the same pelvis. Reserve bend in the
    # receiving knee, easing into the actual asymmetric idle angle.
    h2,k2,f2=[n+'_'+side for n in ('thigh','calf','foot')]
    hip2=c.world(pose)[h2][0]
    up2=(restworld[h2][0]-restworld[k2][0]).length
    lo2=(restworld[k2][0]-restworld[f2][0]).length
    idle_angle=math.pi-(restworld[h2][0]-restworld[k2][0]).angle(restworld[f2][0]-restworld[k2][0])
    min_angle=math.radians(19)+(idle_angle-math.radians(19))*smooth((u-.64)/.34)
    d2=up2**2+lo2**2+2*up2*lo2*math.cos(min_angle)
    if u>=.5:
     height_caps.append(target.y+math.sqrt(max(0,d2-(hip2.x-target.x)**2-(hip2.z-target.z)**2))-hip2.y)
   targets[side]=(target,q)
  pose['pelvis'][0].y+=min(height_caps)*smooth((u-.13)/.20)
  prepared.append((pose,targets))
 # Smooth the torso path separately from the support exchange. Both ankle
 # targets stay in their authored paths and constrain the available height.
 heights=[c.world(p)['pelvis'][0].y for p,_ in prepared]
 low=min(heights)
 span=restworld['pelvis'][0].y-low
 dip=heights[0]-low
 previous=0
 for i,(pose,targets) in enumerate(prepared):
  u=i/frames
  trough=.24
  end=.96
  if u<trough:
   height=heights[0]-dip*smooth(u/trough)
  else:
   x=min(1,(u-trough)/(end-trough))
   recovered=1-(1-x)**4*(1+4*x)
   height=low+span*recovered
  world=c.world(pose)
  available=1e8
  for side,(t,_) in targets.items():
   h,k,f=(world[n+'_'+side][0] for n in ('thigh','calf','foot'))
   upper,lower=(h-k).length,(f-k).length
   d=(upper+lower)-.03
   horizontal=(h.x-t.x)**2+(h.z-t.z)**2
   available=min(available,world['pelvis'][0].y+t.y+(max(0,d*d-horizontal))**.5-h.y)
  print('limit',gait,support,i,round(height-available,2)) if height>available+.05 else None
  pose['pelvis'][0].y+=height-world['pelvis'][0].y
  # Carry the idle clip's natural sway through the final samples.
  idle_weight=smooth((u-.64)/.30)
  if idle_weight>0:
   moving_idle=idle.pose(idle.length-(frames-i))
   for n in pose:
    if n=='root':continue
    refp,refq=rest[n]
    delta=moving_idle[n][1] @ refq.inverted()
    pose[n][1]=Quaternion().slerp(delta,idle_weight) @ pose[n][1]
    pose[n][0]=pose[n][0]+(moving_idle[n][0]-refp)*idle_weight
  for side,(target,q) in targets.items():
   leg(c,pose,side,target,q)
  if i==frames: pose=rest
  result.append(pose)
 return result
segments=[]; poses=[]
for g,frames in [('Walk',11),('Run',13),('Sprint',15)]:
 c=Clip(g)
 assert c.parents==template.parents and c.parents==idle.parents
 for side in ('l','r'):
  n=g.lower()+'_stop_'+('left' if side=='l' else 'right')
  movement=sample_stop(c,side,frames,g)
  # Unity derives slopes on the entire take before trimming a sub-clip.
  # Supply real neighbors on both sides of every playable frame range.
  poses.extend([movement[0]]*3)
  first=len(poses)
  poses.extend(movement)
  segments.append((n,first,len(poses)-1))
  for t in (1,2,3):
   tail=idle.pose(t)
   tail['root']=rest['root']
   poses.append(tail)
  print(*segments[-1])
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
