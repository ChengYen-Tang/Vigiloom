using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Hosting;
using Vigiloom.Modules.SecurityAnalysis.Application.ScheduledJobs;
using Vigiloom.Security.Jobs;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
public sealed class SecurityScheduleTests
{
    [TestMethod]
    public async Task SecurityJobsUseSecurityQueueAndSharedAssembly()
    {
        var manager = new RecordingManager();
        IHostedService[] schedules =
        [
            new IntelligenceRefreshSchedule(manager), new DebianRefreshSchedule(manager), new UbuntuRefreshSchedule(manager),
            new MsrcRefreshSchedule(manager), new EpssRefreshSchedule(manager), new SigmaRefreshSchedule(manager),
            new SignatureRefreshSchedule(manager), new BaselineRefreshSchedule(manager), new AdvisoryRefreshSchedule(manager),
            new InventoryExpirySchedule(manager), new IntegrityExpirySchedule(manager),
            new LinuxReassessmentSchedule(manager), new WindowsReassessmentSchedule(manager)
        ];
        foreach (var schedule in schedules) await schedule.StartAsync(CancellationToken.None);
        Assert.HasCount(13, manager.Jobs);
        Assert.AreEqual(13, manager.Jobs.Select(x => x.Id).Distinct().Count());
        foreach (var (id, job) in manager.Jobs)
        {
            Assert.IsTrue(id.StartsWith("vigiloom-security-", StringComparison.Ordinal));
            Assert.AreEqual("security", job.Queue);
            Assert.AreEqual(typeof(IntelligenceRefreshJob).Assembly, job.Type.Assembly);
        }
    }

    private sealed class RecordingManager : IRecurringJobManager
    {
        public List<(string Id, Job Job)> Jobs { get; } = [];
        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) => Jobs.Add((recurringJobId, job));
        public void Trigger(string recurringJobId) { }
        public void RemoveIfExists(string recurringJobId) { }
    }
}
