## ServicesApp (C# + WinUI 3)

现代化的 Windows 后台服务管理工具，支持NSSM的大部分功能，并且拥有美观的可视化操作界面，是NSSM完美替代品。

### 主要功能
1. **服务管理**：
   - 列出由该工具创建的服务。
   - 创建新服务（通过包装器模式运行任意可执行文件）。
   - 启动、停止、编辑、删除服务；点击服务行查看详情。
   - 支持服务描述、多行启动参数、失败自动重启策略（延迟 / 次数 / 计数重置 / 指数退避）。
   - 优雅停止：可选发送 Ctrl+C 或执行停止命令（如 nginx 的 `{exe} -s quit`），超时后强制结束。
   - 目标程序及其子进程纳入 Job Object，包装器异常退出时不会遗留孤儿进程。
   - 资源限制：进程树内存上限、单进程内存上限、CPU 占用硬上限、进程数上限。
   - 健康检查：HTTP / TCP / 命令探测，连续失败后自动重启（启动宽限期、超时、阈值可配置）。
   - 运行账户：LocalSystem（默认）、LocalService、NetworkService、虚拟账户或指定用户。
   - 服务级环境变量：每行一个 `KEY=VALUE`，叠加在继承的环境之上，值中可用 `%PATH%` 等引用现有变量。
   - 敏感环境变量（密钥、口令）：值保存在只有 SYSTEM、管理员与服务运行账户可读的注册表子键中，
     界面与 `servicesctl get` / `export` 默认遮蔽（`--show-secrets` 显示）。
   - 运行状态：详情与 `servicesctl get` 显示运行时长、重启次数（本轮 / 累计）、上次退出码与时间、最近失败原因（包装器写入 `%ProgramData%\windows_service_logs\state\<服务ID>.json`）。
   - 服务依赖与延迟自动启动：启动类型可选“延迟自动启动”；可指定依赖的 Windows 服务（如 `Tcpip`、`MySQL80`），启动前系统会先启动它们。
   - 列表支持按名称、状态、路径排序（再次点击切换降序，第三次恢复添加顺序）。
   - 查看服务日志，支持 ANSI 颜色、stdout / stderr 筛选、行时间戳显示开关及文本换行切换。
   - 日志每行带毫秒级时间戳；除按天数清理外，可设置日志总大小上限（默认 1 GB，0 为不限制），超出时从最旧的文件开始删除。
2. **环境变量管理**：
   - 添加路径到系统 PATH 环境变量。
   - 打开系统环境变量设置界面。
3. **UI 交互**：
   - 现代化 Windows 11 风格界面。
   - 托盘图标支持。
   - 管理员权限检测与提权。
4. **独立服务包装器 `servicesvc`**：
   - 服务由轻量的 `servicesvc.exe` 运行并监管目标程序，与界面程序解耦；更新或关闭界面不影响服务。
   - 安装目录中的 `servicesvc.exe` 只是模板：服务运行的是部署到 `%ProgramData%\ServicesApp\wrapper\<版本>-<哈希>\` 的副本，
     该目录仅 SYSTEM 与管理员可修改。因此升级时安装目录不会被运行中的服务锁定，无需停止服务；
     程序即使解压在普通用户可写的目录，以 SYSTEM 运行的服务也不会执行可被替换的文件。
   - 打开 ServicesApp、安装 / 升级 MSI 或执行 `servicesctl migrate` 时，服务自动切换到新版本（重启服务后生效），
     不再被引用的旧版本目录随之删除。卸载时保留该目录（仍存在的服务依赖它运行），删除全部服务后执行一次 `migrate` 即可清空。
5. **命令行工具 `servicesctl`**：
   - 支持服务的增删改查、启停与日志查看，适合脚本与 AI 调用。

### 命令行工具

`servicesctl.exe`、`servicesvc.exe` 与 `ServicesApp.exe` 位于同一目录（MSI 安装后会自动加入系统 PATH）。
所有命令均为非交互式，支持 `--json` 输出与明确的退出码，完整说明见内置帮助：

```powershell
servicesctl --help            # 命令总览、退出码、权限说明
servicesctl help create       # 单个命令的完整帮助（选项、JSON 字段、示例）

servicesctl list --json
servicesctl create --name MyApp --exe "C:\Apps\my-app.exe" --args "--port 8080" --auto-restart --start
servicesctl update MyApp --max-restarts 0 --restart
servicesctl logs MyApp --tail 50 --stream stderr
servicesctl logs MyApp --since 10m   # 只看最近 10 分钟
servicesctl logs MyApp -f            # 实时跟踪，Ctrl+C 退出
servicesctl delete MyApp

servicesctl export -o services.json          # 导出全部服务配置（可纳入版本管理）
servicesctl apply -f services.json --dry-run # 查看变更计划
servicesctl apply -f services.json --restart # 声明式应用：创建 / 更新，加 --prune 删除多余服务

servicesctl doctor                           # 诊断：程序 / 目录 / 包装器 / 依赖 / 运行账户权限，附修复建议
```

`list` / `get` / `logs` 普通权限即可运行；其余会修改系统服务的命令需在管理员终端中执行。

### 文档

- [命令行参考](docs/cli.md)（由 `servicesctl` 内置帮助自动生成）
- [配置与数据存储](docs/configuration.md)：文件位置、注册表结构、声明式配置文件格式
- [架构](docs/architecture.md)：组件、Core 分层、包装器生命周期
- [故障排查](docs/troubleshooting.md)
- [参与开发](CONTRIBUTING.md)、[更新日志](CHANGELOG.md)

### 预览

![主界面](/.github/demo/1.png)
![主界面](/.github/demo/2.png)