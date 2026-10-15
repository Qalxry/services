namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static Task<int> VersionAsync(ParsedArgs args)
        {
            if (args.Json)
                WriteJson(new VersionInfo { Version = GetVersion() }, CliJsonContext.Readable.VersionInfo);
            else
                Console.WriteLine(GetVersion());
            return Task.FromResult(ExitCodes.Success);
        }
    }
}
