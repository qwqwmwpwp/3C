# 从仓库任意目录均可运行，违规时输出相对路径、行号和归属并返回 1。
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$cameraRoot = Join-Path $repository 'Assets/FollowCamera'
$characterRoot = Join-Path $repository 'Assets/Code/Character'
$characterDriver = 'Behavior/CharacterBehaviorDriver.cs'

# 用空白替换注释和字符串，保留原始换行，避免注释示例触发规则。
$trivia = '(?s)//[^\r\n]*|/\*.*?\*/|(?:\$@|@\$|@)"(?:""|[^"])*"|\$?"(?:\\[^\r\n]|[^"\\\r\n])*"|''(?:\\[^\r\n]|[^''\\\r\n])*'''
function Read-CSharpCode([string] $path) {
    $source = [System.IO.File]::ReadAllText($path)
    [regex]::Replace($source, $trivia, { param($match) $match.Value -replace '[^\r\n]', ' ' })
}

function Find-Violation([string] $path, [string] $code, [string] $pattern, [string] $message) {
    foreach ($match in [regex]::Matches($code, $pattern)) {
        $line = 1 + [regex]::Matches($code.Substring(0, $match.Index), '\n').Count
        '{0}:{1} {2}（{3}）' -f $path, $line, $message, ($match.Value -replace '\s+', ' ')
    }
}

$controlInput = 'Runtime/CameraControlInput.cs'
$targetInput = 'Runtime/CameraTargetInput.cs'
$obstructionInput = 'Runtime/CameraObstructionInput.cs'
$poseOutput = 'Runtime/CameraPoseOutput.cs'
$mainCamera = '\bCamera\s*\.\s*main\b'
$runtimePorts = 'CameraFollowDriver|CameraControlInput|CameraTargetInput|CameraObstructionInput|CameraControlOutput|CameraPoseOutput'
$concreteCamera = "\b(?:OrbitCameraSolver|$runtimePorts)\b"
$rules = @(
    @{ Pattern = '\b(?:Input|InputSystem|InputAction(?:Asset|Map|Reference)?|Mouse|Keyboard|Gamepad|Touchscreen|Joystick|Pen|Application|Cursor|CursorLockMode)\b'
       Owners = @($controlInput); Message = '设备输入和应用状态仅由 CameraControlInput 访问' }
    @{ Pattern = '\bOnApplication(?:Focus|Pause)\s*\('; Owners = @($controlInput); Message = '应用焦点和暂停回调仅由 CameraControlInput 接收' }
    @{ Pattern = '\b(?:Physics(?:2D)?|PhysicsScene(?:2D)?|Collider(?:2D)?|RaycastHit(?:2D)?|QueryTriggerInteraction)\b'
       Owners = @($obstructionInput); Message = '遮挡查询仅由 CameraObstructionInput 访问' }
    @{ Pattern = '\b(?:Transform|transform|GameObject|gameObject|Rigidbody)\b'
       Owners = @($targetInput, $obstructionInput, $poseOutput); Message = '场景对象访问仅用于目标采样、遮挡过滤和位姿输出' }
    @{ Pattern = '\bCamera\b(?!\s*\.\s*main\b)'; Owners = @($poseOutput); Message = 'Camera 组件仅由 CameraPoseOutput 持有' }
    @{ Pattern = $mainCamera; Owners = @(); Message = '使用场景显式绑定的输出相机' }
    @{ Pattern = '\b(?:CharacterBehavior\w*|CharacterAnimation\w*|Character(?:Controller|MoveBehavior|DashBehavior|MovementSettings)|AnimationCamera\w*|CameraTimeline\w*|CameraTargetBinding|Animator|PlayableDirector)\b'
       Owners = @(); Message = '跟随镜头不依赖角色行为或表演模块' }
    @{ Pattern = '\b(?:FindObjectOfType|FindObjectsOfType|FindFirstObjectByType|FindAnyObjectByType|FindObjectsByType)\b'
       Owners = @(); Message = '场景引用使用显式配置' }
    @{ Pattern = '\b(?:GetComponent(?:InChildren|InParent)?|GetComponents(?:InChildren|InParent)?|TryGetComponent)\b'
       Owners = @($targetInput, $obstructionInput, $poseOutput); Message = '组件访问仅用于目标采样、遮挡过滤和位姿输出' }
)

$requiredFiles = @(
    'Contracts/CameraControlSource.cs', 'Core/OrbitCameraSolver.cs', 'Runtime/CameraFollowDriver.cs',
    $controlInput, $targetInput, $obstructionInput, 'Runtime/CameraControlOutput.cs', $poseOutput
)

$runtimeAssembly = 'Runtime/ThreeC.FollowCamera.asmdef'
$assemblyAutoReferences = [ordered]@{
    'Contracts/ThreeC.FollowCamera.Contracts.asmdef' = $true
    'Core/ThreeC.FollowCamera.Core.asmdef' = $false
}
$assemblyAutoReferences[$runtimeAssembly] = $false

