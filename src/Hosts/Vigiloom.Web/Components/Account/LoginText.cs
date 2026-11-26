using System.Resources;

namespace Vigiloom.Web.Components.Account;

/// <summary>Resource access for login form validation attributes.</summary>
public sealed class LoginText
{
    private static readonly ResourceManager Resources = new(typeof(LoginText));
    public static string RequiredUserName => Resources.GetString(nameof(RequiredUserName))!;
    public static string UserNameLength => Resources.GetString(nameof(UserNameLength))!;
    public static string RequiredPassword => Resources.GetString(nameof(RequiredPassword))!;
    public static string PasswordLength => Resources.GetString(nameof(PasswordLength))!;
}
