using System.Reflection;
using System.IO;
using Athlon.Agent.Core;
using System.Runtime.Loader;

namespace Athlon.Agent.Tests;

public sealed class RuntimeDiagnosticsTypeLoadTests
{
    [Fact]
    public void RuntimeDiagnosticEvent_type_can_be_loaded()
    {
        // 优先用手动构建产物验证，避免 dotnet test shadow copy 影响结论。
        var forcedPath = @"F:/athlon-work/artifacts/test-out/core-build-check/Athlon.Agent.Core.dll";

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

