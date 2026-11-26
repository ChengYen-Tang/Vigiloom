using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Notifications;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal static class SigmaNotification
{
    internal static NotificationRequest Create(Guid deviceId, HostLogEntry log, SigmaSourceRule rule, SigmaEnabledRuleVersion version) =>
        new(Guid.CreateVersion7(), "sigma", log.OccurredAt ?? log.ReceivedAt, "Behavior rule matched",
            $"Device: {deviceId:D}; Rule: {rule.Title}; Source: {rule.Path}; Rule test: {version.TestId:D}; Version: {Convert.ToBase64String(version.Version)}; Event: {log.AgentId:D}/{log.MessageId:D}. Further investigation is required.",
            ResourceId: deviceId, EvidenceOwnerId: log.AgentId, EvidenceId: log.MessageId, RuleTestId: version.TestId);
}
