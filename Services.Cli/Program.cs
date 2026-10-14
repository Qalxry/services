using System.Text;

namespace Services.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (IOException)
            {
                // 无控制台（如输出被重定向）时忽略
            }

            return CliApp.RunAsync(args).GetAwaiter().GetResult();
        }
    }
}
