namespace Services.Cli
{
    internal static partial class CliApp
    {
        private static async Task<int> DeleteAsync(ParsedArgs args)
        {
            var ops = await OpenAsync();
            var service = await ops.FindAsync(args.Positionals[0]);

            await ops.Manager.DeleteServiceAsync(service.Id);

            var message = $"服务 '{service.Name}' 已删除（ID: {service.Id}）。";
            if (args.Json)
                WriteJson(new ActionResult { Action = "delete", Id = service.Id, Message = message }, CliJsonContext.Readable.ActionResult);
            else
                Console.WriteLine(message);
            return ExitCodes.Success;
        }
    }
}
