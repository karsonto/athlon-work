using System.Text;
using Athlon.Agent.Core.Cli;

namespace Athlon.Agent.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var options = CliArgsParser.Parse(args);
        var endpoint = CliEndpointFile.TryGetLive(CliPaths.DefaultRootPath);
        if (endpoint is null)
        {
            Console.Error.WriteLine("Athlon Agent is not running. 请先打开 Athlon Agent。");
            return 1;
        }

        using var client = new CliAgentClient(endpoint, options.Yes);
        if (!await client.HealthAsync().ConfigureAwait(false))
        {
            Console.Error.WriteLine("Athlon Agent is not running. 请先打开 Athlon Agent。");
            return 1;
        }

        var cwd = Directory.GetCurrentDirectory();
        var repl = new CliRepl(client, cwd, options.SessionId);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            repl.CancelCurrentTurn();
        };

        if (!string.IsNullOrWhiteSpace(options.Prompt))
        {
            await repl.SendAsync(options.Prompt).ConfigureAwait(false);
            if (options.Once)
            {
                return 0;
            }
        }

        if (Console.IsInputRedirected && options.Once)
        {
            return 0;
        }

        return await repl.RunInteractiveAsync().ConfigureAwait(false);
    }
}
