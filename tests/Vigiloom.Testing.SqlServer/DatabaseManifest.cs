namespace Vigiloom.Testing.SqlServer;

internal sealed record DatabaseManifest(
    string RunId,
    string CaseId,
    string DatabaseName,
    string DataSource,
    int OwnerProcessId,
    long OwnerStartTimeUtcTicks,
    string DirectoryPath);
