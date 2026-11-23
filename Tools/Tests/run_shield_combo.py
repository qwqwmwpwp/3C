"""Run the fixed-step combat tests against a chosen project assembly, without opening Unity."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
args = argparse.ArgumentParser()
args.add_argument('--editor-data', type=Path, required=True, help='Unity Editor/Data directory')
args.add_argument('--assemblies', type=Path, default=root/'Library/ScriptAssemblies')
args.add_argument('--source', action='store_true', help='Exercise working-tree behavior scripts over cached Unity dependencies')
options = args.parse_args()
editor = options.editor_data.resolve()
assemblies = options.assemblies.resolve()
mono = editor/'MonoBleedingEdge'
framework = mono/'lib/mono/4.7.2-api'
managed = editor/'Managed/UnityEngine'
references = [framework/'mscorlib.dll', framework/'Facades/netstandard.dll', framework/'System.dll',
              framework/'System.Core.dll', managed/'UnityEngine.CoreModule.dll', assemblies/'Assembly-CSharp.dll']
sources = [Path(__file__).with_name('ShieldComboRegression.cs')]
if options.source:
    behavior = root/'Assets/Code/Character/Behavior'
    sources.extend(p for p in behavior.glob('*.cs') if p.stem not in ('CharacterBehaviorDriver','CharacterProfile'))
    sources.append(root/'Assets/Code/Player/Input/PlayerInputSystem.cs')
    motion = root/'Assets/Code/Character/Motion'
    sources.extend(motion/name for name in ('CharacterMotorStep.cs','CharacterGroundSteering.cs','CharacterGroundTrajectory.cs'))
    sources.append(root/'Assets/Code/Character/Anim/CharacterMatchTrajectory.cs')
    references.append(managed/'UnityEngine.PhysicsModule.dll')
    references.extend([managed/'UnityEngine.AnimationModule.dll', root/'Library/ScriptAssemblies/Unity.InputSystem.dll'])
(root/'.utmp').mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(dir=root/'.utmp') as temporary:
    output = Path(temporary)/'Assembly-CSharp-Editor.exe'
    compile_args = ['-nologo', '-langversion:latest', '-target:exe', '-out:'+str(output)]
    if options.source:
        compile_args.append('-nowarn:0436')
    subprocess.run([editor/'NetCoreRuntime/dotnet.exe', editor/'DotNetSdkRoslyn/csc.dll', *compile_args,
                    *['-r:'+str(p) for p in references], *sources], check=True)
    env = os.environ.copy()
    env['MONO_PATH'] = os.pathsep.join(map(str, [assemblies,managed,editor/'Managed',
                                                 root/'Library/ScriptAssemblies',framework/'Facades']))
    raise SystemExit(subprocess.run([mono/'bin/mono.exe',output], env=env).returncode)

