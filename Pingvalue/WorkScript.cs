using Pingvalue.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace Pingvalue
{
    public class WorkScript
    {
        private static PingvalueModels db = new PingvalueModels();

        public static void ClearOldData()
        {
            DateTime Date = DateTime.Today.AddYears(-2);
            var PingOldData = db.PingDatas.Where(c => c.CreateTime <= Date).ToList();
            var SpeedOldData = db.SpeedTests.Where(c => c.TestTime <= Date).ToList();
            db.PingDatas.RemoveRange(PingOldData);
            db.SpeedTests.RemoveRange(SpeedOldData);
            db.SaveChanges();
        }

        public static void StartPing()
        {

        }

        public static void StartSpeedTest()
        {

        }
    }
}