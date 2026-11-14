using System.Security.Claims;

namespace Ledger.Api.Security;

internal static class ClaimsPrincipalExtensions
{
    public static bool HasScope(this ClaimsPrincipal user, string requiredScope) =>
        user.FindAll("scope")
            .SelectMany(static claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.Ordinal);

    public static bool HasAccountAccess(this ClaimsPrincipal user, Guid accountId)
    {
        string? restrictedAccount = user.FindFirstValue("account_id");
        return restrictedAccount is null
               || (Guid.TryParse(restrictedAccount, out Guid allowed) && allowed == accountId);
    }
}
