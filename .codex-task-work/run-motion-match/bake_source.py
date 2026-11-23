import bpy, json, math, re
from pathlib import Path
from mathutils import Vector
root = Path("Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations")
stop = Path("Assets/player/LocomotionStops.fbx")
output = Path(".codex-task-work/run-motion-match")
all_tracks = {}
for src, spans in [(root / "Normal/SS_Run.fbx", [("SS_Run",0,20)]),
                   (root / "Normal/SS_Walk.fbx", [("SS_Walk",0,34)]),
                   (stop, [("run_stop_left",39,52),("run_stop_right",59,72)]),
                   (root / "Special/SS_Sp_Turn_L.fbx", [("TurnL",0,20)]),
                   (root / "Special/SS_Sp_Turn_R.fbx", [("TurnR",0,20)])]:
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
        if name.startswith("Turn"):
            for f in (0,10,20):
                print(name,f, {x:[round(c,3) for c in seq[f][x]] for x in ["root","pelvis","foot_l","foot_r"]})
(output/"source.json").write_text(json.dumps(all_tracks))
