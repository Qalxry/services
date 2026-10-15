# 参与开发

## 工程

| 工程 | 说明 |
|---|---|
| `ServicesApp` | WinUI 3 图形界面（Windows App SDK 1.6） |
| `Services.Cli` | 命令行工具 `servicesctl` |
| `Services.Wrapper` | 服务包装器 `servicesvc` |
| `Services.Core` | 共用逻辑，分层见 [docs/architecture.md](docs/architecture.md) |
| `Services.Tests` | 纯逻辑单元测试（Linux / Windows 均可运行） |
| `Services.IntegrationTests` / `Services.TestTarget` | Windows 端到端测试与测试目标程序 |
| `Services.Installer` | WiX 安装包 |

## 构建

Windows（Visual Studio 2022 或 Build Tools，.NET 8 SDK）：

```powershell
msbuild Services.sln /t:Restore /p:Configuration=Release
msbuild ServicesApp/ServicesApp.csproj /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:PublishSingleFile=true /p:SelfContained=true /p:PublishDir=publish\
msbuild Services.Wrapper/Services.Wrapper.csproj /t:Publish /p:Configuration=Release /p:PublishDir=publish\
msbuild Services.Cli/Services.Cli.csproj /t:Publish /p:Configuration=Release /p:PublishDir=publish\
```

完整步骤（含安装包）见 `.github/workflows/build.yml`。

Linux 上可以编译除 WinUI 界面外的工程并运行单元测试：

```bash
dotnet test Services.Tests
dotnet build Services.Cli -p:EnableWindowsTargeting=true
```

## 测试

- 单元测试：`dotnet test Services.Tests`。
- 集成测试需要 Windows 与管理员权限，会真实创建并删除服务（名称以 `IT_` 开头）：

  ```powershell
  dotnet publish Services.TestTarget -c Release -o it-target
  $env:SERVICES_IT_BIN = "<包含 servicesctl.exe 与 servicesvc.exe 的目录>"
  $env:SERVICES_IT_TARGET = "$PWD\it-target"
  dotnet test Services.IntegrationTests
  ```

  未设置环境变量或非管理员时测试自动跳过；CI 中设置 `SERVICES_IT_REQUIRED=1`，条件不满足时失败。
- 每次推送都会在 GitHub Actions 的 Windows 构建机上运行单元测试与集成测试。

## 约定

- 新功能尽量把逻辑写成不依赖 Windows API 的纯函数，放在 `Services.Core` 并补单元测试；
  涉及服务行为的改动补集成测试。
- 界面只通过 `Services.Core/Application` 与 `Services.Core/Presentation` 使用核心功能。
- 注册表值名只使用 `Storage/RegistryNames.cs` 中的常量。
- 修改 `Services.Cli/HelpText.cs` 后重新生成命令参考：
  `UPDATE_DOCS=1 dotnet test Services.Tests --filter CliReference`（CI 会校验 `docs/cli.md` 与帮助一致）。
- 代码风格见 `.editorconfig`（UTF-8、LF、4 空格）；单个文件尽量不超过 500 行。
- 提交信息：`类型: 摘要`（feat / fix / refactor / test / docs / chore），正文说明动机与影响；
  一个提交只做一件事，纯格式化（如换行符）单独提交。
