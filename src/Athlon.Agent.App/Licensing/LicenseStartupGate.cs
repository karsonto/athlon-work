using System.IO;
using Athlon.Agent.Core.Licensing;
using Athlon.Agent.Infrastructure.Licensing;

namespace Athlon.Agent.App.Licensing;

public static class LicenseStartupGate
{
    public static bool EnsureLicensed()
    {
#if DEBUG
        if (string.Equals(
                Environment.GetEnvironmentVariable("ATHLON_SKIP_LICENSE"),
                "1",
                StringComparison.Ordinal))
        {
            return true;
        }
#endif

        var accountResolver = new FallbackAdAccountResolver();
        var validator = new LicenseValidator(accountResolver);
        var store = new LicenseStore();
        var account = accountResolver.ResolveCurrent();

        LicenseValidationResult? lastResult = null;
        foreach (var path in LicenseFileLocator.GetSearchPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            lastResult = validator.ValidateFile(path);
            if (lastResult.IsValid)
            {
                return true;
            }
        }

        var initialFailure = lastResult
            ?? LicenseValidationResult.Fail(
                LicenseFailureCode.Missing,
                LicenseFailureMessages.Describe(LicenseFailureCode.Missing));

        var window = new LicenseActivationWindow(validator, store, account, initialFailure);
        return window.ShowDialog() == true;
    }
}
