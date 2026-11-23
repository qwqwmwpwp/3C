import json
from pathlib import Path

import bpy


base = Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal')
output = Path(__file__).parent
names = ['root', 'pelvis', 'foot_l', 'ball_l', 'foot_r', 'ball_r', 'hand_l', 'hand_r', 'add_weapon_l']
samples = {}
for filename in ('SS_Idle', 'SS_Attack_1', 'SS_Attack_2', 'SS_Attack_4', 'SS_Take'):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.fps = 30
    bpy.ops.import_scene.fbx(filepath=str((base / (filename + '.fbx')).resolve()), use_anim=True, anim_offset=0.0)
    armature = next(obj for obj in bpy.data.objects if obj.type == 'ARMATURE')
    action = armature.animation_data.action
    first, last = action.frame_range
    print(filename, 'frame range at 30 FPS', first, last, 'scale', tuple(armature.scale))
    frames = []
    for frame in range(round(first), round(last) + 1):
        scene.frame_set(frame)
        frames.append({key: [float(component) for component in armature.matrix_world @ armature.pose.bones[key].head]
                       for key in names})
    samples[filename] = frames
(output / 'bone-samples.json').write_text(json.dumps(samples), encoding='utf-8')
