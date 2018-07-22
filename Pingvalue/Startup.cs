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
            ConfigureAuth(app);
            AppConfig.LoadConfig();

            GlobalConfiguration.Configuration.UseSqlServerStorage("DefaultConnection");
            RecurringJob.AddOrUpdate(() => WorkScript.StartPing(), "*/5 * * * *");
            RecurringJob.AddOrUpdate(() => WorkScript.StartSpeedTest(), "*/30 * * * *");
            RecurringJob.AddOrUpdate(() => WorkScript.ClearOldData(), "0 0 * * *");

            app.UseHangfireServer();
            app.UseHangfireDashboard();
        }
    }
}
