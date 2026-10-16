using System.Collections.Generic;
using System.Linq;

namespace Services.Core.Diagnostics
{
    public enum DiagnosticStatus { Ok, Warning, Error, Skipped }

    /// <summary>
    /// 一项检查的结果。ServiceId 为 null 表示全局检查；Fix 为建议的修复方法（可为 null）。
    /// </summary>
    public sealed record DiagnosticResult(string? ServiceId, string Check, DiagnosticStatus Status, string Message, string? Fix = null)
    {
        public static DiagnosticResult Ok(string? serviceId, string check, string message) =>
            new(serviceId, check, DiagnosticStatus.Ok, message);

        public static DiagnosticResult Warning(string? serviceId, string check, string message, string? fix = null) =>
            new(serviceId, check, DiagnosticStatus.Warning, message, fix);

        public static DiagnosticResult Error(string? serviceId, string check, string message, string? fix = null) =>
            new(serviceId, check, DiagnosticStatus.Error, message, fix);

        public static DiagnosticResult Skipped(string? serviceId, string check, string message) =>
            new(serviceId, check, DiagnosticStatus.Skipped, message);
    }

    public static class DiagnosticSummary
    {
        public static int Count(IEnumerable<DiagnosticResult> results, DiagnosticStatus status) =>
            results.Count(r => r.Status == status);

        public static bool HasErrors(IEnumerable<DiagnosticResult> results) =>
            results.Any(r => r.Status == DiagnosticStatus.Error);
    }
}
