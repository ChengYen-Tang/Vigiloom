using System.Resources;

namespace Vigiloom.Web.Components.Account;

public sealed class ResetPasswordText
{
    private static readonly ResourceManager Resources = new(typeof(ResetPasswordText));
    public static string RequiredCode => Resources.GetString(nameof(RequiredCode))!;
    public static string CodeLength => Resources.GetString(nameof(CodeLength))!;
    public static string RequiredPassword => Resources.GetString(nameof(RequiredPassword))!;
}
