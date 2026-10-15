using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using Services.Core.Helpers;
using Services.Core.Models;
using Services.Core.Scm;
using Services.Core.Services;
using Services.Core.Storage;
using Services.Core.Wrapper;

namespace Services.Core.Diagnostics
{
    /// <summary>
    /// 逐项检查服务能否正常运行，并给出修复建议（servicesctl doctor）。
    /// 检查只读取信息，不修改任何配置；部分检查需要管理员权限，无权限时标记为跳过。
    /// </summary>
    public sealed class ServiceDoctor
    {
        private const string Tool = "servicesctl";

        // 读取并执行文件 / 进入并列出目录；写入日志目录
        private const int ReadExecuteFile = (int)(FileSystemRights.ReadData | FileSystemRights.ExecuteFile);
        private const int ReadDirectory = (int)(FileSystemRights.ListDirectory | FileSystemRights.Traverse);
        private const int WriteDirectory = (int)(FileSystemRights.CreateFiles | FileSystemRights.WriteData);

        private readonly LogManager _logs;
        private readonly string? _wrapperTemplate;

        /// <param name="wrapperTemplate">当前的包装器模板（与 servicesctl 同目录的 servicesvc.exe），找不到时为 null</param>
        public ServiceDoctor(LogManager logs, string? wrapperTemplate)
        {
            _logs = logs;
            _wrapperTemplate = wrapperTemplate;
        }

        public List<DiagnosticResult> CheckGlobal(bool isAdministrator)
        {
            var results = new List<DiagnosticResult>
            {
                isAdministrator
                    ? DiagnosticResult.Ok(null, "admin", "以管理员权限运行，全部检查可用")
                    : DiagnosticResult.Warning(null, "admin", "未以管理员权限运行，部分检查（敏感变量、事件日志源）将跳过",
                        "在“以管理员身份运行”的终端中重新执行"),
                _wrapperTemplate != null
                    ? DiagnosticResult.Ok(null, "wrapper-template", $"包装器模板: {_wrapperTemplate}")
                    : DiagnosticResult.Warning(null, "wrapper-template", $"未找到包装器模板 {ServiceUtils.WrapperExeName}，无法检查服务是否使用当前版本",
                        $"{ServiceUtils.WrapperExeName} 应与 {Tool}.exe 位于同一目录")
            };

            var logDir = _logs.GetLogDirectory();
            results.Add(Directory.Exists(logDir)
                ? DiagnosticResult.Ok(null, "log-directory", $"日志目录: {logDir}")
                : DiagnosticResult.Warning(null, "log-directory", $"日志目录不存在: {logDir}（服务首次启动时创建）"));
            return results;
        }

        public List<DiagnosticResult> CheckService(Service service, ServiceRuntimeState? runtime, bool isAdministrator)
        {
            var id = service.Id;
            var results = new List<DiagnosticResult>();
            void Add(DiagnosticResult result) => results.Add(result);

            // 程序与工作目录
            bool exeExists = File.Exists(service.ExePath);
            Add(exeExists
                ? DiagnosticResult.Ok(id, "exe", $"程序存在: {service.ExePath}")
                : DiagnosticResult.Error(id, "exe", $"程序不存在: {service.ExePath}", $"{Tool} update {id} --exe <正确路径>"));

            var workDir = string.IsNullOrWhiteSpace(service.WorkingDir) ? Path.GetDirectoryName(service.ExePath) : service.WorkingDir;
            bool workDirExists = !string.IsNullOrEmpty(workDir) && Directory.Exists(workDir);
            Add(workDirExists
                ? DiagnosticResult.Ok(id, "workdir", $"工作目录存在: {workDir}")
                : DiagnosticResult.Error(id, "workdir", $"工作目录不存在: {workDir}", $"{Tool} update {id} --workdir <目录>"));

            Add(CheckWrapper(service));
            Add(CheckParameters(service));
            Add(CheckDependencies(service));
            Add(CheckEventSource(service, isAdministrator));

            // 运行账户的权限（LocalSystem 拥有全部权限，无需检查）
            if (service.AccountType == ServiceAccountType.LocalSystem)
            {
                Add(DiagnosticResult.Ok(id, "account", "以 LocalSystem 运行，无需检查文件权限"));
            }
            else if (ResolveAccountSid(service) is not { } sid)
            {
                Add(DiagnosticResult.Error(id, "account", $"无法解析运行账户 '{service.AccountName}'",
                    $"{Tool} update {id} --account <账户>"));
            }
            else
            {
                var sids = AccessEvaluator.CandidateSids(sid);
                if (exeExists)
                    Add(CheckAccess(id, "account-read-exe", service.ExePath, isDirectory: false, sids, ReadExecuteFile,
                        "读取并执行程序", service.AccountName));
                if (workDirExists)
                    Add(CheckAccess(id, "account-read-workdir", workDir!, isDirectory: true, sids, ReadDirectory,
                        "进入工作目录", service.AccountName));
                var logDir = _logs.GetLogDirectory();
                if (Directory.Exists(logDir))
                    Add(CheckAccess(id, "account-write-logs", logDir, isDirectory: true, sids, WriteDirectory,
                        "写入日志目录", service.AccountName, $"重新保存服务即可自动授权：{Tool} update {id} --account {AccountOption(service)}"));
                Add(CheckSecretsAcl(service, sid, isAdministrator));
            }

            Add(CheckRuntime(service, runtime));
            return results;
        }

