using System.Reflection;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Tests;

public sealed class RuntimeDiagnosticCoverageMatrixTests
{
    [Fact]
    public void P0P1_matrix_entries_have_unique_failure_point_and_error_code()
    {
        var entries = RuntimeDiagnosticFailureMatrix.P0P1Entries;
        Assert.NotEmpty(entries);

        var duplicateFailurePoint = entries
            .GroupBy(x => x.FailurePointId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        Assert.True(duplicateFailurePoint is null, $"Duplicate failurePointId: {duplicateFailurePoint?.Key}");

        var duplicateErrorCode = entries
            .GroupBy(x => x.ErrorCode, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        Assert.True(duplicateErrorCode is null, $"Duplicate errorCode: {duplicateErrorCode?.Key}");
    }

    [Fact]
    public void P0P1_matrix_error_codes_exist_in_runtime_error_dictionary()
    {
        var knownErrorCodes = typeof(RuntimeDiagnosticErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in RuntimeDiagnosticFailureMatrix.P0P1Entries)
        {
            Assert.Contains(entry.ErrorCode, knownErrorCodes);
            Assert.False(string.IsNullOrWhiteSpace(entry.Component));
            Assert.False(string.IsNullOrWhiteSpace(entry.Phase));
            Assert.False(string.IsNullOrWhiteSpace(entry.Severity));
        }
    }
}

