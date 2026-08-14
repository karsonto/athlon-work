using Athlon.Agent.Core.Cli;

namespace Athlon.Agent.Cli;

internal sealed class CliRepl(CliAgentClient client, string cwd, string? sessionId)
{
    private string? _sessionId = sessionId;
    private CancellationTokenSource? _turnCts;
    private bool _newSession;

    public string? SessionId => _sessionId;

    public void CancelCurrentTurn()
    {
        var sessionIdToCancel = _sessionId;
        _turnCts?.Cancel();
        if (!string.IsNullOrWhiteSpace(sessionIdToCancel))
        {
            _ = client.CancelAsync(sessionIdToCancel);
        }
    }

    public async Task<int> RunInteractiveAsync()
    {
        PrintBanner();
        while (true)
        {
            Console.Write("> ");
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch (IOException)
            {
                return 0;
            }

            if (line is null)
            {
                Console.WriteLine();
                return 0;
            }

            switch (CliReplCommand.Parse(line))
            {
                case CliReplCommandKind.Empty:
                    continue;
                case CliReplCommandKind.Exit:
                    return 0;
                case CliReplCommandKind.New:
                    _newSession = true;
                    _sessionId = null;
                    Console.WriteLine("已开始新对话。");
                    continue;
                default:
                    await SendAsync(line).ConfigureAwait(false);
                    break;
            }
        }
    }

    public async Task SendAsync(string input)
    {
        using var cts = new CancellationTokenSource();
        _turnCts = cts;
        try
        {
            var newSession = _newSession;
            _newSession = false;
            _sessionId = await client.RunTurnAsync(cwd, input, _sessionId, newSession, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("(已停止)");
        }
        finally
        {
            if (ReferenceEquals(_turnCts, cts))
            {
                _turnCts = null;
            }
        }
    }

    private void PrintBanner()
    {
        var shortId = string.IsNullOrWhiteSpace(_sessionId) ? "new" : _sessionId[..Math.Min(8, _sessionId.Length)];
        Console.WriteLine($"Athlon CLI  ·  session {shortId}  ·  {cwd}");
        Console.WriteLine("(/exit 退出  ·  /new 新对话  ·  Ctrl+C 停止本轮  ·  Ctrl+D 退出)");
        Console.WriteLine();
    }
}
