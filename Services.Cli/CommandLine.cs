namespace Services.Cli
{
    /// <summary>
    /// 退出码（同时写入帮助文本，供脚本 / AI 判断结果）
    /// </summary>
    internal static class ExitCodes
    {
        public const int Success = 0;
        public const int Failure = 1;
        public const int Usage = 2;
        public const int NotFound = 3;
        public const int AdminRequired = 4;
    }

    internal class CliException : Exception
    {
        public int ExitCode { get; }

        public CliException(int exitCode, string message) : base(message)
        {
            ExitCode = exitCode;
        }
    }

    /// <summary>
    /// 参数错误：输出错误信息并提示查看对应命令的帮助
    /// </summary>
    internal sealed class UsageException : CliException
    {
        public string? Command { get; }

        public UsageException(string message, string? command = null) : base(ExitCodes.Usage, message)
        {
            Command = command;
        }
    }

    internal sealed class CommandSpec
    {
        public required string Name { get; init; }
        public required string Summary { get; init; }
        public required string Help { get; init; }
        public string[] ValueOptions { get; init; } = Array.Empty<string>();
        public string[] FlagOptions { get; init; } = Array.Empty<string>();
        /// <summary>
        /// 短选项别名：单个字符（不含 "-"）到长选项名（不含 "--"）的映射，如 { "f", "follow" } 使 -f 等同于 --follow。
        /// 别名指向的长选项必须已在 ValueOptions 或 FlagOptions 中声明。
        /// </summary>
        public IReadOnlyDictionary<string, string> ShortAliases { get; init; } = new Dictionary<string, string>();
        public int MinPositionals { get; init; }
        public int MaxPositionals { get; init; }
        public bool RequiresAdmin { get; init; }
        public required Func<ParsedArgs, Task<int>> Handler { get; init; }
    }

    internal sealed class ParsedArgs
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        public string Command { get; }
        public List<string> Positionals { get; } = new();

        public bool Json => HasFlag("json");
        public bool Help => HasFlag("help");

        private ParsedArgs(string command)
        {
            Command = command;
        }

        public bool HasFlag(string name) => _flags.Contains(name);

        public bool HasValue(string name) => _values.ContainsKey(name);

        public string? GetValue(string name) => _values.TryGetValue(name, out var value) ? value : null;

        public int? GetInt(string name, int min, int max)
        {
            var raw = GetValue(name);
            if (raw == null) return null;

            if (!int.TryParse(raw, out var value) || value < min || value > max)
                throw new UsageException($"--{name} 的值必须是 {min} 到 {max} 之间的整数，实际为 '{raw}'。", Command);
            return value;
        }

        /// <summary>
        /// 一对互斥开关（如 --auto-restart / --no-auto-restart），均未指定时返回 null
        /// </summary>
        public bool? GetSwitch(string onName, string offName)
        {
            bool on = HasFlag(onName), off = HasFlag(offName);
            if (on && off) throw new UsageException($"--{onName} 与 --{offName} 不能同时使用。", Command);
            return on ? true : off ? false : null;
        }

        /// <summary>
        /// 解析命令参数。支持 --name value、--name=value、-h 以及命令声明的短选项别名（如 -f）；
        /// "--" 之后的参数一律视为位置参数。
        /// 未知选项直接报错，避免拼写错误被静默忽略。
        /// </summary>
        public static ParsedArgs Parse(CommandSpec spec, IReadOnlyList<string> tokens)
        {
            var result = new ParsedArgs(spec.Name);
            bool positionalOnly = false;

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                // 需在位置参数判断之前处理，否则不以 "-" 开头的 "/?" 会被当作位置参数
                if (!positionalOnly && (token == "-h" || token == "/?"))
                {
                    result._flags.Add("help");
                    continue;
                }

                if (positionalOnly || !token.StartsWith("-", StringComparison.Ordinal) || token == "-")
                {
                    result.Positionals.Add(token);
                    continue;
                }

                if (token == "--")
                {
                    positionalOnly = true;
                    continue;
                }

                string name;
                string? inlineValue = null;
                if (!token.StartsWith("--", StringComparison.Ordinal))
                {
                    // 短选项仅限命令声明的单字符别名，其余仍需使用 --name 形式
                    if (token.Length != 2 || !spec.ShortAliases.TryGetValue(token.Substring(1), out var longName))
                        throw new UsageException($"未知选项 '{token}'。选项需使用 --name 形式。", spec.Name);
                    name = longName;
                }
                else
                {
                    name = token.Substring(2);
                    int eq = name.IndexOf('=');
                    if (eq >= 0)
                    {
                        inlineValue = name.Substring(eq + 1);
                        name = name.Substring(0, eq);
                    }
                }

                if (IsGlobalFlag(name) || spec.FlagOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (inlineValue != null)
                        throw new UsageException($"选项 --{name} 是开关，不接受值。", spec.Name);
                    result._flags.Add(name);
                }
                else if (spec.ValueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (inlineValue == null)
                    {
                        if (i + 1 >= tokens.Count)
                            throw new UsageException($"选项 --{name} 缺少值。", spec.Name);
                        inlineValue = tokens[++i];
                    }
                    result._values[name] = inlineValue;
                }
                else
                {
                    throw new UsageException($"命令 '{spec.Name}' 不支持选项 --{name}。", spec.Name);
                }
            }

            if (result.Help) return result;

            if (result.Positionals.Count < spec.MinPositionals)
                throw new UsageException("缺少必需的参数。", spec.Name);
            if (result.Positionals.Count > spec.MaxPositionals)
                throw new UsageException($"多余的参数 '{result.Positionals[spec.MaxPositionals]}'。", spec.Name);

            return result;
        }

        private static bool IsGlobalFlag(string name) =>
            string.Equals(name, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "help", StringComparison.OrdinalIgnoreCase);
    }
}
