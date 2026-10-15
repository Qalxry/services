using System.Diagnostics;

namespace Services.TestTarget
{
    /// <summary>
    /// 集成测试目标程序。所有选项可组合，按以下顺序执行：
    ///   --record &lt;file&gt;          追加一行 "start &lt;pid&gt;"（统计启动次数）
    ///   --stdout &lt;text&gt;          向 stdout 输出一行（可重复）
    ///   --stderr &lt;text&gt;          向 stderr 输出一行（可重复）
    ///   --print-env &lt;name&gt;       输出 "env &lt;name&gt;=&lt;value&gt;"（可重复）
    ///   --print-user             输出 "user &lt;域\用户&gt;"
    ///   --print-cwd              输出 "cwd &lt;当前目录&gt;"
    ///   --spawn-child &lt;file&gt;     派生一个一直运行的子进程，将其 PID 写入文件
    ///   --ctrlc-file &lt;file&gt;      收到 Ctrl+C 时写入 "ctrlc" 并以 0 退出
    ///   --ignore-ctrlc           忽略 Ctrl+C
    ///   --quit-file &lt;file&gt;       该文件出现时以 0 退出（模拟 nginx -s quit 之类的停止命令）
    ///   --exit &lt;code&gt;            输出完毕后（等待 --delay 毫秒）以指定退出码退出；不指定则一直运行
    ///   --delay &lt;ms&gt;
    ///   --tick &lt;ms&gt;              运行期间每隔指定毫秒向 stdout 输出 "tick &lt;n&gt;"
    /// 准备完毕后输出 "ready"。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var options = Options.Parse(args);
            if (options.Sleep)
            {
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }

            if (options.Record != null)
                File.AppendAllText(options.Record, $"start {Environment.ProcessId}{Environment.NewLine}");

            foreach (var line in options.Stdout) Console.Out.WriteLine(line);
            foreach (var line in options.Stderr) Console.Error.WriteLine(line);
            foreach (var name in options.PrintEnv)
                Console.Out.WriteLine($"env {name}={Environment.GetEnvironmentVariable(name)}");
            if (options.PrintUser)
                Console.Out.WriteLine($"user {Environment.UserDomainName}\\{Environment.UserName}");
            if (options.PrintCwd)
                Console.Out.WriteLine($"cwd {Environment.CurrentDirectory}");

            if (options.SpawnChild != null)
            {
                var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--sleep") { UseShellExecute = false })!;
                File.WriteAllText(options.SpawnChild, child.Id.ToString());
            }

            var exit = new ManualResetEventSlim();
            int exitCode = 0;

            if (options.CtrlCFile != null || options.IgnoreCtrlC)
            {
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    if (options.IgnoreCtrlC)
                    {
                        Console.Out.WriteLine("ctrl-c ignored");
                        return;
                    }
                    File.WriteAllText(options.CtrlCFile!, "ctrlc");
                    Console.Out.WriteLine("ctrl-c received");
                    exitCode = 0;
                    exit.Set();
                };
            }

            if (options.QuitFile != null)
            {
                var quitFile = options.QuitFile;
                new Thread(() =>
                {
                    while (!File.Exists(quitFile)) Thread.Sleep(100);
                    Console.Out.WriteLine("quit file found");
                    exitCode = 0;
                    exit.Set();
                }) { IsBackground = true }.Start();
            }

            if (options.Tick > 0)
            {
                new Thread(() =>
                {
                    for (int n = 1; ; n++)
                    {
                        Thread.Sleep(options.Tick);
                        Console.Out.WriteLine($"tick {n}");
                    }
                }) { IsBackground = true }.Start();
            }

            Console.Out.WriteLine("ready");
            Console.Out.Flush();

            if (options.Exit is { } code)
            {
                if (exit.Wait(options.Delay)) return exitCode;
                return code;
            }

            exit.Wait();
            return exitCode;
        }

        private sealed class Options
        {
            public string? Record;
            public List<string> Stdout = new();
            public List<string> Stderr = new();
            public List<string> PrintEnv = new();
            public bool PrintUser;
            public bool PrintCwd;
            public string? SpawnChild;
            public string? CtrlCFile;
            public bool IgnoreCtrlC;
            public string? QuitFile;
            public int? Exit;
            public int Delay;
            public int Tick;
            public bool Sleep;

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} requires a value");
                    switch (args[i])
                    {
                        case "--record": o.Record = Next(); break;
                        case "--stdout": o.Stdout.Add(Next()); break;
                        case "--stderr": o.Stderr.Add(Next()); break;
                        case "--print-env": o.PrintEnv.Add(Next()); break;
                        case "--print-user": o.PrintUser = true; break;
                        case "--print-cwd": o.PrintCwd = true; break;
                        case "--spawn-child": o.SpawnChild = Next(); break;
                        case "--ctrlc-file": o.CtrlCFile = Next(); break;
                        case "--ignore-ctrlc": o.IgnoreCtrlC = true; break;
                        case "--quit-file": o.QuitFile = Next(); break;
                        case "--exit": o.Exit = int.Parse(Next()); break;
                        case "--delay": o.Delay = int.Parse(Next()); break;
                        case "--tick": o.Tick = int.Parse(Next()); break;
                        case "--sleep": o.Sleep = true; break;
                        default: throw new ArgumentException($"unknown option {args[i]}");
                    }
                }
                return o;
            }
        }
    }
}
