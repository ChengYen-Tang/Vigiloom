using Microsoft.Owin;
using Owin;

[assembly: OwinStartupAttribute(typeof(Pingvalue.Startup))]
namespace Pingvalue
{
    public partial class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            ConfigureAuth(app);
            AppConfig.LoadConfig();
        }
    }
}
