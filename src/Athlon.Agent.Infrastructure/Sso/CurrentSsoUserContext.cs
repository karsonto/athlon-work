using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.Sso;

public sealed class CurrentSsoUserContext(AppSettings settings, IImpSsoSessionStore sessionStore) : ICurrentSsoUserContext
{
    public string? DisplayName
    {
        get
        {
            if (!settings.Sso.Enabled)
            {
                return null;
            }

            var session = sessionStore.GetCachedSession();
            if (session is null || sessionStore.IsExpired(session))
            {
                return null;
            }

            var displayName = session.DisplayName;
            return string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        }
    }
}
