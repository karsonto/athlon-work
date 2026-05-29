using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Athlon.Agent.Core.Licensing;

namespace Athlon.Agent.Infrastructure.Licensing;

[SupportedOSPlatform("windows")]
public sealed class WindowsAdAccountResolver : IAdAccountResolver
{
    public AdAccountInfo ResolveCurrent()
    {
        var sam = WindowsIdentity.GetCurrent().Name;
        var upn = TryResolveUpn();
        return new AdAccountInfo(sam, upn);
    }

    private static string? TryResolveUpn()
    {
        try
        {
            using var context = new System.DirectoryServices.AccountManagement.PrincipalContext(
                System.DirectoryServices.AccountManagement.ContextType.Domain);
            using var user = System.DirectoryServices.AccountManagement.UserPrincipal.FindByIdentity(
                context,
                System.DirectoryServices.AccountManagement.IdentityType.SamAccountName,
                Environment.UserName);

            if (!string.IsNullOrWhiteSpace(user?.UserPrincipalName))
            {
                return user.UserPrincipalName;
            }
        }
        catch
        {
            // Fall through to whoami.
        }

        return TryResolveUpnViaWhoAmI();
    }

    private static string? TryResolveUpnViaWhoAmI()
    {
        try
        {
            var startInfo = new ProcessStartInfo("whoami", "/upn")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);

            if (process.ExitCode != 0
                || string.IsNullOrWhiteSpace(output)
                || output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return output;
        }
        catch
        {
            return null;
        }
    }
}
