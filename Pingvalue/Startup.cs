using Microsoft.Owin;
using Owin;
using Hangfire;

[assembly: OwinStartupAttribute(typeof(Pingvalue.Startup))]
namespace Pingvalue
{
    public partial class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            LogGenerator.Add("Owin startup");
            ConfigureAuth(app);
            AppConfig.LoadConfig();

            GlobalConfiguration.Configuration.UseSqlServerStorage("DefaultConnection");
            RecurringJob.AddOrUpdate(() => WorkScript.StartPingAsync(), "*/5 * * * *");
            RecurringJob.AddOrUpdate(() => WorkScript.StartSpeedTestAsync(), "*/30 * * * *");
            RecurringJob.AddOrUpdate(() => WorkScript.ClearOldDataAsync(), "0 0 * * *");

            app.UseHangfireServer();
            app.UseHangfireDashboard();
            LogGenerator.Add("Owin startup done.");
        }
    }
}
