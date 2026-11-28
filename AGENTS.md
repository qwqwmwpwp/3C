# 仓库贡献指南

## 项目结构

- `Assets/Code/Character/` 按职责组织角色代码：`Behavior/` 负责行为决策，`Motion/` 负责移动与碰撞，`Forecast/` 负责轨迹预测，`Anim/` 负责动画选择与播放。角色的 Inspector 和烘焙工具放在 `Editor/`。
- `Assets/FollowCamera/` 包含 `Contracts`、`Core` 和 `Runtime` 三个程序集。角色代码通过 `Contracts` 使用相机接口，设备输入和场景访问由运行时适配器处理。
- `Assets/Data/Player/Shield/` 存放盾牌角色配置、地面动作库和动画模块。移动或重命名 Unity 资源时，一并移动或重命名对应的 `.meta` 文件，以保留 GUID 和资源引用。
- `Assets/Scenes/` 下的 `游戏场景.unity` 用于运行玩法，`动画相机测试.unity` 用于检查动画与相机表现。当前构建设置启用的场景是 `SampleScene.unity`。

## 构建与本地开发

使用 `ProjectSettings/ProjectVersion.txt` 指定的 Unity **2022.3.62f3**。在 PowerShell 中将 `$env:UNITY_EDITOR` 设为该版本 `Unity.exe` 的完整路径，然后从仓库根目录运行以下命令。

```powershell
# 打开项目，然后选择所需场景并点击 Play。
& $env:UNITY_EDITOR -projectPath "$PWD"

# 将已启用的场景构建为 Windows x64 可执行文件。
& $env:UNITY_EDITOR -batchmode -quit -projectPath "$PWD" -buildWindows64Player "Builds/Windows/3C.exe" -logFile "build.log"

# 检查程序集引用，以及角色代码对相机模块的依赖。
.\Tools\Check-FollowCameraBoundary.ps1
```

## 编码规范

C# 使用四个空格缩进，花括号独占一行。类型和方法使用 PascalCase，参数和局部变量使用 camelCase。沿用所在模块的命名空间和序列化字段命名习惯。在新增或修改的类型、方法声明上方，用一句中文说明其职责。每个修改过的 Unity 源文件最多 500 行。

## 测试与验证

项目使用 Unity Test Framework。从 **Window → General → Test Runner** 选择与改动相关的 Edit Mode 或 Play Mode 用例。回归测试名写清触发条件和预期结果，例如 `MissingGroup_UsesConfiguredFallback`。涉及画面表现时，记录场景、输入顺序和实际观察结果，并单独说明脚本编译结果。

## 提交与拉取请求

仓库历史中有 `更新` 和 `1` 这样的提交标题。新提交建议直接写明行为变化，例如 `修复持盾急转缺少候选时的回退`。在拉取请求（PR）中说明玩家可观察到的变化、涉及的资源或场景，以及实际执行的检查。有相关议题时附上链接；动画或相机表现有变化时，附一段能展示变化的短视频。
