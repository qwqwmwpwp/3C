$ErrorActionPreference = 'Stop'
$projectRoot = 'E:\github\3C'
$taskRoot = Join-Path $projectRoot '.codex-task-work\structure-opt-20261126-01'
$compileRoot = Join-Path $taskRoot 'compile'
New-Item -ItemType Directory -Path $compileRoot -Force | Out-Null
$unityData = 'E:\unity\2022\2022.3.62f3\Editor\Data'
$runtime = Join-Path $unityData 'NetCoreRuntime\dotnet.exe'
$compiler = Join-Path $unityData 'DotNetSdkRoslyn\csc.dll'
Set-Location -LiteralPath $projectRoot
foreach ($assembly in @('Assembly-CSharp', 'Assembly-CSharp-Editor')) {
    $editorAssembly = $assembly.EndsWith('-Editor')
    $original = Get-Content -LiteralPath (Join-Path $projectRoot ('.codex-animation-check-20261126-a73f\' + $assembly + '.rsp'))
    $response = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $original) {
        if ($line -match '^"Assets/Code/Character/.*\.cs"$') { continue }
        if ($line -match '^-out:') { $response.Add('-out:"' + (Join-Path $compileRoot ($assembly + '.dll')) + '"'); continue }
        if ($line -match '^-refout:') { $response.Add('-refout:"' + (Join-Path $compileRoot ($assembly + '.ref.dll')) + '"'); continue }
        if ($line -match '^-r:.*\.codex-animation-check-20261126-a73f.*Assembly-CSharp\.ref\.dll') {
            $response.Add('-r:"' + (Join-Path $compileRoot 'Assembly-CSharp.ref.dll') + '"'); continue
        }
        $response.Add($line)
    }
    $sourceCount = 0
    foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Assets\Code\Character') -Recurse -Filter '*.cs' -File | Sort-Object FullName) {
        $relative = $source.FullName.Substring($projectRoot.Length + 1).Replace('\','/')
        $isEditor = $relative -match '/Editor/'
        if ($isEditor -ne $editorAssembly) { continue }
        $response.Add('"' + $relative + '"')
        $sourceCount++
    }
    $responsePath = Join-Path $compileRoot ($assembly + '.rsp')
    [System.IO.File]::WriteAllLines($responsePath, $response, [System.Text.UTF8Encoding]::new($false))
    Write-Output ($assembly + ': current Character sources=' + $sourceCount)
    & $runtime $compiler ('@' + $responsePath) 2>&1 | Tee-Object -FilePath (Join-Path $compileRoot ($assembly + '.log'))
    $compileExit = $LASTEXITCODE
    Write-Output ($assembly + ': exit=' + $compileExit)
    if ($compileExit -ne 0) { exit $compileExit }
}