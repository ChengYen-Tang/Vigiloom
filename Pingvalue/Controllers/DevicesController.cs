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

        // GET: Devices/Details/5
        public async Task<ActionResult> Details(Guid? id)
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
            return View(device);
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
