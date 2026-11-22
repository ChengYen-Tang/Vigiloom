namespace Vigiloom.UpdateSecurity;

/// <summary>Manifest content verified against the assigned SHA-256.</summary>
public sealed class VerifiedManifest
{
    internal VerifiedManifest(ReadOnlyMemory<byte> content) => Content = content;
    public ReadOnlyMemory<byte> Content { get; }
}
