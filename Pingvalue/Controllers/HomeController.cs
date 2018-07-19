using System.Web.Mvc;
using System.Threading.Tasks;
using Pingvalue.Models;
using System.Data.Entity;
using System.Linq;

namespace Pingvalue.Controllers
{
    public class HomeController : Controller
    {
        private PingvalueModels db = new PingvalueModels();

        public async Task<ActionResult> Index()
        {
            var Release = from Devices in db.Devices
                          select new HomeViewModels { Id = Devices.Id, DeviceName = Devices.DeviceName, IPAddress = Devices.IPAddress, IsOnline = Devices.IsOnline };

            return View(await Release.OrderBy(c => c.DeviceName).ToListAsync());
        }

        public ActionResult Menu()
        {
            return View(db.DeviceGroups.ToList());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                db.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}