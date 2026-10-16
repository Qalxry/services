using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Services.Core.Models;

namespace Services.Core.Services
{
    /// <summary>
    /// 状态文件使用源生成序列化（包装器以裁剪方式发布，不能使用基于反射的序列化）
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(typeof(ServiceRuntimeState))]
    public partial class ServiceStateJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// 服务运行状态文件的读写。状态文件位于日志目录下的 state 子目录：
    /// %ProgramData%\windows_service_logs\state\&lt;服务ID&gt;.json。
    /// 使用文件而非 HKLM，是因为服务可能以低权限账户运行（日志目录已授予这些账户写权限，子目录继承）。
    /// 写入失败绝不影响服务运行：所有异常都被吞掉，仅输出调试信息。
    /// </summary>
    public class ServiceStateStore
    {
        private const string StateDirectoryName = "state";

        private readonly string _stateDirectory;

        /// <param name="logDirectory">日志目录，缺省为 <see cref="LogManager"/> 使用的目录</param>
        public ServiceStateStore(string? logDirectory = null)
        {
            _stateDirectory = Path.Combine(logDirectory ?? new LogManager().GetLogDirectory(), StateDirectoryName);
        }

        public string GetStatePath(string serviceId) => Path.Combine(_stateDirectory, GetFileName(serviceId));

        public static string Serialize(ServiceRuntimeState state) =>
            JsonSerializer.Serialize(state, ServiceStateJsonContext.Default.ServiceRuntimeState);

        /// <summary>内容无法解析时返回 null</summary>
        public static ServiceRuntimeState? Deserialize(string json)
        {
            try { return JsonSerializer.Deserialize(json, ServiceStateJsonContext.Default.ServiceRuntimeState); }
            catch (JsonException) { return null; }
        }

        // 读取方（GUI 刷新、servicesctl get）正打开状态文件时覆盖移动会失败：即使以 FileShare.Delete 打开，
        // 文件在关闭前仍占用文件名。读取只需几毫秒，短暂重试即可，避免丢失状态更新（如失败原因）。
        private const int MoveAttempts = 10;
        private const int MoveRetryDelayMs = 20;

        /// <summary>
        /// 原子写入：先写临时文件，再覆盖移动到目标位置，读取方不会看到写了一半的内容。返回是否成功。
        /// </summary>
        public bool Write(ServiceRuntimeState state)
        {
            string? tempPath = null;
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var path = GetStatePath(state.ServiceId);
                tempPath = path + ".tmp";
                File.WriteAllText(tempPath, Serialize(state));
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(tempPath, path, overwrite: true);
                        return true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
                    {
                        System.Threading.Thread.Sleep(MoveRetryDelayMs);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write state file for {state.ServiceId}: {ex.Message}");
                try { if (tempPath != null) File.Delete(tempPath); } catch { }
                return false;
            }
        }

        /// <summary>文件不存在或无法读取 / 解析时返回 null</summary>
        public ServiceRuntimeState? Read(string serviceId)
        {
            try
            {
                var path = GetStatePath(serviceId);
                if (!File.Exists(path)) return null;

                // 允许写入方同时替换文件（FileShare.Delete），否则读取期间的覆盖移动会失败
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return Deserialize(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read state file for {serviceId}: {ex.Message}");
                return null;
            }
        }

        public void Delete(string serviceId)
        {
            try
            {
                File.Delete(GetStatePath(serviceId));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete state file for {serviceId}: {ex.Message}");
            }
        }

        // 服务 ID 本身只含字母数字与下划线；仍替换非法字符，避免路径穿越
        private static string GetFileName(string serviceId)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = serviceId.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            return new string(chars) + ".json";
        }
    }
}
