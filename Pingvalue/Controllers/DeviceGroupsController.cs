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
    public class DeviceGroupsController : Controller
    {
        private PingvalueModels db = new PingvalueModels();

        // GET: DeviceGroups
        public async Task<ActionResult> Index()
        {
            return View(await db.DeviceGroups.ToListAsync());
        }

        // GET: DeviceGroups/Details/5
        public async Task<ActionResult> Details(Guid? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            DeviceGroup deviceGroup = await db.DeviceGroups.FindAsync(id);
            if (deviceGroup == null)
            {
                return HttpNotFound();
            }
            return View(deviceGroup);
        }

        // GET: DeviceGroups/Create
        public ActionResult Create()
        {
            return View();
        }

        // POST: DeviceGroups/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see https://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Create([Bind(Include = "Id,GroupName,CreateTime")] DeviceGroup deviceGroup)
        {
            if (ModelState.IsValid)
            {
                deviceGroup.Id = Guid.NewGuid();
                db.DeviceGroups.Add(deviceGroup);
                await db.SaveChangesAsync();
                return RedirectToAction("Index");
            }

            return View(deviceGroup);
        }

        // GET: DeviceGroups/Edit/5
        public async Task<ActionResult> Edit(Guid? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            DeviceGroup deviceGroup = await db.DeviceGroups.FindAsync(id);
            if (deviceGroup == null)
            {
                return HttpNotFound();
            }
            return View(deviceGroup);
        }

        // POST: DeviceGroups/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see https://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Edit([Bind(Include = "Id,GroupName,CreateTime")] DeviceGroup deviceGroup)
        {
            if (ModelState.IsValid)
            {
                db.Entry(deviceGroup).State = EntityState.Modified;
                await db.SaveChangesAsync();
                return RedirectToAction("Index");
            }
            return View(deviceGroup);
        }

        // GET: DeviceGroups/Delete/5
        public async Task<ActionResult> Delete(Guid? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            DeviceGroup deviceGroup = await db.DeviceGroups.FindAsync(id);
            if (deviceGroup == null)
            {
                return HttpNotFound();
            }
            return View(deviceGroup);
        }

        // POST: DeviceGroups/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> DeleteConfirmed(Guid id)
        {
            DeviceGroup deviceGroup = await db.DeviceGroups.FindAsync(id);
            db.DeviceGroups.Remove(deviceGroup);
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
