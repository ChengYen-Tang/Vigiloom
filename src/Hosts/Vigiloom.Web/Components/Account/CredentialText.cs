using System.Resources;

namespace Vigiloom.Web.Components.Account;

public sealed class CredentialText
{
    private static readonly ResourceManager Resources = new(typeof(CredentialText));
    public static string RequiredName => Resources.GetString(nameof(RequiredName))!;
    public static string NameLength => Resources.GetString(nameof(NameLength))!;
    public static string ValidityRange => Resources.GetString(nameof(ValidityRange))!;
    public static string RequiredSelection => Resources.GetString(nameof(RequiredSelection))!;
}
