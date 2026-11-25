using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Terminal;
using Athlon.Agent.Infrastructure.Terminal;

namespace Athlon.Agent.Tests;

public sealed class TerminalToolsPromptContributorTests
{
    [Fact]
    public void Append_OpenLocalTerminal_KeepsCmdProcessRule()
    {
        var text = Render(WorkspaceKind.Local, @"C:\work\demo", tabOpen: true);

        Assert.Contains("separate cmd process", text, StringComparison.Ordinal);
        Assert.DoesNotContain("one-shot SSH exec", text, StringComparison.Ordinal);
        Assert.DoesNotContain("interactive shell on the SSH server", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_OpenSshTerminal_DescribesRemoteShell()
    {
        var text = Render(WorkspaceKind.Ssh, "/srv/app", tabOpen: true);

        Assert.Contains("interactive shell on the SSH server", text, StringComparison.Ordinal);
        Assert.Contains("/srv/app", text, StringComparison.Ordinal);
        Assert.Contains("one-shot SSH exec", text, StringComparison.Ordinal);
        Assert.DoesNotContain("separate cmd process", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_ClosedTerminal_DoesNotDescribeRemoteShell()
    {
        var text = Render(WorkspaceKind.Ssh, "/srv/app", tabOpen: false);

        Assert.Contains("Call terminal_open", text, StringComparison.Ordinal);
        Assert.DoesNotContain("interactive shell on the SSH server", text, StringComparison.Ordinal);
    }

    private static string Render(WorkspaceKind kind, string root, bool tabOpen)
    {
        var builder = new StringBuilder();
        new TerminalToolsPromptContributor(new StubTerminalState(tabOpen)).Append(builder, new EnvironmentPromptContext
        {
            Session = AgentSession.Create("terminal"),
            WorkspaceRoot = root,
            WorkspaceKind = kind,
            Tools = [],
            SkillsDirectory = @"C:\skills",
            Host = new PromptTestHelpers.FakeHostEnvironment(@"C:\skills", @"C:\app"),
            PromptSettings = new PromptSettings()
        });
        return builder.ToString();
    }

    private sealed class StubTerminalState(bool open) : ITerminalWorkspaceState
    {
        public bool HasOpenTerminalTab => open;
    }
}
