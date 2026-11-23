import bpy, math, json
from pathlib import Path
from mathutils import Vector
stage=Path('.codex-task-work/ground-stop')
def sample(path,frames):
 bpy.ops.wm.read_factory_settings(use_empty=True)
 bpy.context.scene.render.fps=30
 bpy.ops.import_scene.fbx(filepath=str(path.resolve()),use_anim=True,anim_offset=0.0,ignore_leaf_bones=False)
 arm=next(o for o in bpy.data.objects if o.type=='ARMATURE')
 action=arm.animation_data.action
 print('Action',action.name,tuple(action.frame_range))
 assert abs(action.frame_range[0])<.01 and abs(action.frame_range[1]-max(frames))<.02
 bones=['root','pelvis']+[f'{bone}_{side}' for side in ('l','r') for bone in ('thigh','calf','foot','ball')]
 result=[]
 for f in frames:
  bpy.context.scene.frame_set(f)
  result.append({b:(arm.matrix_world @ arm.pose.bones[b].head).copy() for b in bones})
 return result
idle=sample(Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal/SS_Idle.fbx'),[0,90])[0]
seq=sample(Path('Assets/player/LocomotionStops.fbx'),range(84))
ranges=json.loads((stage/'ranges.json').read_text())
root0=seq[0]['root']
assert max((p['root']-root0).length for p in seq)<1e-4
for n,start,end in ranges:
 pts=seq[start:end+1]
 maxerr=max(((pts[-1][b]-pts[-1]['root'])-(idle[b]-idle['root'])).length for b in idle)
 assert maxerr < .001,(n,maxerr)
 swing='foot_'+('r' if n.endswith('left') else 'l')
 support='foot_'+('l' if n.endswith('left') else 'r')
 up=(idle['pelvis']-idle['root']).normalized()
 dz=[(p[swing]-pts[-1][swing]).dot(up) for p in pts]
 travel=(pts[0][swing]-pts[-1][swing]).length
 print(n,'duration',round((end-start)/30,3),'end error mm',round(maxerr*1000,3),'swing travel m',round(travel,3),'peak m',round(max(dz),3),'landed height m',round(dz[round((end-start)*.70)],3))
 assert max(dz)>.022 and abs(dz[round((end-start)*.70)]) <.015 and travel>.08
 assert all(math.isfinite(v) for p in pts for bone in p.values() for v in bone)
(stage/'source-poses.json').write_text(json.dumps([{k:list(v) for k,v in p.items()} for p in seq]))
