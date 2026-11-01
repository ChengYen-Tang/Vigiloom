namespace Vigiloom.Testing.SqlServer;

internal sealed record RunManifest(string RunId, int OwnerProcessId, long OwnerStartTimeUtcTicks);
