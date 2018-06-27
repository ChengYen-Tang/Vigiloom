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
    public class PingDatasController : Controller
    {
        private PingvalueModels db = new PingvalueModels();

        // GET: PingDatas
        public async Task<ActionResult> Index()
        {
            return View(await db.PingDatas.ToListAsync());
        }

        // GET: PingDatas/Details/5
        public async Task<ActionResult> Details(Guid? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            PingData pingData = await db.PingDatas.FindAsync(id);
            if (pingData == null)
            {
                return HttpNotFound();
            }
            return View(pingData);
        }

        // GET: PingDatas/Create
        public ActionResult Create()
        {
            return View();
        }

        // POST: PingDatas/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see https://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Create([Bind(Include = "Id,CreateTime,Delay1,Delay2,Delay3,Delay4")] PingData pingData)
        {
            if (ModelState.IsValid)
            {
                pingData.Id = Guid.NewGuid();
                db.PingDatas.Add(pingData);
                await db.SaveChangesAsync();
                return RedirectToAction("Index");
            }

            return View(pingData);
        }

        // GET: PingDatas/Edit/5
        public async Task<ActionResult> Edit(Guid? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            PingData pingData = await db.PingDatas.FindAsync(id);
            if (pingData == null)
            {
                return HttpNotFound();
            }
            return View(pingData);
        }

        // POST: PingDatas/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see https://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Edit([Bind(Include = "Id,CreateTime,Delay1,Delay2,Delay3,Delay4")] PingData pingData)
        {
            if (ModelState.IsValid)
            {
                db.Entry(pingData).State = EntityState.Modified;
                await db.SaveChangesAsync();
                return RedirectToAction("Index");
            }
            return View(pingData);
        }

        // GET: PingDatas/Delete/5
        public async Task<ActionResult> Delete(Guid? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            PingData pingData = await db.PingDatas.FindAsync(id);
            if (pingData == null)
            {
                return HttpNotFound();
            }
            return View(pingData);
        }

        // POST: PingDatas/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> DeleteConfirmed(Guid id)
        {
            PingData pingData = await db.PingDatas.FindAsync(id);
            db.PingDatas.Remove(pingData);
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
