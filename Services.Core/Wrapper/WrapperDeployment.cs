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

namespace Services.Core.Wrapper
{
    /// <summary>
    /// 包装器的受保护部署。安装目录（或解压目录）中的 servicesvc.exe 只是模板，服务运行的是复制到
    /// %ProgramData%\ServicesApp\wrapper\&lt;版本&gt;-&lt;哈希&gt;\servicesvc.exe 的副本：
    /// - 升级或替换安装目录中的文件不会被正在运行的服务锁定；
    /// - 目录只允许 SYSTEM 与 Administrators 修改（禁止继承），即使程序被解压在普通用户可写的目录，
    ///   以 SYSTEM 运行的服务也不会执行可被普通用户替换的程序。
    /// 不再被任何服务引用、且未被占用的旧版本目录在 <see cref="CleanupUnused"/> 中删除。
    /// 需要管理员权限。
    /// </summary>
    public static class WrapperDeployment
    {
        private const int HashLength = 12;
        // 刚部署、尚未被服务引用的目录（另一进程正在创建服务）不清理
        private static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);

        public static string ProductDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServicesApp");

        public static string RootDirectory => Path.Combine(ProductDirectory, "wrapper");

        /// <summary>
        /// 将模板部署到受保护目录（内容相同的版本已存在时直接复用），返回部署后的包装器路径
        /// </summary>
        public static string Deploy(string templatePath)
        {
            var hash = ComputeHash(templatePath);
            var version = FileVersionInfo.GetVersionInfo(templatePath).FileVersion;
            var directory = Path.Combine(RootDirectory, GetVersionDirectoryName(version, hash));
            var target = Path.Combine(directory, ServiceUtils.WrapperExeName);

            EnsureSecureDirectory(ProductDirectory);
            EnsureSecureDirectory(RootDirectory);

            if (Directory.Exists(directory) && !IsReparsePoint(directory) &&
                File.Exists(target) && ComputeHash(target) == hash)
            {
                EnsureSecureDirectory(directory);
                return target;
            }

            // 目录不完整或内容不符（如复制中断）：重建
            if (Directory.Exists(directory))
            {
                if (IsReparsePoint(directory)) Directory.Delete(directory);
                else Directory.Delete(directory, recursive: true);
            }
            EnsureSecureDirectory(directory);

            var temp = target + ".tmp";
            File.Copy(templatePath, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);

            if (ComputeHash(target) != hash)
                throw new IOException($"Wrapper copy verification failed: {target}");
            return target;
        }

        /// <summary>
        /// 删除不再被任何服务引用的版本目录。被占用（服务仍在运行旧版本）的目录跳过，留待下次清理。
        /// 引用关系取自系统中全部服务的 ImagePath（不限于本工具索引中的服务），避免误删。
        /// </summary>
        /// <returns>已删除的版本目录名</returns>
        public static List<string> CleanupUnused()
        {
            var removed = new List<string>();
            if (!Directory.Exists(RootDirectory)) return removed;

            var referenced = ReadAllWrapperExecutables();
            foreach (var directory in SelectUnused(Directory.GetDirectories(RootDirectory), referenced))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(directory) < MinimumAge) continue;

                    if (IsReparsePoint(directory)) Directory.Delete(directory);
                    else Directory.Delete(directory, recursive: true);
                    removed.Add(Path.GetFileName(directory));
                }
                catch (IOException)
                {
                    // 包装器正在运行（文件被锁定）
                }
                catch (UnauthorizedAccessException)
                {
                    // 同上（正在运行的映像文件拒绝删除）
                }
            }
            return removed;
        }

        /// <summary>
        /// 版本目录名：&lt;文件版本&gt;-&lt;内容哈希前 12 位&gt;。同一版本号的不同构建（如开发版）不会互相覆盖。
        /// </summary>
        public static string GetVersionDirectoryName(string? version, string hash)
        {
            var safeVersion = new string((version ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
            if (safeVersion.Length == 0) safeVersion = "0.0.0.0";
            return $"{safeVersion}-{hash[..Math.Min(HashLength, hash.Length)].ToLowerInvariant()}";
        }

        /// <summary>
        /// 从候选版本目录中选出没有被任何包装器路径引用的目录（路径比较不区分大小写）
        /// </summary>
        public static IEnumerable<string> SelectUnused(IEnumerable<string> directories, IEnumerable<string> referencedExecutables)
        {
            // 按 Windows 路径规则手工处理（同时接受 / 与 \），使纯逻辑在任何平台上的结果一致
            static string TrimSeparators(string path) => path.TrimEnd('\\', '/');
            static string? Parent(string path)
            {
                int index = TrimSeparators(path).LastIndexOfAny(new[] { '\\', '/' });
                return index > 0 ? path.Substring(0, index) : null;
            }

            var referencedDirectories = new HashSet<string>(
                referencedExecutables.Select(Parent).Where(dir => dir != null).Select(dir => TrimSeparators(dir!)),
                StringComparer.OrdinalIgnoreCase);

            return directories.Where(dir => !referencedDirectories.Contains(TrimSeparators(dir)));
        }

        private static List<string> ReadAllWrapperExecutables()
        {
            var result = new List<string>();
            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return result;

            foreach (var name in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var key = servicesKey.OpenSubKey(name);
                    var imagePath = key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (ServiceUtils.ParseWrapperExecutable(imagePath) is { } exe) result.Add(exe);
                }
                catch
                {
                    // 个别服务键无权读取
                }
            }
            return result;
        }

        /// <summary>
        /// 创建目录（已存在则复用）并设置显式 ACL：所有者 Administrators；SYSTEM、Administrators 完全控制，
        /// Users 读取与执行；禁止继承上级权限。目录若是联接点（可能被预先放置以重定向写入），先删除再创建。
        /// </summary>
        private static void EnsureSecureDirectory(string path)
        {
            if (Directory.Exists(path) && IsReparsePoint(path))
                Directory.Delete(path);
            Directory.CreateDirectory(path);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var security = new DirectorySecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }

        private static bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static string ComputeHash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}