        private DiagnosticResult CheckWrapper(Service service)
        {
            var id = service.Id;
            var imagePath = ServiceRegistryStore.ReadImagePath(id);
            var wrapper = ServiceUtils.ParseWrapperExecutable(imagePath);
            if (wrapper == null)
                return DiagnosticResult.Error(id, "wrapper", $"服务的启动命令不是由包装器运行: {imagePath}",
                    "删除后重新创建该服务");
            if (!File.Exists(wrapper))
                return DiagnosticResult.Error(id, "wrapper", $"包装器不存在: {wrapper}", $"{Tool} migrate");

            bool isProtected = wrapper.StartsWith(WrapperDeployment.RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!isProtected)
                return DiagnosticResult.Warning(id, "wrapper",
                    $"包装器不在受保护目录中: {wrapper}（安装目录被锁定、可能被普通用户替换）", $"{Tool} migrate，然后重启服务");

            if (_wrapperTemplate != null && File.Exists(_wrapperTemplate) && !SameContent(wrapper, _wrapperTemplate))
                return DiagnosticResult.Warning(id, "wrapper", $"包装器不是当前版本: {wrapper}", $"{Tool} migrate，然后重启服务");

            return DiagnosticResult.Ok(id, "wrapper", $"包装器: {wrapper}");
        }

        private static DiagnosticResult CheckParameters(Service service)
        {
            var id = service.Id;
            var problems = new List<string>();
            if (service.StopMode == ServiceStopMode.Command && string.IsNullOrWhiteSpace(service.StopCommand))
                problems.Add("停止方式为 command 但没有停止命令（将退回直接结束进程）");
            if (service.RestartDelaySeconds is < 1 or > RestartPolicySettings.MaxDelaySeconds)
                problems.Add($"重启延迟 {service.RestartDelaySeconds} 秒超出范围");
            if (service.RestartResetMinutes < 1)
                problems.Add($"重启计数重置时间 {service.RestartResetMinutes} 分钟无效");

            return problems.Count == 0
                ? DiagnosticResult.Ok(id, "parameters", "服务参数完整")
                : DiagnosticResult.Warning(id, "parameters", string.Join("；", problems), $"{Tool} update {id} ...（见 {Tool} help update）");
        }

        private static DiagnosticResult CheckDependencies(Service service)
        {
            var id = service.Id;
            if (service.Dependencies.Count == 0) return DiagnosticResult.Ok(id, "dependencies", "没有依赖服务");

            var missing = service.Dependencies.Where(d => !d.StartsWith('+') && !ScmClient.Exists(d)).ToList();
            return missing.Count == 0
                ? DiagnosticResult.Ok(id, "dependencies", $"依赖服务均存在: {string.Join(", ", service.Dependencies)}")
                : DiagnosticResult.Error(id, "dependencies", $"依赖的服务不存在: {string.Join(", ", missing)}（服务将无法启动）",
                    $"安装缺失的服务，或 {Tool} update {id} --depends-on <服务>... / --clear-depends");
        }

        private static DiagnosticResult CheckEventSource(Service service, bool isAdministrator)
        {
            var id = service.Id;
            try
            {
                return EventLog.SourceExists(id)
                    ? DiagnosticResult.Ok(id, "event-source", "已注册 Windows 事件日志源")
                    : DiagnosticResult.Warning(id, "event-source", "未注册 Windows 事件日志源，低权限账户运行时无法写入事件日志",
                        $"以管理员身份重新保存服务即可注册，如：{Tool} update {id} --startup {ServiceStartupModes.ToCliValue(service.StartupType, service.DelayedAutoStart)}");
            }
            catch (Exception) when (!isAdministrator)
            {
                return DiagnosticResult.Skipped(id, "event-source", "需要管理员权限才能检查事件日志源");
            }
            catch (Exception ex)
            {
                return DiagnosticResult.Warning(id, "event-source", $"无法检查事件日志源: {ex.Message}");
            }
        }

