using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Logical metadata from an OTLP resource; it is never an authenticated source identity.</summary>
internal sealed record ResourceIdentity(string ServiceKey, string Environment, string? InstanceId, string ServiceNamespace)
{
    /// <summary>Reject ambiguous or invalid identities rather than selecting an arbitrary duplicate attribute.</summary>
    internal static bool TryRead(Resource? resource, out ResourceIdentity? identity)
    {
        identity = null;
        if (resource is null) return false;
        string? service = null, environment = null, instance = null, serviceNamespace = null;
        var seenService = false;
        var seenEnvironment = false;
        var seenInstance = false;
        var seenNamespace = false;
        foreach (var attribute in resource.Attributes)
        {
            switch (attribute.Key)
            {
                case "service.name":
                    if (seenService || !ReadString(attribute, out service)) return false;
                    seenService = true;
                    break;
                case "service.namespace":
                    if (seenNamespace || !ReadString(attribute, out serviceNamespace)) return false;
                    seenNamespace = true;
                    break;
                case "deployment.environment.name":
                    if (seenEnvironment || !ReadString(attribute, out environment)) return false;
                    seenEnvironment = true;
                    break;
                case "service.instance.id":
                    if (seenInstance || !ReadString(attribute, out instance)) return false;
                    seenInstance = true;
                    break;
            }
        }
        if (service is null || environment is null || service.Length > 128 || environment.Length > 64 ||
            string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(environment) || !IsValidUnicode(service) || !IsValidUnicode(environment) ||
            instance is { Length: > 256 } || instance is not null && !IsValidUnicode(instance) ||
            serviceNamespace is { Length: > 128 } || serviceNamespace is not null && !IsValidUnicode(serviceNamespace)) return false;
        identity = new(service.Trim(), environment.Trim(), string.IsNullOrEmpty(instance) ? null : instance, serviceNamespace ?? "");
        return true;
    }

    private static bool ReadString(KeyValue attribute, out string? value)
    {
        value = attribute.Value?.ValueCase == AnyValue.ValueOneofCase.StringValue ? attribute.Value.StringValue : null;
        return value is not null;
    }

    private static bool IsValidUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !char.IsLowSurrogate(value[++i])) return false;
        }
        return true;
    }
}
