using System.Resources;

namespace Vigiloom.Web.Components.Account;

/// <summary>Resources for the sign-in challenge and its form validation.</summary>
public sealed class TwoFactorText
{
    private static readonly ResourceManager Resources = new(typeof(TwoFactorText));
    public static string RequiredCode => Resources.GetString(nameof(RequiredCode))!;
    public static string CodeLength => Resources.GetString(nameof(CodeLength))!;
}
