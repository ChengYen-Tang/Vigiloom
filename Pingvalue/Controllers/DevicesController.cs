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
    [Authorize]
    public class DevicesController : Controller
    {
        private PingvalueModels db = new PingvalueModels();

        // GET: Devices
        public async Task<ActionResult> Index()
        {
            return View(await db.Devices.Select(c => 
            new DeviceViewModel {
                Id = c.Id,
                CreateTime = c.CreateTime,
                DeviceName = c.DeviceName,
                IPAddress = c.IPAddress,
                IsOnline = c.IsOnline})
            .ToListAsync());
        }

        [AllowAnonymous]
        // GET: Devices/Details/5
        public async Task<ActionResult> Details(Guid? id, DateTime? Date)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }

            if (Date == null)
                Date = DateTime.Today;

            DateTime MaxDate = Date.Value.AddDays(1);
            List<PingData> Datas = await (
                from Devices in db.Devices
                from PingDatas in Devices.PingDatas
                where Devices.Id == id && PingDatas.CreateTime >= Date && PingDatas.CreateTime < MaxDate
                select PingDatas
                ).OrderByDescending(c => c.CreateTime).ToListAsync();

            if (Datas == null)
            {
                return HttpNotFound();
            }

            string CharDelayList = "";
            string ChartTimeList = "";

            foreach (PingData Data in Datas.OrderBy(c => c.CreateTime))
            {
                long[] ArrayNewPingData = new long[4];
                ArrayNewPingData[0] = Data.Delay1;
                ArrayNewPingData[1] = Data.Delay2;
                ArrayNewPingData[2] = Data.Delay3;
                ArrayNewPingData[3] = Data.Delay4;
                double TotalDelay = 0;
                int DelayCount = 0;
                if (ArrayNewPingData[0] != long.MaxValue || ArrayNewPingData[1] != long.MaxValue || ArrayNewPingData[2] != long.MaxValue || ArrayNewPingData[3] != long.MaxValue)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        DelayCount++;
                        TotalDelay = TotalDelay + Convert.ToDouble(ArrayNewPingData[i]);
                    }

                    CharDelayList += (TotalDelay / DelayCount) + ",";
                }
                else
                    CharDelayList += "null" + ",";
                ChartTimeList = ChartTimeList + Convert.ToChar(34) + Data.CreateTime.Hour + ":" + Data.CreateTime.Minute + Convert.ToChar(34) + ",";
            }
            DetailDeviceViewModel Detail = new DetailDeviceViewModel
            {
                Id = (Guid)id,
                PingDatas = Datas.Select(c => new DetailPingDataViewModel {
                    CreateTime = c.CreateTime.ToString("yyyy-MM-dd HH:mm"),
                    Delay1 = c.Delay1,
                    Delay2 = c.Delay2,
                    Delay3 = c.Delay3,
                    Delay4 = c.Delay4,
                    AverageDelay = (c.Delay1 + c.Delay2 + c.Delay3 + c.Delay4) / 4
                }).ToList(),
                DatetimePicker = Date.Value.Year + "-" + Date.Value.Month + "-" + Date.Value.Day,
                CharDelayList = CharDelayList.TrimEnd(new char[] { ',' }),
                ChartTimeList = ChartTimeList.TrimEnd(new char[] { ','})
            };
            return View(Detail);
        }

        // GET: Devices/Create
        public async Task<ActionResult> Create()
        {
            ViewBag.RoleId = new SelectList(await db.DeviceGroups.ToListAsync(), "Id", "GroupName");
            return View();
        }

        // POST: Devices/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see https://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Create(CreateDeviceViewModel device, params Guid[] selectedGroups)
        {
            if (ModelState.IsValid)
            {
                selectedGroups = selectedGroups ?? new Guid[] { };

                db.Devices.Add(new Device {
                    Id = Guid.NewGuid(),
                    DeviceName = device.DeviceName,
                    IPAddress = device.IPAddress,
                    IsOnline = false,
                    DeviceGroups = await db.DeviceGroups.Where(c => selectedGroups.Contains(c.Id)).ToListAsync()
                });
                await db.SaveChangesAsync();
                return RedirectToAction("Index");
            }

            return View(device);
        }

        // GET: Devices/Edit/5
        public async Task<ActionResult> Edit(Guid? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Device device = await db.Devices.FindAsync(id);

            if (device == null)
            {
                return HttpNotFound();
            }
            return View(new EditeDeviceViewModel
            {
                Id = device.Id,
                DeviceName = device.DeviceName,
                IPAddress = device.IPAddress,
                GroupList = db.DeviceGroups.ToList().Select(x => new SelectListItem
                {
                    Selected = device.DeviceGroups.Contains(x),
                    Text = x.GroupName,
                    Value = x.Id.ToString()
                })
            });
        }

        // POST: Devices/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see https://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Edit([Bind(Include = "Id,DeviceName,IPAddress")] EditeDeviceViewModel NewDevice, params Guid[] selectedGroups)
        {
            if (ModelState.IsValid)
            {
                Device device = await db.Devices.FindAsync(NewDevice.Id);
                if (device == null)
                {
                    return HttpNotFound();
                }
                device.DeviceName = NewDevice.DeviceName;
                device.IPAddress = NewDevice.IPAddress;
                device.DeviceGroups.Clear();

                selectedGroups = selectedGroups ?? new Guid[] { };

                var ExceptGroups = db.DeviceGroups.Where(c => selectedGroups.Contains(c.Id)).Except(device.DeviceGroups);

                foreach (DeviceGroup Group in ExceptGroups)
                {
                    if (selectedGroups.Contains(Group.Id))
                        device.DeviceGroups.Add(Group);
                    else
                        device.DeviceGroups.Remove(Group);
                }

                db.Entry(device).State = EntityState.Modified;
                await db.SaveChangesAsync();
                return RedirectToAction("Index");
            }
            return View(NewDevice);
        }

        // GET: Devices/Delete/5
        public async Task<ActionResult> Delete(Guid? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Device device = await db.Devices.FindAsync(id);
            if (device == null)
            {
                return HttpNotFound();
            }
            return View(new DeviceViewModel {
                Id = device.Id,
                CreateTime = device.CreateTime,
                DeviceName = device.DeviceName,
                IPAddress = device.IPAddress,
                IsOnline = device.IsOnline
            });
        }

        // POST: Devices/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> DeleteConfirmed(Guid id)
        {
            List<PingData> pingDatas = await db.PingDatas.Where(c => c.Device.Id == id).ToListAsync();
            db.PingDatas.RemoveRange(pingDatas);
            Device device = await db.Devices.FindAsync(id);
            db.Devices.Remove(device);
            await db.SaveChangesAsync();
            return RedirectToAction("Index");
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
