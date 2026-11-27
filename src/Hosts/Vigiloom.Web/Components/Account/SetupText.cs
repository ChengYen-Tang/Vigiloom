using System.Resources;

namespace Vigiloom.Web.Components.Account;

public sealed class SetupText
{
    private static readonly ResourceManager Resources = new(typeof(SetupText));
    public static string RequiredToken => Resources.GetString(nameof(RequiredToken))!;
    public static string TokenLength => Resources.GetString(nameof(TokenLength))!;
    public static string RequiredConfirmation => Resources.GetString(nameof(RequiredConfirmation))!;
    public static string PasswordMismatch => Resources.GetString(nameof(PasswordMismatch))!;
}
