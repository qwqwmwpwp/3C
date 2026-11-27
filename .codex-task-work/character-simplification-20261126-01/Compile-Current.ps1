param([switch]$CharacterOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = 'E:\github\3C'
$taskRoot = Join-Path $projectRoot '.codex-task-work\character-simplification-20261126-01'
$compileRoot = Join-Path $taskRoot 'compile'
New-Item -ItemType Directory -Path $compileRoot -Force | Out-Null
$unityData = 'E:\unity\2022\2022.3.62f3\Editor\Data'
$runtime = Join-Path $unityData 'NetCoreRuntime\dotnet.exe'
$compiler = Join-Path $unityData 'DotNetSdkRoslyn\csc.dll'
$assemblies = @('ThreeC.FollowCamera.Contracts','ThreeC.FollowCamera.Core','ThreeC.FollowCamera','Assembly-CSharp','Assembly-CSharp-Editor')
Set-Location -LiteralPath $projectRoot
$compileAssemblies = if ($CharacterOnly) { @('Assembly-CSharp','Assembly-CSharp-Editor') } else { $assemblies }
foreach ($assembly in $compileAssemblies) {
    $characterAssembly = $assembly.StartsWith('Assembly-CSharp')
    $editorAssembly = $assembly.EndsWith('-Editor')
    $sourceRsp = if ($characterAssembly) { Join-Path $projectRoot ('.codex-animation-check-20261126-a73f\'+$assembly+'.rsp') } else { Join-Path $projectRoot ('Library\Bee\artifacts\1900b0aE.dag\'+$assembly+'.rsp') }
    $sourceRoot = switch ($assembly) {
        'ThreeC.FollowCamera.Contracts' { 'Assets\FollowCamera\Contracts' }
        'ThreeC.FollowCamera.Core' { 'Assets\FollowCamera\Core' }
        'ThreeC.FollowCamera' { 'Assets\FollowCamera\Runtime' }
        default { 'Assets\Code\Character' }
    }
    $response = [System.Collections.Generic.List[string]]::new()
    foreach ($line in Get-Content -LiteralPath $sourceRsp) {
        if ($characterAssembly -and $line -match '^"Assets/Code/Character/.*\.cs"$') { continue }
        if (!$characterAssembly -and $line -match '^"Assets/FollowCamera/.*\.cs"$') { continue }
        if ($line -match '^-out:') { $response.Add('-out:"'+(Join-Path $compileRoot ($assembly+'.dll'))+'"'); continue }
        if ($line -match '^-refout:') { $response.Add('-refout:"'+(Join-Path $compileRoot ($assembly+'.ref.dll'))+'"'); continue }
        if ($line -match '^-r:.*[/\\]([^/\\]+)\.ref\.dll"$' -and $assemblies -contains $Matches[1]) {
            $response.Add('-r:"'+(Join-Path $compileRoot ($Matches[1]+'.ref.dll'))+'"'); continue
        }
        $response.Add($line)
    }
    $sourceCount = 0
    foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot $sourceRoot) -Recurse -Filter '*.cs' -File | Sort-Object FullName) {
        $relative = $source.FullName.Substring($projectRoot.Length+1).Replace('\','/')
        if ($characterAssembly -and (($relative -match '/Editor/') -ne $editorAssembly)) { continue }
        $response.Add('"'+$relative+'"')
        $sourceCount++
    }
    $responsePath = Join-Path $compileRoot ($assembly+'.rsp')
    [System.IO.File]::WriteAllLines($responsePath,$response,[System.Text.UTF8Encoding]::new($false))
    Write-Output ($assembly+': current owned sources='+$sourceCount)
    & $runtime $compiler ('@'+$responsePath) 2>&1 | Tee-Object -FilePath (Join-Path $compileRoot ($assembly+'.log'))
    $compileExit = $LASTEXITCODE
    Write-Output ($assembly+': exit='+$compileExit)
    if ($compileExit -ne 0) { exit $compileExit }
}