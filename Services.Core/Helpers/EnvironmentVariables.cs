using System;
using System.Collections.Generic;
using System.Linq;

namespace Services.Core.Helpers
{
    /// <summary>
    /// 服务级环境变量的解析、格式化与校验（无 Windows 依赖）。
    /// 存储格式为注册表 REG_MULTI_SZ，每项 KEY=VALUE；变量名不区分大小写（与 Windows 一致）。
    /// </summary>
    public static class EnvironmentVariables
    {
        /// <summary>
        /// 解析 KEY=VALUE 行。忽略空行与行首尾空白；按第一个 "=" 拆分，值可为空（KEY=），值中可再含 "="；
        /// 重复的键以后者为准。格式错误时抛出 ArgumentException，消息指明出错的行。
        /// </summary>
        public static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int lineNumber = 0;
            foreach (var line in lines)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var error = TryParseEntry(line, out var key, out var value);
                if (error != null)
                    throw new ArgumentException($"环境变量第 {lineNumber} 行无效：{error}（'{line.Trim()}'）。");
                result[key] = value;
            }
            return result;
        }

        /// <summary>
        /// 解析多行文本（\r、\n、\r\n 均可作为换行），规则同 <see cref="Parse"/>
        /// </summary>
        public static Dictionary<string, string> ParseText(string? text)
        {
            return Parse((text ?? "").Split(new[] { '\r', '\n' }));
        }

        /// <summary>
        /// 宽松解析：跳过格式错误的行而不抛出异常。用于读取注册表中的配置（可能被手工修改），
        /// 避免个别坏项导致整个服务无法加载或启动。
        /// </summary>
        public static Dictionary<string, string> ParseLenient(IEnumerable<string>? lines)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (lines == null) return result;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (TryParseEntry(line, out var key, out var value) == null)
                    result[key] = value;
            }
            return result;
        }

        /// <summary>
        /// 解析单个 KEY=VALUE（如命令行 --env 的参数）。格式错误时抛出 ArgumentException。
        /// </summary>
        public static KeyValuePair<string, string> ParseEntry(string entry)
        {
            var error = TryParseEntry(entry ?? "", out var key, out var value);
            if (error != null)
                throw new ArgumentException($"环境变量 '{(entry ?? "").Trim()}' 无效：{error}。");
            return new KeyValuePair<string, string>(key, value);
        }

        /// <summary>
        /// 格式化为 KEY=VALUE 行（按键排序，便于稳定显示），行间以 "\n" 分隔；没有变量时返回空字符串
        /// </summary>
        public static string Format(IReadOnlyDictionary<string, string> environment)
        {
            return string.Join("\n", FormatLines(environment));
        }

        /// <summary>
        /// 格式化为 KEY=VALUE 的行序列（按键排序），即注册表 REG_MULTI_SZ 的内容
        /// </summary>
        public static string[] FormatLines(IReadOnlyDictionary<string, string> environment)
        {
            return environment
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")
                .ToArray();
        }

        /// <summary>
        /// 校验整个变量集合（用于保存配置前）：键不能为空或含 "="、NUL、控制字符；值不能含换行与 NUL。
        /// 不合法时抛出 ArgumentException。
        /// </summary>
        public static void Validate(IReadOnlyDictionary<string, string> environment)
        {
            foreach (var (key, value) in environment)
            {
                var error = ValidateKey(key) ?? ValidateValue(value);
                if (error != null)
                    throw new ArgumentException($"环境变量 '{key}' 无效：{error}。");
            }
        }

        /// <summary>
        /// 复制为新的可变字典（键不区分大小写），避免多个对象共享同一实例
        /// </summary>
        public static Dictionary<string, string> Clone(IReadOnlyDictionary<string, string>? environment)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (environment != null)
            {
                foreach (var (key, value) in environment)
                    result[key] = value;
            }
            return result;
        }

        /// <summary>
        /// 将变量合并到目标环境（如 ProcessStartInfo.Environment）：同名变量覆盖继承的值。
        /// 值先经 expand 展开（运行时传入 Environment.ExpandEnvironmentVariables，支持 PATH=%PATH%;C:\tools）。
        /// </summary>
        public static void Apply(IDictionary<string, string?> target, IReadOnlyDictionary<string, string> environment, Func<string, string> expand)
        {
            foreach (var (key, value) in environment)
                target[key] = expand(value);
        }

        /// <summary>
        /// 解析单个条目，成功返回 null，否则返回错误原因
        /// </summary>
        private static string? TryParseEntry(string entry, out string key, out string value)
        {
            key = "";
            value = "";

            var text = entry.Trim();
            int eq = text.IndexOf('=');
            if (eq < 0) return "缺少 '='，应为 KEY=VALUE 格式";

            key = text.Substring(0, eq).Trim();
            value = text.Substring(eq + 1);

            return ValidateKey(key) ?? ValidateValue(value);
        }

        private static string? ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "变量名不能为空";
            if (key != key.Trim()) return "变量名首尾不能有空白";
            if (key.Contains('=')) return "变量名不能包含 '='";
            if (key.Any(char.IsControl)) return "变量名不能包含控制字符";
            return null;
        }

        private static string? ValidateValue(string value)
        {
            if (value.Contains('\0')) return "值不能包含 NUL 字符";
            if (value.Contains('\n') || value.Contains('\r')) return "值不能包含换行";
            return null;
        }
    }
}
