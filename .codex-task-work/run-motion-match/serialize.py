from pathlib import Path
import json, yaml, math, uuid, re
root=Path("Assets/CharacterBehavior")
base=Path("Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal")
sources=json.loads(Path(".codex-task-work/run-motion-match/source.json").read_text())
asset=Path("Assets/player/ShieldGroundMotion.asset")
meta_template=(root/"Runtime/CharacterLocomotionPoseSelector.cs.meta").read_text()
for name in ["Runtime/CharacterMotionDatabase", "Runtime/CharacterMotionMatcher", "Runtime/CharacterFootObservation","Runtime/CharacterMatchTrajectory","Editor/CharacterMotionDatabaseEditor"]:
    dst=root/(name+".cs.meta")
    if not dst.exists(): dst.write_text(re.sub(r"guid: [0-9a-f]+","guid: "+uuid.uuid4().hex,meta_template))
def guid(path):
    return re.search(r"^guid: (\w+)",Path(str(path)+".meta").read_text(), re.M)[1]
def vec(arr):
    return "{"+", ".join(axis+": "+format(v,".8g") for axis,v in zip("xyz",arr))+"}"
def contact(keys,t):
    if t<=keys[0]["time"]: return keys[0]["value"]
    for a,b in zip(keys,keys[1:]):
        if t<=b["time"]:
            d=b["time"]-a["time"]; u=(t-a["time"])/d
            val=(2*u**3-3*u*u+1)*a["value"]+(u**3-2*u*u+u)*d*a["outSlope"]+(-2*u**3+3*u*u)*b["value"]+(u**3-u*u)*d*b["inSlope"]
            return max(0,min(1,val))
    return keys[-1]["value"]
def point(raw,bone):
    dx,dy,dz=(a-b for a,b in zip(raw[bone],raw["pelvis"]))
    return [-dx,-dz,dy]
controller=guid("Assets/player/player.controller")
script=guid(root/"Runtime/CharacterMotionDatabase.cs")
body=f"""%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: 0}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {script}, type: 3}}
  m_Name: ShieldGroundMotion
  m_EditorClassIdentifier:
  Avatar: {{fileID: 9000000, guid: b02f7765ace5dff4a866b767512e4a05, type: 3}}
  Controller: {{fileID: 9100000, guid: {controller}, type: 2}}
  Tracks:
"""
for name, file, states,speed,brake in [("SS_Run",base/"SS_Run.fbx",["move","locomotion_run_start"],4.98,0),("SS_Walk",base/"SS_Walk.fbx",["walk","locomotion_walk_start"],1.55,0),("run_stop_left",Path("Assets/player/LocomotionStops.fbx"),["run_stop_left"],4.98,.16),("run_stop_right",Path("Assets/player/LocomotionStops.fbx"),["run_stop_right"],4.98,.16)]:
    meta=yaml.safe_load(Path(str(file)+".meta").read_text())["ModelImporter"]
    id=next(row["first"][74] for row in meta["internalIDToNameTable"] if row["second"]==name)
    clip=next(c for c in meta["animations"]["clipAnimations"] if c["name"]==name)
    curves={c["name"]:c["curve"]["curve"] for c in clip["curves"]}
    seq=sources[name]; n=len(seq)-1; assert n==clip["lastFrame"]-clip["firstFrame"]
    body+="  - Clip: {fileID: %d, guid: %s, type: 3}\n    States:\n"%(id,guid(file))
    body+="".join("    - Base Layer."+s+"\n" for s in states)
    body+="    ReferenceSpeed: %g\n    BrakeSeconds: %g\n    LastEntrySeconds: %.8g\n    Seconds: %.8g\n    Poses:\n"%(speed,brake,.09 if brake else n/30,n/30)
    for i,frame in enumerate(seq):
        before=i-1 if i>0 else (0 if brake else n-1)
        after=i+1 if i<n else (n if brake else 1)
        dt=(1 if brake and i in (0,n) else 2)/30
        for k,bon in [("Left","foot_l"),("Right","foot_r")]:
            body+=("    - " if k=="Left" else "      ")+k+": "+vec(point(frame,bon))+"\n"
        for k,bon in [("Left","foot_l"),("Right","foot_r")]:
            v=[(a-b)/dt for a,b in zip(point(seq[after],bon),point(seq[before],bon))]
            body+="      "+k+"Velocity: "+vec(v)+"\n"
        vals=[contact(curves[k],i/n) for k in ("IKLeftFoot","IKRightFoot")]
        body+="      Contact: "+vec(vals)+"\n"
    print(name,len(seq),n/30)
assert not asset.exists()
asset.write_bytes(body.encode())
assetguid=uuid.uuid4().hex
Path(str(asset)+".meta").write_text("fileFormatVersion: 2\nguid: "+assetguid+"\nNativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 11400000\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n")
p=Path("Assets/player/player 1.prefab"); raw=p.read_bytes(); anchor=b"  lowerBodyLayerIndex: 1"; assert raw.count(anchor)==1
sep=b"\r\n" if b"\r\n" in raw else b"\n"
lines=["  motionDatabase: {fileID: 11400000, guid: "+assetguid+", type: 2}","  motionMatching: 2","  matchSwitchMargin: 0.12","  matchReverseHold: 0.08"]
p.write_bytes(raw.replace(anchor,sep.join(s.encode() for s in lines)+sep+anchor))
