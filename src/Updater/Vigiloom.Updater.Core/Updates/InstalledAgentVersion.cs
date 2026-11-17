using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NuGet.Versioning;

namespace Vigiloom.Updater.Core.Updates;

/// <summary>Reads the installed Agent's informational version without loading or executing it.</summary>
public static class InstalledAgentVersion
{
    public static string Read(string versionDirectory)
    {
        var path = Path.Combine(versionDirectory, "Vigiloom.Agent.dll");
        using var stream = File.OpenRead(path);
        using var image = new PEReader(stream);
        if (!image.HasMetadata) throw new InvalidDataException("The installed Agent assembly has no metadata.");
        var metadata = image.GetMetadataReader();
        if (!metadata.IsAssembly) throw new InvalidDataException("The installed Agent is not an assembly.");
        var assembly = metadata.GetAssemblyDefinition();
        if (metadata.GetString(assembly.Name) != "Vigiloom.Agent")
            throw new InvalidDataException("The installed assembly is not the Agent.");
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection" ||
                metadata.GetString(type.Name) != "AssemblyInformationalVersionAttribute") continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) break;
            var version = Normalize(blob.ReadSerializedString());
            if (version is not null) return version;
            break;
        }
        throw new InvalidDataException("The installed Agent version is missing or invalid.");
    }

    internal static string? Normalize(string? value)
    {
        var version = value?.Split('+', 2)[0];
        return version is { Length: > 0 and <= 128 } && NuGetVersion.TryParse(version, out var parsed) &&
            !parsed.HasMetadata && version == parsed.ToNormalizedString() ? version : null;
    }
}
