using System.Reflection;
using System.Text;
using Services.Cli;
using Xunit;

namespace Services.Tests
{
    /// <summary>
    /// docs/cli.md 由 servicesctl 的内置帮助生成，本测试保证二者一致。
    /// 修改帮助文本后执行以下命令重新生成：
    ///   UPDATE_DOCS=1 dotnet test Services.Tests --filter CliReference
    /// </summary>
    public class CliReferenceTests
    {
        private const string DocPath = "docs/cli.md";

        /// <summary>
        /// 命令帮助：HelpText 中除 Tool 以外的公开常量，按声明顺序（与命令表顺序一致）
        /// </summary>
        private static List<(string Name, string Summary, string Help)> Commands()
        {
            return typeof(HelpText).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(HelpText.Tool))
                .Select(f =>
                {
                    var help = ((string)f.GetRawConstantValue()!).Replace("\r\n", "\n");
                    // 首行格式：servicesctl <命令> - <摘要>
                    var firstLine = help.Split('\n')[0];
                    var dash = firstLine.IndexOf(" - ", StringComparison.Ordinal);
                    var name = firstLine.Substring(HelpText.Tool.Length + 1, dash - HelpText.Tool.Length - 1).Trim();
                    return (name, HelpText.Summary(help), help.TrimEnd());
                })
                .ToList();
        }

        public static string Generate()
        {
            var commands = Commands();
            var specs = commands.Select(c => new CommandSpec
            {
                Name = c.Name,
                Summary = c.Summary,
                Help = c.Help,
                Handler = _ => Task.FromResult(0)
            });

            var sb = new StringBuilder();
            sb.Append("# servicesctl 命令参考\n\n");
            sb.Append("> 本文件由 `Services.Tests/CliReferenceTests.cs` 根据 servicesctl 的内置帮助自动生成，请勿手工编辑。\n");
            sb.Append("> 修改帮助文本（`Services.Cli/HelpText.cs`）后，在仓库根目录执行\n");
            sb.Append("> `UPDATE_DOCS=1 dotnet test Services.Tests --filter CliReference` 重新生成；CI 会校验二者一致。\n\n");
            sb.Append("## 目录\n\n");
            sb.Append("- [总览](#总览)\n");
            foreach (var (name, summary, _) in commands)
                sb.Append($"- [{name}](#{name}) — {summary}\n");
            sb.Append("\n## 总览\n\n```text\n");
            sb.Append(HelpText.Overview(specs).Replace("\r\n", "\n").TrimEnd());
            sb.Append("\n```\n");
            foreach (var (name, _, help) in commands)
            {
                sb.Append($"\n## {name}\n\n```text\n{help}\n```\n");
            }
            return sb.ToString();
        }

        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Services.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录（Services.sln）");
        }

        [Fact]
        public void CliReference_IsUpToDate()
        {
            var path = Path.Combine(RepositoryRoot(), DocPath);
            var expected = Generate();

            if (Environment.GetEnvironmentVariable("UPDATE_DOCS") == "1")
            {
                File.WriteAllText(path, expected, new UTF8Encoding(false));
                return;
            }

            Assert.True(File.Exists(path), $"{DocPath} 不存在，请执行 UPDATE_DOCS=1 dotnet test Services.Tests --filter CliReference 生成");
            var actual = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.True(expected == actual,
                $"{DocPath} 与 servicesctl 的内置帮助不一致，请执行 UPDATE_DOCS=1 dotnet test Services.Tests --filter CliReference 重新生成");
        }

        [Fact]
        public void EveryCommandHelpStartsWithToolAndSummary()
        {
            var commands = Commands();
            Assert.Equal(15, commands.Count);
            Assert.All(commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Summary)));
            Assert.Equal(commands.Count, commands.Select(c => c.Name).Distinct().Count());
        }
    }
}
