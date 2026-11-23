from pathlib import Path
import bpy, json, sys, numpy as np, math
from mathutils import Vector
s=Path('.codex-task-work/ground-stop-settle')
asset,label=sys.argv[sys.argv.index('--')+1:]
ranges=json.loads((s/'ranges.json').read_text())
bones=['root','pelvis','spine_03','head','foot_l','foot_r','thigh_l','thigh_r','calf_l','calf_r']
def read(path,at):
 bpy.ops.wm.read_factory_settings(use_empty=True)
 bpy.context.scene.render.fps=30
 bpy.ops.import_scene.fbx(filepath=str(Path(path).resolve()),use_anim=True,anim_offset=0.0,ignore_leaf_bones=False)
 arm=next(o for o in bpy.data.objects if o.type=='ARMATURE')
 frames=[]
 for t in at:
  bpy.context.scene.frame_set(int(t),subframe=t-int(t))
  frames.append({bone:arm.matrix_world@arm.pose.bones[bone].matrix for bone in bones})
 return frames
def vector(p,b):return np.array(p[b].translation,dtype=float)
def angle(m,n):return 2*math.acos(min(1,abs(m.to_quaternion().dot(n.to_quaternion()))))*180/math.pi
sub=4
seq=read(asset,[i/sub for i in range((ranges[-1][2]+3)*sub+1)])
idle=read('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal/SS_Idle.fbx',[i/sub for i in range(6*sub+1)])
out={}
for name,start,end in ranges:
 full=seq[start*sub:end*sub+1];duration=end-start
 dest=idle[0]
 up=vector(dest,'pelvis')-vector(dest,'root'); up/=np.linalg.norm(up)
 sample=full+idle[1:]
 measures={}
 for bone in ['pelvis','spine_03','head']:
  positions=np.array([vector(f,bone) for f in sample])
  heights=(positions[::sub]-vector(dest,bone))@up*100
  delta=np.diff(positions[::sub],axis=0)*100
  after=math.floor(duration*2/3)
  finish=duration+2
  measures[bone]=dict(peak_speed=max(np.linalg.norm(delta[after:finish],axis=1)),peak_change=max(np.linalg.norm(np.diff(delta,axis=0)[after-1:finish-1],axis=1)),height=heights[:duration+2].round(3).tolist(),end_step=np.linalg.norm(delta[duration-1]),pose_error=np.linalg.norm(positions[duration*sub]-vector(dest,bone)))
  assert measures[bone]['pose_error'] < .0001
 joints=[]
 for side in ('l','r'):
  local=[f['pelvis'].inverted()@f['thigh_'+side] for f in full[::sub]]
  joints.append(max(angle(a,b) for a,b in zip(local,local[1:])))
 legs=tuple('foot_'+p for p in ('l','r'))
 swing=legs[1] if name.endswith('left') else legs[0]
 touch=math.ceil(duration*.67)*sub
 drift=max(np.linalg.norm(vector(f,swing)-vector(dest,swing)) for f in full[touch:])
 root=max(np.linalg.norm(vector(f,'root')-vector(dest,'root')) for f in full)
 measures.update(thigh_max=max(joints),landed_foot_drift=drift,root=root)
 print(name, 'tail [cm/frame; change] pelvis,chest,head',[(round(measures[b]['peak_speed'],2),round(measures[b]['peak_change'],2)) for b in ('pelvis','spine_03','head')], 'max thigh',round(max(joints),1), 'plant err mm',round(drift*1000,1),'last heights',measures['pelvis']['height'][after:duration+1])
 assert root<.0001
 out[name]=measures
(s/(label+'.json')).write_text(json.dumps(out,default=float))
