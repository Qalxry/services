namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> GetAsync(ParsedArgs args)
        {
            var ops = await OpenAsync();
            var service = await ops.FindAsync(args.Positionals[0]);

            if (args.Json)
                WriteJson(ToInfo(service, ops), CliJsonContext.Readable.ServiceInfo);
            else
                WriteServiceText(service, ops);
            return ExitCodes.Success;
        }
    }
}
