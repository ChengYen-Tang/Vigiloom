namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum MatchState { Affected, Fixed, NotAffected, Unknown, Unsupported, NotEvaluated }
internal enum PackageOrigin { Unknown, AssumedDistribution, Conflicting, VerifiedDistribution }
