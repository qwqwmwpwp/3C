param([string]$AssemblyPath='Library/ScriptAssemblies/Assembly-CSharp.dll')
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::LoadFrom('E:/unity/2022/2022.3.62f3/Editor/Data/Managed/Unity.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path $AssemblyPath).Path)
$module=$assembly.MainModule
$mapping=$module.GetType('ThreeC.CharacterBehavior.CharacterBehaviorAnimationMapping')
$field=$mapping.Fields | Where-Object Name -CEQ 'Behavior'
if (!$field) { throw 'Behavior field missing' }
$decl=$module.GetType($field.FieldType.FullName)
$underlying=if($decl -and $decl.IsEnum){($decl.Fields | Where-Object Name -eq 'value__').FieldType.FullName}
$serializable=($field.IsPublic -or ($field.CustomAttributes.AttributeType.FullName -contains 'UnityEngine.SerializeField')) -and ($underlying -notin @('System.UInt64','System.Int64'))
$bits=$module.GetType('ThreeC.CharacterBehavior.CharacterBehaviorId').Fields
$asset=Get-Content Assets/player/ShieldCharacterProfile.asset -Raw
$rows=[regex]::Matches($asset,'(?ms)^    - Behavior: (\d+)\r?\n(?:(?!^    -|^    \w+:).)*?^      EntryAction: (\S+)')
foreach($pair in @(@('LightAttack','full.light_attack'),@('Guard','upper.raise'))){
    $bit=($bits | Where-Object Name -eq $pair[0]).Constant
    $match=$rows | Where-Object {$_.Groups[1].Value -eq "$bit" -and $_.Groups[2].Value -eq $pair[1]}
    if(!$match){throw ("Missing YAML mapping " + $pair[0])}
}
$actions=Get-Content Assets/player/Resources/PlayerCombat.inputactions -Raw | ConvertFrom-Json
$bindings=@(($actions.maps | Where-Object name -eq Combat).bindings | Where-Object action -eq LightAttack | ForEach-Object path)
$mouse=$bindings -contains '<Mouse>/leftButton' -and $bindings -notcontains '<Keyboard>/j' -and $bindings -contains '<Gamepad>/rightShoulder'
"Field=$($field.FieldType.FullName); enum storage=$underlying; Unity-supported persisted behavior field=$serializable; LightAttack=8 and Guard=4 configured; mouseBinding=$mouse"
if(!$serializable -or !$mouse){exit 1}
