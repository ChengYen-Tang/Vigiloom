using System.Globalization;
using System.Security.Claims;

namespace Vigiloom.Web.Authentication;

internal static class SessionLifetime
{
    public const string ExpirationClaim = "Vigiloom.SessionExpiresUnixMs";

    public static bool IsCurrent(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ExpirationClaim);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var expires) &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < expires;
    }
}