        private static DiagnosticResult CheckAccess(string id, string check, string path, bool isDirectory,
            IReadOnlyCollection<string> sids, int required, string what, string? account, string? fix = null)
        {
            List<AceInfo> aces;
            try
            {
                FileSystemSecurity security = isDirectory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
                aces = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .Where(rule => (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0)
                    .Select(rule => new AceInfo(rule.IdentityReference.Value, (int)rule.FileSystemRights, rule.AccessControlType == AccessControlType.Allow))
                    .ToList();
            }
            catch (Exception ex)
            {
                return DiagnosticResult.Skipped(id, check, $"无法读取 {path} 的权限: {ex.Message}");
            }

            return AccessEvaluator.Evaluate(aces, sids, required) switch
            {
                AccessVerdict.Granted => DiagnosticResult.Ok(id, check, $"运行账户可以{what}: {path}"),
                AccessVerdict.Denied => DiagnosticResult.Error(id, check, $"运行账户 {account} 被拒绝{what}: {path}",
                    fix ?? $"icacls \"{path}\" /grant \"{account}:(RX)\""),
                _ => DiagnosticResult.Warning(id, check, $"未发现允许运行账户 {account} {what}的权限规则: {path}（若通过其他组获得权限可忽略）",
                    fix ?? $"icacls \"{path}\" /grant \"{account}:(RX)\"")
            };
        }

        private static DiagnosticResult CheckSecretsAcl(Service service, string accountSid, bool isAdministrator)
        {
            var id = service.Id;
            if (service.SecretEnvironment.Count == 0) return DiagnosticResult.Ok(id, "secrets", "没有敏感环境变量");
            if (!isAdministrator) return DiagnosticResult.Skipped(id, "secrets", "需要管理员权限才能检查敏感环境变量的权限");

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegistryPaths.Secrets(id));
                if (key == null)
                    return DiagnosticResult.Error(id, "secrets", "敏感环境变量的值缺失", $"{Tool} update {id} --secret-env KEY=VALUE");

                var granted = key.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<RegistryAccessRule>()
                    .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                                 rule.IdentityReference.Value == accountSid &&
                                 (rule.RegistryRights & RegistryRights.QueryValues) != 0);
                return granted
                    ? DiagnosticResult.Ok(id, "secrets", "运行账户可以读取敏感环境变量")
                    : DiagnosticResult.Error(id, "secrets", "运行账户无权读取敏感环境变量（这些变量不会被设置）",
                        $"重新保存服务即可更新权限：{Tool} update {id} --account {AccountOption(service)}");
            }
            catch (Exception ex)
            {
                return DiagnosticResult.Warning(id, "secrets", $"无法检查敏感环境变量的权限: {ex.Message}");
            }
        }

        private static DiagnosticResult CheckRuntime(Service service, ServiceRuntimeState? runtime)
        {
            var id = service.Id;
            if (service.State == ServiceRunState.Stopped && service.StartupType == ServiceStartupType.Auto &&
                !string.IsNullOrWhiteSpace(runtime?.LastFailureReason))
            {
                return DiagnosticResult.Warning(id, "runtime",
                    $"自动启动的服务已停止，最近失败原因: {runtime!.LastFailureReason}", $"{Tool} logs {id} --tail 50");
            }
            return DiagnosticResult.Ok(id, "runtime", runtime?.LastFailureReason is { Length: > 0 } reason
                ? $"最近失败原因（已恢复）: {reason}"
                : "没有失败记录");
        }

        private static string? ResolveAccountSid(Service service)
        {
            try
            {
                var objectName = string.IsNullOrWhiteSpace(service.AccountName)
                    ? ServiceAccounts.ToObjectName(service.AccountType, service.Id, null)
                    : service.AccountName;
                return ServiceAccounts.ResolveSid(objectName).Value;
            }
            catch
            {
                return null;
            }
        }

        private static string AccountOption(Service service) => service.AccountType switch
        {
            ServiceAccountType.LocalService => "localservice",
            ServiceAccountType.NetworkService => "networkservice",
            ServiceAccountType.Virtual => "virtual",
            ServiceAccountType.User => $"{service.AccountName} --password-stdin",
            _ => "localsystem"
        };

        private static bool SameContent(string a, string b)
        {
            try
            {
                using var fa = File.OpenRead(a);
                using var fb = File.OpenRead(b);
                return fa.Length == fb.Length && SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
            }
            catch
            {
                return true;
            }
        }
    }
}
