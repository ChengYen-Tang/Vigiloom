using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using System.Web;
using System.Web.Mvc;
using Pingvalue.Models;

namespace Pingvalue.Controllers
{
    public class SpeedTestsController : Controller
    {
        private PingvalueModels db = new PingvalueModels();

        // GET: SpeedTests
        public async Task<ActionResult> Index(DateTime? Date)
        {
            if (Date == null)
                Date = DateTime.Today;

            DateTime MaxDate = Date.Value.AddDays(1);
            List<SpeedTestViewModel> SpeedTestDatas = await db.SpeedTests
                .Where(c => c.TestTime >= Date && c.TestTime < MaxDate)
                .OrderByDescending(c => c.TestTime)
                .Select(c => new SpeedTestViewModel
                {
                    Id = c.Id,
                    TestTime = c.TestTime.ToString(),
                    SpeedDownload = c.SpeedDownload,
                    SpeedUpload = c.SpeetUpload
                }).ToListAsync();

            string ChartTimeList = "";
            string CharDownloadList = "";
            string CharUploadList = "";

            foreach (var SpeedTestData in SpeedTestDatas.OrderBy(c => c.TestTime))
            {
                SpeedTestData.TestTime = DateTime.Parse(SpeedTestData.TestTime).ToString("yyyy-MM-dd HH:mm");
                if (SpeedTestData.SpeedDownload == "Failed")
                {
                    SpeedTestData.SpeedDownload = "0 bps";
                }
                if (SpeedTestData.SpeedUpload == "Failed")
                {
                    SpeedTestData.SpeedUpload = "0 bps";
                }
                string[] DownloadStrings = SpeedTestData.SpeedDownload.Split(' ');
                string[] UploadStrings = SpeedTestData.SpeedUpload.Split(' ');
                if (DownloadStrings[1] == "bps")
                {
                    DownloadStrings[0] = (Convert.ToDouble(DownloadStrings[0]) / 1048576).ToString();
                }
                if (DownloadStrings[1] == "Kbps")
                {
                    DownloadStrings[0] = (Convert.ToDouble(DownloadStrings[0]) / 1024).ToString();
                }
                if (UploadStrings[1] == "bps")
                {
                    UploadStrings[0] = (Convert.ToDouble(UploadStrings[0]) / 1048576).ToString();
                }
                if (UploadStrings[1] == "Kbps")
                {
                    UploadStrings[0] = (Convert.ToDouble(UploadStrings[0]) / 1024).ToString();
                }
                ChartTimeList = ChartTimeList + Convert.ToChar(34) + DateTime.Parse(SpeedTestData.TestTime).Hour + ":" + DateTime.Parse(SpeedTestData.TestTime).Minute + Convert.ToChar(34) + ",";
                CharDownloadList = CharDownloadList + DownloadStrings[0] + ",";
                CharUploadList = CharUploadList + UploadStrings[0] + ",";
            }

            return View(new IndexSpeedTestViewModel {
                SpeedTestDatas = SpeedTestDatas,
                DatetimePicker = Date.Value.Year + "-" + Date.Value.Month + "-" + Date.Value.Day,
                ChartTimeList = ChartTimeList.TrimEnd(new char[] { ',' }),
                CharUploadList = CharUploadList.TrimEnd(new char[] { ',' }),
                CharDownloadList = CharDownloadList.TrimEnd(new char[] { ',' })
            });
        }

        // GET: SpeedTests/Details/5
        public async Task<ActionResult> Details(Guid? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            SpeedTest speedTest = await db.SpeedTests.FindAsync(id);
            if (speedTest == null)
            {
                return HttpNotFound();
            }
            return View(new DetailSpeedTestViewModel {
                Id = speedTest.Id,
                ISP = speedTest.ISP,
                Server = speedTest.Server,
                TestTime = speedTest.TestTime,
                DelayTime = speedTest.DelayTime,
                ClientLatitude = speedTest.ClientLatitude,
                ClientLongitude = speedTest.ClientLongitude,
                ServerLatitude = speedTest.ServerLatitude,
                ServerLongitude = speedTest.ServerLongitude,
                SpeedDownload = speedTest.SpeedDownload,
                SpeedUpload = speedTest.SpeetUpload
            });
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
