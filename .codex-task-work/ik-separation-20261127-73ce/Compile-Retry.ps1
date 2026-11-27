$ErrorActionPreference = 'Stop'
$project = (Get-Location).Path
$task = Join-Path $project '.codex-task-work/ik-separation-20261127-73ce'
$output = Join-Path $task 'compile-retry'
if(Test-Path -LiteralPath $output){throw 'Retry evidence directory exists'}
[void][IO.Directory]::CreateDirectory($output)
$changed = Import-Csv -LiteralPath (Join-Path $task 'changes.csv')
function Assert-TaskHashes {
 foreach($file in $changed) {
    if((Get-FileHash -LiteralPath $file.After).Hash -ne $file.CurrentSha256){throw "审核文件发生变化 $($file.After)"}
 }
}
Assert-TaskHashes
$assetRoot=(Get-Item -LiteralPath Assets).FullName + '\'
$unique=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$snapshot=Get-ChildItem -LiteralPath Assets/Code/Character -Recurse -File -Filter '*.cs' | Sort-Object FullName
$assemblyNames=@('Assembly-CSharp','Assembly-CSharp-Editor')
foreach($assembly in $assemblyNames) {
 $editor=$assembly.EndsWith('-Editor')
 $response=[Collections.Generic.List[string]]::new()
 $rspSources=[Collections.Generic.List[string]]::new()
 foreach($line in Get-Content -LiteralPath (Join-Path '.codex-animation-check-20261126-a73f' ($assembly+'.rsp'))) {
    if($line -match '^"Assets/Code/Character/.*\.cs"$'){continue}
    if($line -match '^-(out|refout):'){
      $name=if($Matches[1] -eq 'out'){$assembly+'.dll'}else{$assembly+'.ref.dll'}
      $response.Add('-'+$Matches[1]+':"'+(Join-Path $output $name)+'"')
    } elseif($line -match '^-r:.*[/\\]Assembly-CSharp\.ref\.dll"$') {
      $response.Add('-r:"'+(Join-Path $output 'Assembly-CSharp.ref.dll')+'"')
    } else {
      $response.Add($line)
      if($line -match '^[^-].*\.cs"?$'){$rspSources.Add($line.Trim('"'))}
    }
 }
 foreach($source in $snapshot) {
    $rel=$source.FullName.Substring($project.Length+1).Replace('\','/')
    if(($rel -match '/Editor/') -ne $editor){continue}
    $response.Add('"'+$rel+'"')
    $rspSources.Add($rel)
 }
 foreach($s in $rspSources){
    $path=(Get-Item -LiteralPath $s -ErrorAction Stop).FullName
    if(!$path.StartsWith($assetRoot,[StringComparison]::OrdinalIgnoreCase)){throw "源码输入不在当前 Assets $path"}
    if(!$unique.Add($path)){throw "重复源码输入 $path"}
 }
 [IO.File]::WriteAllLines((Join-Path $output ($assembly+'.rsp')),$response,[Text.UTF8Encoding]::new($false))
 "$assembly 输入源文件数 $($rspSources.Count)"
}
foreach($f in @($changed | Where-Object {$_.After.EndsWith('.cs')} | ForEach-Object {$_.After})+'Assets/Code/Character/Anim/CompiledAnimationDecisions.cs'){
 if(!$unique.Contains((Get-Item -LiteralPath $f).FullName)){throw "遗漏 $f"}
}
$unityData='E:/unity/2022/2022.3.62f3/Editor/Data'
foreach($assembly in $assemblyNames){
 $log=Join-Path $output ($assembly+'.log')
 & (Join-Path $unityData 'NetCoreRuntime/dotnet.exe') (Join-Path $unityData 'DotNetSdkRoslyn/csc.dll') ('@'+(Join-Path $output ($assembly+'.rsp'))) 2>&1 | Tee-Object -FilePath $log
 $result=$LASTEXITCODE
 "$assembly exit=$result" | Tee-Object -Append -FilePath $log
 if($result -ne 0){exit $result}
}
Assert-TaskHashes
'全部审核哈希在编译结束时一致'
