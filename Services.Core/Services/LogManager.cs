using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Services.Core.Services
{
    /// <summary>
    /// 参与总大小统计的日志文件信息
    /// </summary>
    public readonly record struct LogFileEntry(string Path, long Length, DateTime LastWriteTime);

    public class LogManager
    {
        private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "windows_service_logs");
        private const int DefaultRetentionDays = 7;
        private const int DefaultMaxTotalSizeMB = 1024;

        public LogManager()
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
        }

        public string GetLogDirectory()
        {
            return LogDirectory;
        }

        public string? GetLatestLogPath(string serviceName)
        {
            if (!Directory.Exists(LogDirectory)) return null;

            string? latestFile = null;
            DateTime latestTime = DateTime.MinValue;

            foreach (var file in Directory.EnumerateFiles(LogDirectory, $"{serviceName}_*.log"))
            {
                try
                {
                    var creationTime = File.GetCreationTime(file);
                    if (creationTime > latestTime)
                    {
                        latestTime = creationTime;
                        latestFile = file;
                    }
                }
                catch { }
            }

            return latestFile;
        }

        /// <summary>
        /// 按全局设置（保留天数、总大小上限）清理日志
        /// </summary>
        public void CleanupLogs()
        {
            CleanupOldLogs(GetGlobalRetentionDays(), GetGlobalMaxTotalSizeMB());
        }

        /// <summary>
        /// 先删除超过保留天数的日志，再在总大小超过上限时从最旧的文件开始删除。
        /// 正在写入（被占用）而无法删除的文件会被跳过。
        /// </summary>
        /// <param name="maxTotalSizeMB">日志总大小上限（MB），0 表示不限制</param>
        public void CleanupOldLogs(int retentionDays = DefaultRetentionDays, int maxTotalSizeMB = DefaultMaxTotalSizeMB)
        {
            if (!Directory.Exists(LogDirectory)) return;

            try
            {
                var cutoffDate = DateTime.Now.AddDays(-retentionDays);

                foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
                {
                    try
                    {
                        if (File.GetCreationTime(file) < cutoffDate)
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to delete log {file}: {ex.Message}");
                    }
                }

                CleanupBySize(maxTotalSizeMB);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CleanupOldLogs failed: {ex.Message}");
            }
        }

        private static void CleanupBySize(int maxTotalSizeMB)
        {
            if (maxTotalSizeMB <= 0) return;
            long maxTotalBytes = maxTotalSizeMB * 1024L * 1024L;

            var files = new List<LogFileEntry>();
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
            {
                try
                {
                    var info = new FileInfo(file);
                    files.Add(new LogFileEntry(file, info.Length, info.LastWriteTime));
                }
                catch { }
            }

            // 删除失败的文件仍计入总大小但不再选中，换更新的文件补足，直到没有新的失败
            var undeletable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var candidates = SelectFilesToDelete(files, maxTotalBytes, undeletable);
                bool anyFailed = false;

                foreach (var candidate in candidates)
                {
                    try
                    {
                        File.Delete(candidate.Path);
                        files.Remove(candidate);
                    }
                    catch (Exception ex)
                    {
                        undeletable.Add(candidate.Path);
                        anyFailed = true;
                        System.Diagnostics.Debug.WriteLine($"Failed to delete log {candidate.Path}: {ex.Message}");
                    }
                }

                if (!anyFailed) break;
            }
        }

        /// <summary>
        /// 计算总大小超过上限时需要删除的文件：按最后写入时间从旧到新依次选取，直到剩余总大小不超过上限。
        /// 上限 &lt;= 0 表示不限制。undeletable 中的文件仍计入总大小，但不会被选中。
        /// </summary>
        public static List<LogFileEntry> SelectFilesToDelete(IEnumerable<LogFileEntry> files, long maxTotalBytes, ISet<string>? undeletable = null)
        {
            var result = new List<LogFileEntry>();
            if (maxTotalBytes <= 0) return result;

            var ordered = files
                .OrderBy(f => f.LastWriteTime)
                .ThenBy(f => f.Path, StringComparer.Ordinal)
                .ToList();

            long total = ordered.Sum(f => f.Length);
            foreach (var file in ordered)
            {
                if (total <= maxTotalBytes) break;
                if (undeletable != null && undeletable.Contains(file.Path)) continue;

                result.Add(file);
                total -= file.Length;
            }
            return result;
        }

        public static int GetGlobalRetentionDays()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WindowsServiceManager");
                if (key != null)
                {
                    var val = key.GetValue("LogRetentionDays");
                    if (val is int days) return days;
                }
            }
            catch { }
            return DefaultRetentionDays;
        }

        /// <summary>
        /// 日志总大小上限（MB），0 表示不限制，默认 1024
        /// </summary>
        public static int GetGlobalMaxTotalSizeMB()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WindowsServiceManager");
                if (key?.GetValue("LogMaxTotalSizeMB") is int mb && mb >= 0) return mb;
            }
            catch { }
            return DefaultMaxTotalSizeMB;
        }

        public static void SetGlobalMaxTotalSizeMB(int mb)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WindowsServiceManager");
                key.SetValue("LogMaxTotalSizeMB", mb);
            }
            catch { }
        }

        /// <summary>
        /// 日志查看器是否自动换行（默认开启）
        /// </summary>
        public static bool GetLogWordWrap()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WindowsServiceManager");
                if (key?.GetValue("LogWordWrap") is int val) return val != 0;
            }
            catch { }
            return true;
        }

        public static void SetLogWordWrap(bool wrap)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WindowsServiceManager");
                key.SetValue("LogWordWrap", wrap ? 1 : 0);
            }
            catch { }
        }

        /// <summary>
        /// 日志查看器是否显示行首时间戳（默认开启）
        /// </summary>
        public static bool GetLogShowTimestamps()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WindowsServiceManager");
                if (key?.GetValue("LogShowTimestamps") is int val) return val != 0;
            }
            catch { }
            return true;
        }

        public static void SetLogShowTimestamps(bool show)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WindowsServiceManager");
                key.SetValue("LogShowTimestamps", show ? 1 : 0);
            }
            catch { }
        }

        public static void SetGlobalRetentionDays(int days)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\WindowsServiceManager");
                key.SetValue("LogRetentionDays", days);
            }
            catch { }
        }
    }
}
