from pathlib import Path
from math import acos,pi
import bpy,sys,json,math
from mathutils import Vector
s=Path('.codex-task-work/ground-stop-thigh')
ranges=json.loads(Path('.codex-task-work/ground-stop/ranges.json').read_text())
args=sys.argv[sys.argv.index('--')+1:]
src,label=args
bpy.ops.wm.read_factory_settings(use_empty=True)
scene=bpy.context.scene;scene.render.fps=30
bpy.ops.import_scene.fbx(filepath=str(Path(src).resolve()),use_anim=True,anim_offset=0.0,ignore_leaf_bones=False)
arm=next(o for o in bpy.data.objects if o.type=='ARMATURE')
metrics={}
def quatangle(a,b):
 return 2*acos(min(1,abs(a.normalized().dot(b.normalized()))))*180/pi
def perframe(u):
 scene.frame_set(int(u),subframe=u-int(u))
 return {b.name:(arm.matrix_world @ b.matrix) for b in arm.pose.bones}
for name,start,end in ranges:
 matrices=[perframe(start+i*.25) for i in range(4*(end-start)+1)]
 vals={}
 for side in ('l','r'):
  def leg(m):
   h,k,a=(m[n+'_'+side].translation for n in ('thigh','calf','foot'))
   axis=(a-h).normalized(); v=k-h; pole=v-axis*v.dot(axis)
   return pole.normalized(),pole.length, 180-(h-k).angle(a-k)*180/pi
  rots=[(m['pelvis'].inverted()@m['thigh_'+side]).to_quaternion() for m in matrices]
  pole=[leg(m) for m in matrices]
  vel=[quatangle(a,b) for a,b in zip(rots[:-1],rots[1:])]
  turns=[p[0].angle(q[0],0)*180/pi for p,q in zip(pole[:-1],pole[1:])]
  integer=[round(quatangle(a,b),1) for a,b in zip(rots[:-4:4],rots[4::4])]
  bends=[round(p[2],1) for p in pole[::4]]
  print(name,side,'frame rotation',integer,'knee bend',bends,'max bend-normal step', round(max(turns),1))
  vals[side]={'frame_angles':integer,'knee_bend':bends,'max_subframe':max(vel),'max_pole':max(turns),'end_hip':list(rots[-1]),'end_ankle':list(matrices[-1]['foot_'+side].translation)}
 metrics[name]=vals
(s/(label+'.json')).write_text(json.dumps(metrics))
