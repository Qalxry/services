using System;
using System.Collections.Generic;
using System.Linq;

namespace Services.Core.Helpers
{
    /// <summary>
    /// 服务依赖（本服务启动前需先启动的服务）的解析、格式化与校验（无 Windows 依赖）。
    /// 写入 SCM 的 lpDependencies 为以 NUL 分隔的服务名列表；读取自服务键 DependOnService（REG_MULTI_SZ）。
    /// 服务名不区分大小写（与 Windows 一致）；不支持依赖组（以 "+" 开头）。
    /// </summary>
    public static class ServiceDependencies
    {
        /// <summary>Windows 服务名的最大长度</summary>
        public const int MaxNameLength = 256;

        /// <summary>
        /// 规范化依赖列表：去除首尾空白、忽略空项，按不区分大小写去重（保留首次出现的写法与顺序）。
        /// 返回新的可变列表。
        /// </summary>
        public static List<string> Normalize(IEnumerable<string?>? names)
        {
            var result = new List<string>();
            if (names == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                var trimmed = name?.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (seen.Add(trimmed)) result.Add(trimmed);
            }
            return result;
        }

        /// <summary>
        /// 解析用户输入的依赖文本：逗号（含全角）、分号、换行均可作为分隔符，随后规范化
        /// </summary>
        public static List<string> ParseText(string? text)
        {
            return Normalize((text ?? "").Split(new[] { ',', '，', ';', '；', '\r', '\n' }));
        }

        /// <summary>
        /// 格式化为单行文本（逗号加空格分隔），没有依赖时返回空字符串
        /// </summary>
        public static string Format(IEnumerable<string>? names)
        {
            return string.Join(", ", names ?? Enumerable.Empty<string>());
        }

        /// <summary>
        /// 宽松读取注册表 DependOnService：规范化并跳过依赖组（以 "+" 开头），避免个别坏项导致服务无法加载
        /// </summary>
        public static List<string> ParseRegistry(IEnumerable<string>? values)
        {
            return Normalize(values?.Where(value => !IsGroup(value)));
        }

        /// <summary>
        /// 复制为新的可变列表，避免多个对象共享同一实例
        /// </summary>
        public static List<string> Clone(IEnumerable<string>? names)
        {
            return names == null ? new List<string>() : new List<string>(names);
        }

        /// <summary>
        /// 构造 ChangeServiceConfig / CreateService 的 lpDependencies 参数：每项以 NUL 结尾（"A\0B\0"），
        /// P/Invoke 封送字符串时会再追加终止符，从而以双 NUL 结束。没有依赖时返回空字符串（表示清空依赖）。
        /// 调用前应已通过 <see cref="Validate"/>。
        /// </summary>
        public static string BuildMultiString(IEnumerable<string>? names)
        {
            return string.Concat(Normalize(names).Select(name => name + "\0"));
        }

        /// <summary>
        /// 校验依赖名称的格式（不检查服务是否存在）：不能为空、不能含 NUL 或控制字符、不能含 "/" 与 "\"、
        /// 不能以 "+" 开头（依赖组）、长度不超过 256；selfId 不为 null 时不能依赖自身（不区分大小写）。
        /// 不合法时抛出 ArgumentException，消息指明出错的名称。
        /// </summary>
        public static void Validate(IEnumerable<string>? names, string? selfId = null)
        {
            if (names == null) return;

            foreach (var name in names)
            {
                var error = ValidateName(name);
                if (error != null)
                    throw new ArgumentException($"依赖服务 '{name}' 无效：{error}。");
                if (selfId != null && string.Equals(name.Trim(), selfId, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"服务不能依赖自身（'{name.Trim()}'）。");
            }
        }

        /// <summary>
        /// 校验依赖的服务都存在，缺失时抛出 ArgumentException（列出所有不存在的服务名）。
        /// serviceExists 由调用方提供（运行时查询 SCM），便于脱离 Windows 测试。
        /// </summary>
        public static void EnsureExist(IEnumerable<string> names, Func<string, bool> serviceExists)
        {
            var missing = names.Where(name => !serviceExists(name)).ToList();
            if (missing.Count > 0)
                throw new ArgumentException(
                    $"依赖的服务不存在：{string.Join("、", missing)}。请填写 Windows 服务名（如 Tcpip、MySQL80），而非显示名称。");
        }

        /// <summary>
        /// 两个依赖列表是否相同（忽略顺序与大小写）
        /// </summary>
        public static bool SameSet(IEnumerable<string>? left, IEnumerable<string>? right)
        {
            var a = Normalize(left);
            var b = new HashSet<string>(Normalize(right), StringComparer.OrdinalIgnoreCase);
            return a.Count == b.Count && a.All(b.Contains);
        }

        private static bool IsGroup(string? name) => name != null && name.TrimStart().StartsWith('+');

        private static string? ValidateName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "服务名不能为空";
            var text = name.Trim();
            if (text.StartsWith('+')) return "不支持依赖组（以 '+' 开头）";
            if (text.Contains('\0')) return "不能包含 NUL 字符";
            if (text.Any(char.IsControl)) return "不能包含控制字符";
            if (text.Contains('/') || text.Contains('\\')) return "不能包含 '/' 或 '\\'";
            if (text.Length > MaxNameLength) return $"长度不能超过 {MaxNameLength} 个字符";
            return null;
        }
    }
}
