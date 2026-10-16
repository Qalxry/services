namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> GetAsync(ParsedArgs args)
        {
            var ops = await OpenAsync();
            var service = await ops.FindAsync(args.Positionals[0]);

            bool showSecrets = args.HasFlag("show-secrets");
            if (args.Json)
                WriteJson(ToInfo(service, ops, showSecrets), CliJsonContext.Readable.ServiceInfo);
            else
                WriteServiceText(service, ops, showSecrets);
            return ExitCodes.Success;
        }
    }
}
