using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Reads the account-level habit profile. Read-only by design: the profile is maintained by the
/// system after each turn, so the model never writes it through a tool.
/// </summary>
public sealed class ProfileGetTool(
    IProfileMemory profileMemory,
    AppSettings settings,
    IAppLogger logger) : IAgentTool, ILongTermMemoryTool
{
    private readonly IAppLogger _logger = logger.ForContext("ProfileGetTool");

    public ToolDefinition Definition => new(
        Name: "profile_get",
        Description:
            "Read the account-level profile shared by every workspace and session: user habits " +
            "(how to address them, language, tone, response format) and lessons learned from past " +
            "corrections. Call with no arguments to list what is stored; pass file=\"user\" or " +
            "file=\"lessons\" to read one document in full. The profile is maintained automatically " +
            "after each turn and may come from a remote store, so it can lag slightly. This is not " +
            "session memory: use memory_search for facts specific to the current project session.",
        ToolSchema.Object()
            .String(
                "file",
                "Which profile document to read. Omit to list both documents.",
                required: false,
                enumValues: ["user", "lessons"])
            .Build());

    public async Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Profile.Enabled)
        {
            return ToolResult.Success("Profile disabled", "The account profile is disabled in settings.");
        }

        try
        {
            if (invocation.Arguments.TryGetString("file", out var file) && !string.IsNullOrWhiteSpace(file))
            {
                if (!TryParseKind(file, out var kind))
                {
                    return ToolResult.Failure(
                        "Unknown profile file",
                        $"file must be one of: {string.Join(", ", ProfileKinds.All.Select(ToArgument))}");
                }

                return await ReadOneAsync(kind, cancellationToken).ConfigureAwait(false);
            }

            return await ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning("Profile read failed: {Error}", ex.Message);
            return ToolResult.Failure("Profile read failed", ex.Message);
        }
    }

    private async Task<ToolResult> ListAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Account profile documents (shared across all workspaces and sessions):");
        foreach (var kind in ProfileKinds.All)
        {
            var document = await profileMemory.ReadAsync(kind, cancellationToken).ConfigureAwait(false);
            var purpose = kind == ProfileKind.User
                ? "stable habits: address form, language, tone, response format"
                : "reusable lessons distilled from past corrections";
            var state = document is null || document.IsEmpty
                ? "empty"
                : $"{document.Content.Length} chars, updated {document.UpdatedAt:O}";
            sb.AppendLine($"- {ToArgument(kind)} ({ProfileKinds.FileName(kind)}): {purpose} — {state}");
        }

        sb.AppendLine("Call profile_get with file=\"user\" or file=\"lessons\" to read one document.");
        return ToolResult.Success("Profile index", sb.ToString().TrimEnd());
    }

    private async Task<ToolResult> ReadOneAsync(ProfileKind kind, CancellationToken cancellationToken)
    {
        var document = await profileMemory.ReadAsync(kind, cancellationToken).ConfigureAwait(false);
        var name = ProfileKinds.FileName(kind);
        if (document is null || document.IsEmpty)
        {
            return ToolResult.Success($"Empty {name}", "This profile document has not been written yet.");
        }

        return ToolResult.Success($"Read {name} ({document.Content.Length} chars)", document.Content);
    }

    private static bool TryParseKind(string value, out ProfileKind kind)
    {
        foreach (var candidate in ProfileKinds.All)
        {
            if (string.Equals(value.Trim(), ToArgument(candidate), StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.Trim(), ProfileKinds.FileName(candidate), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private static string ToArgument(ProfileKind kind) =>
        kind == ProfileKind.User ? "user" : "lessons";
}
