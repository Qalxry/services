using Services.Core.Diagnostics;
using Services.Core.Services;

namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> DoctorAsync(ParsedArgs args)
        {
            var ops = await OpenAsync();
            var services = args.Positionals.Count == 0 ? await ops.ListAsync() : await ops.FindManyAsync(args.Positionals);
            bool admin = IsAdministrator();

            var doctor = new ServiceDoctor(ops.Logs, WindowsServiceManager.FindWrapperExecutable());
            var results = doctor.CheckGlobal(admin);
            foreach (var service in services)
                results.AddRange(doctor.CheckService(service, ops.ReadRuntime(service.Id), admin));

            var names = services.ToDictionary(s => s.Id, s => s.Name);
            if (args.Json)
            {
                WriteJson(new DoctorResult
                {
                    Ok = !DiagnosticSummary.HasErrors(results),
                    Summary = new DoctorSummary
                    {
                        Ok = DiagnosticSummary.Count(results, DiagnosticStatus.Ok),
                        Warning = DiagnosticSummary.Count(results, DiagnosticStatus.Warning),
                        Error = DiagnosticSummary.Count(results, DiagnosticStatus.Error),
                        Skipped = DiagnosticSummary.Count(results, DiagnosticStatus.Skipped)
                    },
                    Checks = results.Select(r => new DoctorCheck
                    {
                        ServiceId = r.ServiceId,
                        ServiceName = r.ServiceId != null && names.TryGetValue(r.ServiceId, out var name) ? name : null,
                        Check = r.Check,
                        Status = r.Status.ToString().ToLowerInvariant(),
                        Message = r.Message,
                        Fix = r.Fix
                    }).ToList()
                }, CliJsonContext.Readable.DoctorResult);
            }
            else
            {
                WriteDoctorText(results, names, args.HasFlag("all"));
            }
            return DiagnosticSummary.HasErrors(results) ? ExitCodes.Failure : ExitCodes.Success;
        }

        /// <summary>
        /// 按全局 / 服务分组输出；默认只列出未通过的项（--all 列出全部）
        /// </summary>
        private static void WriteDoctorText(List<DiagnosticResult> results, Dictionary<string, string> names, bool all)
        {
            foreach (var group in results.GroupBy(r => r.ServiceId))
            {
                var shown = group.Where(r => all || r.Status is DiagnosticStatus.Warning or DiagnosticStatus.Error).ToList();
                if (shown.Count == 0) continue;

                Console.WriteLine(group.Key == null ? "全局" : $"服务 {names.GetValueOrDefault(group.Key, group.Key)} ({group.Key})");
                foreach (var result in shown)
                {
                    var marker = result.Status switch
                    {
                        DiagnosticStatus.Ok => "[OK]   ",
                        DiagnosticStatus.Warning => "[WARN] ",
                        DiagnosticStatus.Error => "[ERROR]",
                        _ => "[SKIP] "
                    };
                    Console.WriteLine($"  {marker} {result.Check}: {result.Message}");
                    if (result.Fix != null) Console.WriteLine($"          修复: {result.Fix}");
                }
                Console.WriteLine();
            }

            int errors = DiagnosticSummary.Count(results, DiagnosticStatus.Error);
            int warnings = DiagnosticSummary.Count(results, DiagnosticStatus.Warning);
            Console.WriteLine(errors + warnings == 0
                ? $"全部 {results.Count} 项检查通过（跳过 {DiagnosticSummary.Count(results, DiagnosticStatus.Skipped)} 项）。"
                : $"错误 {errors}、警告 {warnings}、通过 {DiagnosticSummary.Count(results, DiagnosticStatus.Ok)}、" +
                  $"跳过 {DiagnosticSummary.Count(results, DiagnosticStatus.Skipped)}。" + (all ? "" : "使用 --all 查看全部检查项。"));
        }
    }
}
