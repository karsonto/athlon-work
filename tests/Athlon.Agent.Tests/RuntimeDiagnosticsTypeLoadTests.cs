using System.Reflection;
using System.Runtime.Loader;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class RuntimeDiagnosticsTypeLoadTests
{
    [Fact]
    public void RuntimeDiagnosticEvent_type_can_be_loaded()
    {
        var forcedPath = typeof(AgentSession).Assembly.Location;
        Assert.False(string.IsNullOrWhiteSpace(forcedPath));
        Assert.True(File.Exists(forcedPath), $"Core assembly not found at {forcedPath}");

        var alc = new AssemblyLoadContext("diagnostics-rt", isCollectible: true);
        var asm = alc.LoadFromAssemblyPath(forcedPath);

        var typeName = "Athlon.Agent.Core.RuntimeDiagnostics.RuntimeDiagnosticEvent";
        try
        {
            var t = asm.GetType(typeName, throwOnError: true);
            Assert.NotNull(t);
        }
        catch (TypeLoadException ex)
        {
            throw new Exception(
                $"TypeLoadException while loading {typeName}.\nCore asm: {asm.FullName}\nLocation: {asm.Location}\n{ex}\nInner: {ex.InnerException}",
                ex);
        }
    }
}