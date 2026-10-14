## ServicesApp (C# + WinUI 3)

现代化的 Windows 后台服务管理工具，支持NSSM的大部分功能，并且拥有美观的可视化操作界面，是NSSM完美替代品。

### 主要功能
1. **服务管理**：
   - 列出由该工具创建的服务。
   - 创建新服务（通过包装器模式运行任意可执行文件）。
   - 启动、停止、编辑、删除服务；点击服务行查看详情。
   - 支持服务描述、多行启动参数、失败自动重启策略（延迟 / 次数 / 计数重置 / 指数退避）。
   - 列表支持按名称、状态、路径排序（再次点击切换降序，第三次恢复添加顺序）。
   - 查看服务日志，支持 ANSI 颜色、stdout / stderr 筛选及文本换行切换。
2. **环境变量管理**：
   - 添加路径到系统 PATH 环境变量。
   - 打开系统环境变量设置界面。
3. **UI 交互**：
   - 现代化 Windows 11 风格界面。
   - 托盘图标支持。
   - 管理员权限检测与提权。
4. **命令行工具 `servicesctl`**：
   - 支持服务的增删改查、启停与日志查看，适合脚本与 AI 调用。

### 命令行工具

`servicesctl.exe` 与 `ServicesApp.exe` 位于同一目录（MSI 安装后会自动加入系统 PATH）。
所有命令均为非交互式，支持 `--json` 输出与明确的退出码，完整说明见内置帮助：

```powershell
servicesctl --help            # 命令总览、退出码、权限说明
servicesctl help create       # 单个命令的完整帮助（选项、JSON 字段、示例）

servicesctl list --json
servicesctl create --name MyApp --exe "C:\Apps\my-app.exe" --args "--port 8080" --auto-restart --start
servicesctl update MyApp --max-restarts 0 --restart
servicesctl logs MyApp --tail 50 --stream stderr
servicesctl delete MyApp
```

`list` / `get` / `logs` 普通权限即可运行；其余会修改系统服务的命令需在管理员终端中执行。

### 预览

![主界面](/.github/demo/1.png)
![主界面](/.github/demo/2.png)