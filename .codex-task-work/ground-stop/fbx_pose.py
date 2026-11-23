from pathlib import Path
import sys, math, array, collections
from mathutils import Vector, Quaternion, Euler
sys.path.insert(0,'D:/Steam/steamapps/common/Blender/5.3/scripts/addons_core/io_scene_fbx')
import fbx2json, encode_bin
from bisect import bisect_right
stage=Path('.codex-task-work/ground-stop')
base=Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal')
tick=1539538600 # 30 Hz FBX time
def child(n, name): return next(i for i in n.elems if i.id==name)
def sub(n, name): return child(n,name).props
def props(n): return {i.props[0]:i.props[4:] for i in child(n,b'Properties70').elems}
def clean(s): return s.split(b'\x00')[0].decode()
def rot(values): return Euler([math.radians(i) for i in values],'XYZ').to_quaternion()
def smooth(t): t=max(0,min(1,t)); return t*t*(3-2*t)
class Clip:
 def __init__(self,name):
  import importlib; importlib.reload(fbx2json)
  self.root,self.ver=fbx2json.parse(base/('SS_'+name+'.fbx'))
  objs=child(self.root,b'Objects')
  self.objs={n.props[0]:n for n in objs.elems if n.props}
  self.names={uid:clean(n.props[1]) for uid,n in self.objs.items() if n.id==b'Model'}
  self.models={self.names[uid]:self.objs[uid] for uid in self.names}
  self.static={name:props(n) for name,n in self.models.items()}
  self.pre={k:rot(v.get(b'PreRotation',(0,0,0))) for k,v in self.static.items()}
  self.post={k:rot(v.get(b'PostRotation',(0,0,0))).inverted() for k,v in self.static.items()}
  self.parents={k:None for k in self.models}
  cons=[n.props for n in child(self.root,b'Connections').elems]
  nodes={a:(self.names[b],c) for kind,a,b,*rest in cons if kind==b'OP' and b in self.names for c in rest}
  self.curves={(nodes[b][0],nodes[b][1],rest[0][-1:]):self.objs[a] for kind,a,b,*rest in cons if b in nodes and kind==b'OP'}
  for kind,a,b,*rest in cons:
   if kind==b'OO' and a in self.names and b in self.names: self.parents[self.names[a]]=self.names[b]
  self.length=sub(child(child(self.root,b'Takes'),b'Take'),b'LocalTime')[1]/tick
  for d in self.static.values():
   assert d.get(b'RotationOrder',[0])==[0]
   assert all(abs(x)<1e-6 for x in d.get(b'RotationPivot',[0,0,0])+d.get(b'ScalingPivot',[0,0,0]))
 def trs(self,n,channel,t):
  static=self.static[n].get(channel, [1]*3 if channel==b'Lcl Scaling' else [0]*3)
  out=[]
  for i,a in enumerate((b'X',b'Y',b'Z')):
   curve=self.curves.get((n,channel,a))
   if curve:
    times=sub(curve,b'KeyTime')[0]; vs=sub(curve,b'KeyValueFloat')[0]
    ti=t*tick; k=bisect_right(times,ti)-1
    if k<0: v=vs[0]
    elif k+1>=len(times): v=vs[-1]
    else: v=vs[k]+(vs[k+1]-vs[k])*(ti-times[k])/(times[k+1]-times[k])
   else: v=static[i]
   out.append(v)
  return Vector(out)
 def pose(self,t):
  return {n:[self.trs(n,b'Lcl Translation',t),self.pre[n] @ rot(self.trs(n,b'Lcl Rotation',t)) @ self.post[n]] for n in self.models}
 def world(self,pose):
  result={}
  def one(n):
   if n not in result:
    p,q=pose[n]; parent=self.parents[n]
    if parent: pp,pq=one(parent); p,q=pp+pq@p,pq@q
    result[n]=p,q
   return result[n]
  for n in pose: one(n)
  return result
def save_node(node):
 n=encode_bin.FBXElem(node.id)
 types={'C':'char','B':'bool','Y':'int16','I':'int32','F':'float32','D':'float64','L':'int64','R':'bytes','S':'string','f':'float32_array','i':'int32_array','d':'float64_array','l':'int64_array','b':'bool_array','c':'byte_array','Z':'int8'}
 for v,k in zip(node.props,node.props_type): getattr(n,'add_'+types[chr(k)])(v.tolist() if isinstance(v,array.array) else v)
 n.elems=[save_node(e) for e in node.elems]
 return n
