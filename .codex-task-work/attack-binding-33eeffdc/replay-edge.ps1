param([Parameter(Mandatory)][string]$AssemblyPath)
$ErrorActionPreference='Stop'
$eng='E:/unity/2022/2022.3.62f3/Editor/Data/Managed/UnityEngine'
foreach($n in 'UnityEngine.CoreModule','UnityEngine.PhysicsModule','UnityEngine.AnimationModule'){
    [void][Reflection.Assembly]::LoadFrom((Join-Path $eng ($n+'.dll')))
}
$assembly=[Reflection.Assembly]::LoadFrom((Get-Item -LiteralPath $AssemblyPath).FullName)
$flags=[Reflection.BindingFlags]'Public,NonPublic,Instance'
function T([string]$name){$assembly.GetType(('ThreeC.CharacterBehavior.'+$name),$true)}
$map=T 'CharacterBehaviorAnimationMapping'
$behavior=T 'CharacterBehaviorId'
$trigger=T 'CharacterAnimationTrigger'
$rules=[Array]::CreateInstance($map,2)
$yaml=Get-Content Assets/player/ShieldCharacterProfile.asset -Raw
$rows=[regex]::Matches($yaml,'(?ms)^    - Behavior: (\d+)\r?\n(?:(?!^    -|^    \w+:).)*?^      EntryAction: (\S+)')
if ($rows.Count -ne 2){throw 'Expected shield profile rows'}
for($i=0;$i -lt $rows.Count;$i++){
    $r=$rows[$i];$row=[Activator]::CreateInstance($map);$f=$map.GetField('Behavior',$flags)
    # The installed editor explicitly reports that it skipped the old ulong-backed enum field.
    if(!$f.FieldType.IsEnum){$f.SetValue($row,[uint64]$r.Groups[1].Value)}
    $row.EntryAction=$r.Groups[2].Value
    if($r.Value -match 'Trigger: (\d+)'){$row.Trigger=[Enum]::ToObject($trigger,[int]$Matches[1])}
    $rules.SetValue($row,$i)
}
$ctor=(T 'CharacterBehaviorAnimationBindings').GetConstructors($flags)[0]
$arg=[object[]]::new(4);$arg[0]=$rules;$bindings=$ctor.Invoke($arg)
$stepType=T 'CharacterBehaviorStep';$resultType=T 'CharacterBehaviorFrameResult';$frameType=T 'CharacterControllerFrameResult'
$step=[Activator]::CreateInstance($stepType)
$attack=[Enum]::Parse($behavior,'LightAttack')
foreach($field in 'Started','Active','Executed'){ $stepType.GetField($field,$flags).SetValue($step,$attack) }
$result=[Activator]::CreateInstance($resultType)
$resultType.GetField('step',$flags).SetValue($result,$step)
$frame=[Activator]::CreateInstance($frameType)
$frameType.GetField('<Behavior>k__BackingField',$flags).SetValue($frame,$result)
$frameType.GetField('<SimulationVersion>k__BackingField',$flags).SetValue($frame,[int]1)
$bindings.Observe($frame,$false)
$queue=$bindings.GetType().GetField('edges',$flags).GetValue($bindings)
$entryKeys=@(foreach($e in $queue){$e.Item1.Rule.EntryAction})
"Actual Observe(LightAttack started): queued=$($queue.Count) keys=[$($entryKeys -join ',')]"
if($queue.Count -ne 1 -or $entryKeys[0] -ne 'full.light_attack'){exit 1}
