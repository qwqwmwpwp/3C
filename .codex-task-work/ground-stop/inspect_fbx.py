import sys
from pathlib import Path
sys.path.insert(0, 'D:/Steam/steamapps/common/Blender/5.3/scripts/addons_core/io_scene_fbx')
import fbx2json
base=Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal')
def child(n, name): return next(i for i in n.elems if i.id==name)
def sub(n, name): return child(n,name).props
root,ver=fbx2json.parse(base/'SS_Run.fbx')
objs=child(root,b'Objects')
for name in [b'GlobalSettings',b'Takes',b'Documents']:
 n=child(root,name); print(name,[(i.id,i.props,[(j.id,j.props) for j in i.elems][:18]) for i in n.elems][:12])
from collections import Counter
print(Counter(i.id for i in objs.elems))
for x in objs.elems:
 if x.id==b'Model' and x.props[1].split(b'\0')[0] in [b'root',b'pelvis',b'thigh_l',b'calf_l',b'foot_l']:
  print(x.props, [i.props for i in child(x,b'Properties70').elems if i.props[0] in [b'Lcl Translation',b'Lcl Rotation',b'Lcl Scaling',b'PreRotation',b'PostRotation',b'RotationOrder',b'RotationPivot']])
for name in [b'AnimationCurveNode',b'AnimationStack',b'AnimationCurve']:
 x=next(i for i in objs.elems if i.id==name);print(x.props,[(i.id,str(i.props)[:145],[(j.id,j.props) for j in i.elems][:10]) for i in x.elems])
print([i.props for i in child(root,b'Connections').elems[-15:]])
