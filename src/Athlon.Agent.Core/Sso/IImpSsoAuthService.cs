namespace Athlon.Agent.Core.Sso;

public interface IImpSsoAuthService
{
    string BuildImpLoginUrl(SsoSettings settings);

    Task<ImpSsoCheckResult> CheckSsoTokenAsync(
        string ssoToken,
        SsoSettings settings,
        CancellationToken cancellationToken = default);

    Task<ImpSsoCheckResult> CompleteLoginAsync(
        ImpSsoCallbackPayload payload,
        SsoSettings settings,
        CancellationToken cancellationToken = default);
}
