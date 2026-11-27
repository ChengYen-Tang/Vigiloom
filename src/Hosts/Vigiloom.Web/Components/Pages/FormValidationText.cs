using System.Resources;

namespace Vigiloom.Web.Components.Pages;

public sealed class FormValidationText
{
    private static readonly ResourceManager Resources = new(typeof(FormValidationText));
    public static string Required => Resources.GetString(nameof(Required))!;
    public static string StringLength => Resources.GetString(nameof(StringLength))!;
    public static string Range => Resources.GetString(nameof(Range))!;
}
