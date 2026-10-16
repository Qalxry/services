namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static Task<int> HelpAsync(ParsedArgs args)
        {
            if (args.Positionals.Count == 0)
            {
                Console.WriteLine(HelpText.Overview(Commands));
                return Task.FromResult(ExitCodes.Success);
            }

            var spec = FindCommand(args.Positionals[0])
                ?? throw new UsageException($"未知命令 '{args.Positionals[0]}'。", "help");
            Console.WriteLine(spec.Help);
            return Task.FromResult(ExitCodes.Success);
        }
    }
}
