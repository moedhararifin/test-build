using Microsoft.AspNetCore.Http;

namespace Dollhouse.Server.Accounts;

public static class LocalSession
{
    public static LocalLogin? Resolve(IHeaderDictionary headers, LocalAccountStore accounts)
    {
        var values = headers["X-Rehost-Session"];
        if (values.Count == 0 || string.IsNullOrEmpty(values[0])) return null;
        var session = values[0]!;
        // Native license requests repeat this header. Never select one of conflicting values.
        foreach (var value in values)
            if (!string.Equals(value, session, StringComparison.Ordinal)) return null;
        return accounts.FindSession(session);
    }
}
