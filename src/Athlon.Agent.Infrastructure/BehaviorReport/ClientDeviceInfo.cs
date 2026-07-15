using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.BehaviorReport;

public sealed record ClientDeviceSnapshot
{
    public string UserId { get; init; } = "";
    public string ClientIp { get; init; } = "127.0.0.1";
    public string MacAddress { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string AppName { get; init; } = "Athlon Agent";
    public string AppVersion { get; init; } = "unknown";
    public string ScreenResolution { get; init; } = "";
}

public sealed class ClientDeviceInfo
{
    private readonly IImpSsoSessionStore? _sessionStore;
    private readonly Func<string>? _screenResolutionProvider;
    private readonly string _appName;
    private readonly string _appVersion;
    private ClientDeviceSnapshot? _cached;

    public ClientDeviceInfo(
        IImpSsoSessionStore? sessionStore = null,
        Func<string>? screenResolutionProvider = null,
        string? appName = null,
        string? appVersion = null)
    {
        _sessionStore = sessionStore;
        _screenResolutionProvider = screenResolutionProvider;
        _appName = string.IsNullOrWhiteSpace(appName) ? "Athlon Agent" : appName.Trim();
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion.Trim();
    }

    public ClientDeviceSnapshot GetSnapshot(bool forceRefresh = false)
    {
        if (!forceRefresh && _cached is not null)
        {
            var userId = ResolveUserId();
            if (string.Equals(_cached.UserId, userId, StringComparison.Ordinal))
            {
                return _cached;
            }

            _cached = _cached with { UserId = userId };
            return _cached;
        }

        _cached = new ClientDeviceSnapshot
        {
            UserId = ResolveUserId(),
            ClientIp = ResolveClientIp(),
            MacAddress = ResolveMacAddress(),
            OsVersion = RuntimeInformation.OSDescription,
            AppName = _appName,
            AppVersion = _appVersion,
            ScreenResolution = SafeResolveScreenResolution()
        };
        return _cached;
    }

    public void RefreshUserId()
    {
        if (_cached is null)
        {
            return;
        }

        _cached = _cached with { UserId = ResolveUserId() };
    }

    private string ResolveUserId()
    {
        try
        {
            var session = _sessionStore?.GetCachedSession();
            if (session is null || string.IsNullOrWhiteSpace(session.UserId))
            {
                return "";
            }

            return session.UserId.Trim();
        }
        catch
        {
            return "";
        }
    }

    private string SafeResolveScreenResolution()
    {
        try
        {
            return _screenResolutionProvider?.Invoke() ?? "";
        }
        catch
        {
            return "";
        }
    }

    internal static string ResolveClientIp()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var address in host.AddressList)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address.ToString();
                }
            }
        }
        catch
        {
            // fall through
        }

        return "127.0.0.1";
    }

    internal static string ResolveMacAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length == 0 || bytes.All(b => b == 0))
                {
                    continue;
                }

                return string.Join(':', bytes.Select(b => b.ToString("X2")));
            }
        }
        catch
        {
            // fall through
        }

        return "";
    }
}
