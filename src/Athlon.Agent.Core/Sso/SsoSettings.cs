namespace Athlon.Agent.Core.Sso;

public sealed class SsoSettings
{
    public bool Enabled { get; set; }

    public string ImpDomain { get; set; } = "www.icbcasia.com";

    public string AppId { get; set; } = "252";

    public string Version { get; set; } = "20251127";

    public int SessionValidityHours { get; set; } = 24;

    public int CallbackPort { get; set; } = 5657;

    public string CallbackPath { get; set; } = "/sso/auth";

    public string CompletePath { get; set; } = "/sso/complete";

    public string CallbackBaseUrl => $"http://localhost:{CallbackPort}";

    public string CallbackUrl => $"{CallbackBaseUrl}{CallbackPath}";
}
