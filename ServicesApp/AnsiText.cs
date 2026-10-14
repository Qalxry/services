using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Services.Core.Helpers;
using System;
using System.Collections.Generic;
using Windows.UI;

namespace ServicesApp
{
    /// <summary>
    /// 日志查看器中的一行
    /// </summary>
    public sealed class LogEntry
    {
        public LogLineKind Kind { get; }
        public string Text { get; }

        public LogEntry(LogLineKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public static LogEntry Parse(string line)
        {
            var (kind, text) = LogLine.Parse(line);
            return new LogEntry(kind, text);
        }
    }

    /// <summary>
    /// 附加属性：将 LogEntry 渲染到 Border 内的 TextBlock 中，ANSI 颜色/样式由 AnsiParser 解析。
    /// stderr 行在左侧显示提示色条，包装器消息以斜体弱化显示。
    /// </summary>
    public static class AnsiText
    {
        public static readonly DependencyProperty EntryProperty = DependencyProperty.RegisterAttached(
            "Entry", typeof(object), typeof(AnsiText), new PropertyMetadata(null, OnEntryChanged));

        public static object GetEntry(DependencyObject obj) => obj.GetValue(EntryProperty);
        public static void SetEntry(DependencyObject obj, object value) => obj.SetValue(EntryProperty, value);

        private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
        private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

        private static void OnEntryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Border border || border.Child is not TextBlock textBlock) return;

            textBlock.Inlines.Clear();
            if (e.NewValue is not LogEntry entry) return;

            border.BorderBrush = entry.Kind == LogLineKind.Stderr
                ? GetResourceBrush("SystemFillColorCautionBrush") ?? TransparentBrush
                : TransparentBrush;
            textBlock.FontStyle = entry.Kind == LogLineKind.System ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
            textBlock.Opacity = entry.Kind == LogLineKind.System ? 0.7 : 1.0;

            var palette = IsDark(border) ? AnsiParser.DarkPalette : AnsiParser.LightPalette;
            foreach (var segment in AnsiParser.Parse(entry.Text, palette))
            {
                var run = new Run { Text = segment.Text };
                if (segment.Foreground is Color color) run.Foreground = GetBrush(color);
                if (segment.Bold) run.FontWeight = FontWeights.Bold;
                if (segment.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (segment.Underline) run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                textBlock.Inlines.Add(run);
            }
        }

        private static bool IsDark(FrameworkElement element)
        {
            if (element.XamlRoot != null && element.ActualTheme != ElementTheme.Default)
                return element.ActualTheme == ElementTheme.Dark;
            return Application.Current.RequestedTheme == ApplicationTheme.Dark;
        }

        private static SolidColorBrush GetBrush(Color color)
        {
            if (!BrushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                BrushCache[color] = brush;
            }
            return brush;
        }

        private static Brush? GetResourceBrush(string key)
        {
            return Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
        }
    }
}
