from pathlib import Path
import bpy, json,sys,numpy as np,math
s=Path('.codex-task-work/ground-stop-settle')
ranges=json.loads((s/'ranges.json').read_text())
src=Path(sys.argv[sys.argv.index('--')+1])
def load(p,arr):
 bpy.ops.wm.read_factory_settings(use_empty=True)
 bpy.context.scene.render.fps=30
 bpy.ops.import_scene.fbx(filepath=str(p.resolve()),use_anim=True,anim_offset=0,ignore_leaf_bones=False)
 arm=next(o for o in bpy.data.objects if o.type=='ARMATURE')
 frames={}
 for t in arr:
  bpy.context.scene.frame_set(t)
  frames[t]={b.name:np.array(arm.matrix_world@b.matrix,dtype=float) for b in arm.pose.bones}
 return frames
idle=load(Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal/SS_Idle.fbx'),range(4))
seq=load(src,range(ranges[-1][2]+4))
worst=0
for name,a,b in ranges:
 for j in range(1,4):
  for n in seq[a]:
   assert np.abs(seq[a][n]-seq[a-j][n]).max()<1e-5,(n,name,j)
 for j in range(4):
  for n in seq[b]:
   e=np.abs(seq[b+j][n]-idle[j][n]).max()
   worst=max(worst,e)
   assert e<1e-4,(n,name,j,e)
print('Near-edge poses verified for',len(seq[0]),'bones across all',len(ranges),'clips. Maximum matrix component error',worst)
