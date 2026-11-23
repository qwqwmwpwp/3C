from pathlib import Path
import importlib
import sys

sys.path.insert(0, 'D:/Steam/steamapps/common/Blender/5.3/scripts/addons_core/io_scene_fbx')
import fbx2json


def child(node, name):
    return next(x for x in node.elems if x.id == name)


base = Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal')
for name in ('SS_Idle', 'SS_Attack_1', 'SS_Attack_2', 'SS_Attack_4', 'SS_Take'):
    importlib.reload(fbx2json)
    root, ver = fbx2json.parse(base / (name + '.fbx'))
    props = child(child(root, b'GlobalSettings'), b'Properties70')
    print(name, {x.props[0]: x.props[4:] for x in props.elems if x.props[0] in [b'TimeMode', b'CustomFrameRate', b'TimeSpanStart', b'TimeSpanStop']})
    print([(c.id,c.props,[(x.id,x.props) for x in c.elems]) for c in child(root,b'Takes').elems])
