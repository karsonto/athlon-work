using System.Runtime.InteropServices;
using System.Security.Principal;
using Athlon.Agent.Core.Licensing;

namespace Athlon.Agent.Infrastructure.Licensing;

/// <summary>Non-Windows fallback for tests and dev on other OSes.</summary>
public sealed class FallbackAdAccountResolver : IAdAccountResolver
{
    public AdAccountInfo ResolveCurrent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsAdAccountResolver().ResolveCurrent();
        }

        var name = Environment.UserName;
        var domain = Environment.UserDomainName;
        var sam = string.IsNullOrWhiteSpace(domain) || domain == name
            ? name
            : $"{domain}\\{name}";

        return new AdAccountInfo(sam, null);
    }
}
