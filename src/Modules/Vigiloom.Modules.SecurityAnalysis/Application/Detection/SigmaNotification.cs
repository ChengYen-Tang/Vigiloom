using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Notifications;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal static class SigmaNotification
{
    internal static NotificationRequest Create(Guid deviceId, HostLogEntry log, SigmaSourceRule rule, SigmaEnabledRuleVersion version) =>
        new(Guid.CreateVersion7(), "sigma", log.OccurredAt ?? log.ReceivedAt, "行為規則命中",
            $"設備：{deviceId:D}；規則：{rule.Title}；來源：{rule.Path}；規則測試：{version.TestId:D}；版本：{Convert.ToBase64String(version.Version)}；原始事件：{log.AgentId:D}/{log.MessageId:D}。此結果需要進一步調查。",
            ResourceId: deviceId, EvidenceOwnerId: log.AgentId, EvidenceId: log.MessageId, RuleTestId: version.TestId);
}
