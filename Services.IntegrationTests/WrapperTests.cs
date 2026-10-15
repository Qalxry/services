using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Services.IntegrationTests.Infrastructure;

namespace Services.IntegrationTests
{
    /// <summary>
    /// 受保护的版本化包装器目录（%ProgramData%\ServicesApp\wrapper）
    /// </summary>
    public class WrapperTests
    {
        public static string WrapperRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServicesApp", "wrapper");

        private static string WrapperOf(TestService service)
        {
            var imagePath = ConfigAndMigrateTests.ReadImagePath(service.Id);
            return imagePath.Split('"', StringSplitOptions.RemoveEmptyEntries)[0];
        }

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        /// <summary>
        /// 在 wrapper 目录下放置一个“旧版本”（当前包装器的副本），并把创建时间改早，使其可被清理
        /// </summary>
        private static string PlaceOldVersion(string name, string source)
        {
            var dir = Path.Combine(WrapperRoot, name);
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "servicesvc.exe");
            File.Copy(source, exe, overwrite: true);
            Directory.SetCreationTimeUtc(dir, DateTime.UtcNow.AddDays(-1));
            return exe;
        }

        [IntegrationFact]
        public void ServiceRunsProtectedCopyOfTemplate()
        {
            using var service = TestService.Create("Protected", "", "--start");
            var wrapper = WrapperOf(service);

            Assert.StartsWith(WrapperRoot + "\\", wrapper, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Hash(ItEnvironment.WrapperPath), Hash(wrapper));

            // 目录禁止继承；普通用户只能读取与执行
            var security = new DirectoryInfo(Path.GetDirectoryName(wrapper)!).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData |
                                           FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (sid == users || sid == authenticated || sid == everyone)
                    Assert.True(rule.AccessControlType == AccessControlType.Deny || (rule.FileSystemRights & write) == 0,
                        $"{sid} 拥有写权限: {rule.FileSystemRights}");
            }
            Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                security.GetOwner(typeof(SecurityIdentifier)));

            // 服务运行期间模板未被锁定，可直接替换（升级安装目录无需停止服务）
            var template = ItEnvironment.WrapperPath;
            File.Copy(template, template + ".bak", overwrite: true);
            File.Copy(template + ".bak", template, overwrite: true);
            File.Delete(template + ".bak");
            Assert.Equal("running", service.State);
        }

        [IntegrationFact]
        public void ServicesShareOneCopyPerVersion()
        {
            using var first = TestService.Create("ShareA", "", "--startup", "manual");
            using var second = TestService.Create("ShareB", "", "--startup", "manual");
            Assert.Equal(WrapperOf(first), WrapperOf(second), StringComparer.OrdinalIgnoreCase);
        }

        [IntegrationFact]
        public void MigrateRemovesUnreferencedVersions()
        {
            using var service = TestService.Create("Cleanup", "", "--startup", "manual");
            var stale = PlaceOldVersion("0.0.0.1-unreferenced0", ItEnvironment.WrapperPath);

            var result = Ctl.Json("migrate");

            Assert.Contains("0.0.0.1-unreferenced0", result.GetProperty("removedVersions").EnumerateArray().Select(v => v.GetString()));
            Assert.False(Directory.Exists(Path.GetDirectoryName(stale)));
            Assert.True(File.Exists(WrapperOf(service)), "当前版本不应被删除");
        }

        [IntegrationFact]
        public void RunningOldVersionIsRemovedAfterRestart()
        {
            using var service = TestService.Create("OldVersion", "", "--startup", "manual");
            var current = WrapperOf(service);
            var old = PlaceOldVersion("0.0.0.2-runningold00", current);
            ConfigAndMigrateTests.RunSc("config", service.Id, "binPath=", $"\"{old}\" --service-wrapper \"{service.Id}\"");
            service.Start();

            // 服务仍在运行旧版本：切换路径，但旧版本目录被占用而保留
            var first = Ctl.Json("migrate");
            Assert.Contains(service.Id, first.GetProperty("migrated").EnumerateArray().Select(m => m.GetString()));
            Assert.True(File.Exists(old), "运行中的旧版本不应被删除");
            Assert.Equal(current, WrapperOf(service), StringComparer.OrdinalIgnoreCase);

            // 重启后服务运行新版本，旧版本在下一次 migrate 中删除
            Ctl.Json("restart", service.Id);
            var second = Ctl.Json("migrate");
            Assert.Contains("0.0.0.2-runningold00", second.GetProperty("removedVersions").EnumerateArray().Select(v => v.GetString()));
            Assert.False(Directory.Exists(Path.GetDirectoryName(old)));
            Assert.Equal("running", service.State);
        }
    }
}
