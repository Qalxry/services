using System.Collections.Generic;
using System.Text;
using Windows.UI;

namespace ServicesApp
{
    /// <summary>
    /// ANSI 转义序列解析器：仅处理 SGR（ESC[...m）的前景色、粗体、斜体、下划线，
    /// 背景色等其余样式忽略；光标移动、OSC 等非 SGR 序列直接移除。
    /// </summary>
    public static class AnsiParser
    {
        // VS Code 终端配色
        public static readonly Color[] DarkPalette =
        {
            Hex(0x808080), Hex(0xCD3131), Hex(0x0DBC79), Hex(0xE5E510), Hex(0x2472C8), Hex(0xBC3FBC), Hex(0x11A8CD), Hex(0xE5E5E5),
            Hex(0x8C8C8C), Hex(0xF14C4C), Hex(0x23D18B), Hex(0xF5F543), Hex(0x3B8EEA), Hex(0xD670D6), Hex(0x29B8DB), Hex(0xFFFFFF),
        };

        public static readonly Color[] LightPalette =
        {
            Hex(0x000000), Hex(0xCD3131), Hex(0x00BC00), Hex(0x949800), Hex(0x0451A5), Hex(0xBC05BC), Hex(0x0598BC), Hex(0x555555),
            Hex(0x666666), Hex(0xCD3131), Hex(0x14CE14), Hex(0xB5BA00), Hex(0x0451A5), Hex(0xBC05BC), Hex(0x0598BC), Hex(0x808080),
        };

        private static Color Hex(int rgb) =>
            Color.FromArgb(0xFF, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        public readonly record struct Segment(string Text, Color? Foreground, bool Bold, bool Italic, bool Underline);

        private struct Style
        {
            public Color? Foreground;
            public bool Bold;
            public bool Italic;
            public bool Underline;
        }

        /// <summary>
        /// 将一行文本按样式切分为若干片段
        /// </summary>
        public static List<Segment> Parse(string text, Color[] palette)
        {
            var segments = new List<Segment>();
            if (text.IndexOf('\u001b') < 0)
            {
                if (text.Length > 0) segments.Add(new Segment(text, null, false, false, false));
                return segments;
            }

            var style = new Style();
            var sb = new StringBuilder();

            void Flush()
            {
                if (sb.Length == 0) return;
                segments.Add(new Segment(sb.ToString(), style.Foreground, style.Bold, style.Italic, style.Underline));
                sb.Clear();
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c != '\u001b')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                if (i + 1 >= text.Length) break;
                char next = text[i + 1];

                if (next == '[')
                {
                    // CSI: 参数字节直到 0x40-0x7E 的终止字节
                    int j = i + 2;
                    while (j < text.Length && (text[j] < 0x40 || text[j] > 0x7E)) j++;
                    if (j >= text.Length) break;

                    if (text[j] == 'm')
                    {
                        Flush();
                        ApplySgr(text.Substring(i + 2, j - i - 2), ref style, palette);
                    }
                    i = j + 1;
                }
                else if (next == ']')
                {
                    // OSC: 以 BEL 或 ESC\ 结束
                    int j = i + 2;
                    while (j < text.Length && text[j] != '\u0007' && !(text[j] == '\u001b' && j + 1 < text.Length && text[j + 1] == '\\')) j++;
                    i = j >= text.Length ? j : (text[j] == '\u0007' ? j + 1 : j + 2);
                }
                else
                {
                    i += 2;
                }
            }

            Flush();
            return segments;
        }

        private static void ApplySgr(string parameters, ref Style style, Color[] palette)
        {
            var parts = parameters.Length == 0 ? new[] { "0" } : parameters.Split(';', ':');
            var codes = new int[parts.Length];
            for (int k = 0; k < parts.Length; k++)
            {
                codes[k] = int.TryParse(parts[k], out var v) ? v : 0;
            }

            for (int k = 0; k < codes.Length; k++)
            {
                int code = codes[k];
                switch (code)
                {
                    case 0: style = new Style(); break;
                    case 1: style.Bold = true; break;
                    case 3: style.Italic = true; break;
                    case 4: style.Underline = true; break;
                    case 21:
                    case 22: style.Bold = false; break;
                    case 23: style.Italic = false; break;
                    case 24: style.Underline = false; break;
                    case 39: style.Foreground = null; break;
                    case >= 30 and <= 37: style.Foreground = palette[code - 30]; break;
                    case >= 90 and <= 97: style.Foreground = palette[code - 90 + 8]; break;
                    case 38:
                    case 48:
                        var color = ReadExtendedColor(codes, ref k, palette);
                        if (code == 38 && color.HasValue) style.Foreground = color;
                        break;
                }
            }
        }

        // 38;5;n（256 色）或 38;2;r;g;b（真彩色）
        private static Color? ReadExtendedColor(int[] codes, ref int k, Color[] palette)
        {
            if (k + 1 >= codes.Length) return null;

            int mode = codes[k + 1];
            if (mode == 5 && k + 2 < codes.Length)
            {
                int n = codes[k + 2];
                k += 2;
                return Color256(n, palette);
            }
            if (mode == 2 && k + 4 < codes.Length)
            {
                var color = Color.FromArgb(0xFF, (byte)codes[k + 2], (byte)codes[k + 3], (byte)codes[k + 4]);
                k += 4;
                return color;
            }

            k += 1;
            return null;
        }

        private static Color? Color256(int n, Color[] palette)
        {
            if (n < 0 || n > 255) return null;
            if (n < 16) return palette[n];
            if (n >= 232)
            {
                byte gray = (byte)(8 + (n - 232) * 10);
                return Color.FromArgb(0xFF, gray, gray, gray);
            }

            n -= 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return Color.FromArgb(0xFF, Level(n / 36), Level(n / 6 % 6), Level(n % 6));
        }
    }
}
