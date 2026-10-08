namespace Athlon.Agent.Core;

/// <summary>Wakes the session when a background <c>execute_command</c> process exits.</summary>
public interface IBackgroundCommandCompletionNotifier
{
    void NotifyCompletionReady(string sessionId);
}

public sealed record BackgroundCommandCompletion(
    string CommandId,
    int Pid,
    int? ExitCode,
    string AnnounceText);

/// <summary>User message inserted when a background command finishes after the turn has ended.</summary>
public static class BackgroundCommandAutoContinuePrompt
{
    public const string Marker = "<athlon-command-auto-continue />";

    public static string BuildUserMessage() =>
        "A background command has finished.\n\n" +
        Marker +
        "\n\n" +
        "Review the background command completion in your context and tell the user the exit code " +
        "and anything important in the output. Do not start a duplicate command.";

    public static bool IsAutoContinueMessage(ChatMessage message) =>
        message.Role == MessageRole.User
        && message.Content.Contains(Marker, StringComparison.Ordinal);
}
