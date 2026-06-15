using System.Net;
using System.Text;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.Sso;

public sealed class ImpSsoAuthService(HttpClient httpClient) : IImpSsoAuthService
{
    public string BuildImpLoginUrl(SsoSettings settings)
    {
        var appId = Uri.EscapeDataString(settings.AppId);
        var msg = Uri.EscapeDataString(settings.Msg);
        return $"https://{settings.ImpDomain}/icbcasia/imp/index.html" +
               $"?toLogin=true&appId={appId}&msg={msg}#/login";
    }

    public async Task<ImpSsoCheckResult> CheckSsoTokenAsync(
        string ssoToken,
        SsoSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ssoToken))
        {
            return ImpSsoCheckResult.Fail(ImpSsoCheckStatus.Missing, "ssotoken 为空。");
        }

        var httpResult = await PostCheckSsoTokenAsync(ssoToken, settings, cancellationToken);
        var parsed = ImpSsoResponseParser.Parse(httpResult);
        return MapParsedResult(parsed, ssoToken, settings);
    }

    public async Task<ImpSsoCheckResult> CompleteLoginAsync(
        ImpSsoCallbackPayload payload,
        SsoSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.Token))
        {
            return ImpSsoCheckResult.Fail(ImpSsoCheckStatus.Missing, "回调缺少 token。");
        }

        if (!string.IsNullOrWhiteSpace(payload.AppId)
            && !string.Equals(payload.AppId, settings.AppId, StringComparison.Ordinal))
        {
            return ImpSsoCheckResult.Fail(ImpSsoCheckStatus.Invalid, "回调 appId 与配置不匹配。");
        }

        return await CheckSsoTokenAsync(payload.Token, settings, cancellationToken);
    }

    internal static ImpSsoCheckResult MapParsedResult(
        ImpSsoParsedResponse parsed,
        string ssoToken,
        SsoSettings settings)
    {
        if (parsed.Status == ImpSsoCheckStatus.Valid)
        {
            var loggedInAt = DateTimeOffset.UtcNow;
            var expiresAt = loggedInAt.AddHours(settings.SessionValidityHours);
            var session = new ImpSsoSession
            {
                SsoToken = ssoToken,
                UserId = parsed.UserId ?? "",
                DisplayName = parsed.DisplayName ?? parsed.UserId ?? "",
                LoggedInAt = loggedInAt,
                ExpiresAt = expiresAt
            };
            return ImpSsoCheckResult.Success(session);
        }

        return ImpSsoCheckResult.Fail(parsed.Status, parsed.Message);
    }

    private async Task<string?> PostCheckSsoTokenAsync(
        string ssoToken,
        SsoSettings settings,
        CancellationToken cancellationToken)
    {
        var requestIp = ResolveRequestIp();
        var url =
            $"https://{settings.ImpDomain}/icbcasia/imp/check_ssotoken?version={Uri.EscapeDataString(settings.Version)}";
        var body =
            $"ssotoken={Uri.EscapeDataString(ssoToken)}" +
            $"&impappid={Uri.EscapeDataString(settings.AppId)}" +
            $"&dse_sessionId={Uri.EscapeDataString(ssoToken)}" +
            $"&requestip={Uri.EscapeDataString(requestIp)}";

        using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await httpClient.PostAsync(url, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string ResolveRequestIp()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var address in host.AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
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
}