$violations = @(
    foreach ($path in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $cameraRoot $path) -PathType Leaf)) {
            "Assets/FollowCamera/$path 缺少边界文件"
        }
    }

    $runtimeMeta = Join-Path $cameraRoot "$runtimeAssembly.meta"
    $runtimeReferenceNames = @('ThreeC.FollowCamera', 'ThreeC.FollowCamera.dll')
    $runtimeGuid = if (Test-Path -LiteralPath $runtimeMeta -PathType Leaf) {
        [regex]::Match((Get-Content -LiteralPath $runtimeMeta -Raw), '(?m)^guid:\s*([0-9a-fA-F]{32})\s*$').Groups[1].Value
    }
    if ($runtimeGuid) {
        $runtimeReferenceNames += "GUID:$runtimeGuid"
    } else {
        "Assets/FollowCamera/$runtimeAssembly.meta 缺少有效的运行时程序集 GUID"
    }
    foreach ($assembly in $assemblyAutoReferences.GetEnumerator()) {
        $path = "Assets/FollowCamera/$($assembly.Key)"
        $assemblyFile = Join-Path $repository $path
        if (-not (Test-Path -LiteralPath $assemblyFile -PathType Leaf)) {
            "$path 缺少程序集定义"
            continue
        }
        try {
            $definition = Get-Content -LiteralPath $assemblyFile -Raw | ConvertFrom-Json
        } catch {
            "$path 无法解析程序集定义（$($_.Exception.Message)）"
            continue
        }
        if ($definition.autoReferenced -isnot [bool] -or $definition.autoReferenced -ne $assembly.Value) {
            "$path autoReferenced 应为 $($assembly.Value.ToString().ToLowerInvariant())"
        }
        if ($assembly.Key -ne $runtimeAssembly) {
            foreach ($reference in @($definition.references) + @($definition.precompiledReferences)) {
                if ($reference -in $runtimeReferenceNames -or $reference -match '^Assembly-CSharp(?:-Editor)?(?:-firstpass)?(?:\.dll)?$') {
                    "$path 核心和契约不能引用运行时或默认项目脚本（$reference）"
                }
            }
        }
    }

    if (Test-Path -LiteralPath $cameraRoot -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $cameraRoot -Filter '*.cs' -File -Recurse) {
            $relative = $file.FullName.Substring($cameraRoot.Length + 1).Replace('\', '/')
            $path = "Assets/FollowCamera/$relative"
            $code = Read-CSharpCode $file.FullName
            foreach ($rule in $rules) {
                if ($relative -notin $rule.Owners) {
                    Find-Violation $path $code $rule.Pattern $rule.Message
                }
            }
            if ($relative -in @($targetInput, $obstructionInput)) {
                # 这两个端口允许读取场景对象，只检查变换接收者，避开普通数据字段的赋值。
                $transformNames = @('transform') + @(
                    foreach ($declaration in [regex]::Matches($code, '\b(?:Transform|Rigidbody)\s+(\w+)\b')) {
                        [regex]::Escape($declaration.Groups[1].Value)
                    }
                )
                $receiver = '\b(?:' + ($transformNames -join '|') + ')\s*\??\.\s*'
                $mutation = '(?:SetPositionAndRotation|SetLocalPositionAndRotation|LookAt|Rotate|RotateAround|Translate|MovePosition|MoveRotation|SetParent|SetSiblingIndex)\s*\(|(?:position|localPosition|rotation|localRotation|localScale|eulerAngles|localEulerAngles|right|forward|up|parent)\s*[+*/-]?=(?!=|>)'
                Find-Violation $path $code ($receiver + '(?:' + $mutation + ')') '场景位姿仅由 CameraPoseOutput 写入'
            }
            if ($relative.StartsWith('Core/')) {
                Find-Violation $path $code '\b(?:MonoBehaviour|Behaviour|Component|ScriptableObject|Time|Debug|CameraControlSource)\b' '核心仅接收数据并进行计算'
                Find-Violation $path $code "\b(?:$runtimePorts)\b" '核心不依赖运行时端口'
            }
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $characterRoot $characterDriver) -PathType Leaf)) {
        "Assets/Code/Character/$characterDriver 缺少角色接入点"
    }
    if (Test-Path -LiteralPath $characterRoot -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $characterRoot -Filter '*.cs' -File -Recurse) {
            $relative = $file.FullName.Substring($characterRoot.Length + 1).Replace('\', '/')
            if ($relative -like 'Editor/*') { continue }
            $path = "Assets/Code/Character/$relative"
            $code = Read-CSharpCode $file.FullName
            Find-Violation $path $code $concreteCamera '角色只依赖 CameraControlSource 合同'
            Find-Violation $path $code $mainCamera '角色使用 CameraControlSource 读取移动基底'
            if ($relative -eq $characterDriver) {
                # 除序列化字段、判空和只读方法调用外，避免把源对象传给其他代码。
                $basisRead = '\bcameraControlSource\s*\.\s*TryGetBasis\s*\('
                if (-not [regex]::IsMatch($code, $basisRead)) {
                    "$path 缺少 CameraControlSource.TryGetBasis 读取"
                }
                $allowedUse = '\bCameraControlSource\s+cameraControlSource\s*;|\bcameraControlSource\s*!=\s*null\b|' + $basisRead
                $code = [regex]::Replace($code, $allowedUse, { param($match) $match.Value -replace '[^\r\n]', ' ' })
            }
            Find-Violation $path $code '\b(?:CameraControlSource|cameraControlSource)\b' '角色只在驱动器通过 TryGetBasis 读取移动基底'
        }
    }
)

if ($violations.Count -gt 0) {
    $violations | Write-Output
    exit 1
}
Write-Output '通过跟随镜头端口归属、程序集引用和角色接入检查。'
