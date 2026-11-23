import bpy, json, math, re
from pathlib import Path
from mathutils import Vector
root = Path("Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations")
stop = Path("Assets/player/LocomotionStops.fbx")
output = Path('.codex-task-work/ground-selection-20261121')
all_tracks = {}
ranges=json.loads((output/'ranges.json').read_text())
for src, spans in [(output/'LocomotionTurns.fbx',ranges),
    (root/'Normal/SS_Sprint.fbx',[('SS_Sprint',0,18)]),
    (stop,[('walk_stop_left', 3, 14), ('walk_stop_right', 21, 32), ('run_stop_left', 39, 52), ('run_stop_right', 59, 72), ('sprint_stop_left', 79, 94), ('sprint_stop_right', 101, 116)])]:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.render.fps = 30
    bpy.ops.import_scene.fbx(filepath=str(src.resolve()), use_anim=True, anim_offset=0.0)
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    def pos(name):
        return arm.matrix_world @ arm.pose.bones[name].head
    def rest(name):
        return arm.matrix_world @ arm.data.bones[name].head_local
    bones = ["pelvis","foot_l","foot_r","ball_l","ball_r","head","root"]
    print("SOURCE",src.name, list(arm.animation_data.action.frame_range),{name:tuple(round(v,4) for v in rest(name)) for name in bones})
    for name, start, end in spans:
        seq = []
        for i in range(start,end+1):
            bpy.context.scene.frame_set(i)
            seq.append({k:list(pos(k)) for k in bones})
        all_tracks[name] = seq
(output/"samples.json").write_text(json.dumps(all_tracks))