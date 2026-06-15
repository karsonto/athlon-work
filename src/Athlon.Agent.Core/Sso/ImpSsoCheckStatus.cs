namespace Athlon.Agent.Core.Sso;

public enum ImpSsoCheckStatus
{
    Valid,
    Missing,
    LoginRequired,
    ReLoginRequired,
    NoRole,
    Invalid
}

public sealed class ImpSsoCheckResult
{
    public ImpSsoCheckStatus Status { get; init; }

    public string Message { get; init; } = "";

    public ImpSsoSession? Session { get; init; }

    public bool IsValid => Status == ImpSsoCheckStatus.Valid && Session is not null;

    public static ImpSsoCheckResult Success(ImpSsoSession session) =>
        new() { Status = ImpSsoCheckStatus.Valid, Session = session };

    public static ImpSsoCheckResult Fail(ImpSsoCheckStatus status, string message) =>
        new() { Status = status, Message = message };
}
