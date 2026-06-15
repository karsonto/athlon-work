namespace Athlon.Agent.Core.Sso;

public interface IImpSsoSessionStore
{
    ImpSsoSession? GetCachedSession();

    void SaveSession(ImpSsoSession session);

    void Clear();

    bool IsExpired(ImpSsoSession session);
}
