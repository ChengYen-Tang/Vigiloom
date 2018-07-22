using Pingvalue.Models;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
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

        public static async Task StartPingAsync()
        {
            DateTime PingTime = DateTime.Now;
            List<PingData> PingDatas = new List<PingData>();
            List<string> StatusChangeDevices = new List<string>();
            object PingLock = new object();

            Parallel.ForEach(await db.Devices.ToListAsync(), (device) =>
            {
                long[] PingDelay = new long[5];
                for (int i = 0; i < 5; i++)
                {
                    Ping pingSender = new Ping();
                    IPAddress address = IPAddress.Parse(device.IPAddress);
                    PingReply reply = pingSender.Send(address, 999);
                    if (reply.Status == IPStatus.Success)
                    {
                        PingDelay[i] = reply.RoundtripTime;
                    }
                    else
                    {
                        PingDelay[i] = -1;
                    }
                }

                lock (PingLock)
                {
                    if (PingDelay.Where(c => c != -1).Count() > 0)
                    {
                        if (!device.IsOnline)
                        {
                            device.IsOnline = !device.IsOnline;
                            StatusChangeDevices.Add(
                                "群組 :" + string.Join(",", device.DeviceGroups.Select(c => c.GroupName)) + 
                                " 設備 :" + device.DeviceName +
                                " IP位置 :" + device.IPAddress + 
                                " 在 " + PingTime +
                                " 恢復連線"
                                );
                            db.Entry(device).State = EntityState.Modified;
                        }
                    }
                    else
                        if (device.IsOnline)
                        {
                            device.IsOnline = !device.IsOnline;
                            StatusChangeDevices.Add(
                                "群組 :" + string.Join(",", device.DeviceGroups.Select(c => c.GroupName)) +
                                " 設備 :" + device.DeviceName +
                                " IP位置 :" + device.IPAddress +
                                " 在 " + PingTime +
                                " 4次Ping測試結果 TimedOut"
                                );
                            db.Entry(device).State = EntityState.Modified;
                        }

                    PingDatas.Add(new PingData
                    {
                        Id = Guid.NewGuid(),
                        CreateTime = PingTime,
                        Device = device,
                        Delay1 = PingDelay[1],
                        Delay2 = PingDelay[2],
                        Delay3 = PingDelay[3],
                        Delay4 = PingDelay[4]
                    });
                }
            });

            try
            {
                db.PingDatas.AddRange(PingDatas);
                await db.SaveChangesAsync();

                LineBotMessage(string.Join("\n", StatusChangeDevices));
            }
            catch
            {

            }
        }

        private static bool LineBotMessage(string Message)
        {
            try
            {
                isRock.LineBot.Utility.PushMessage(AppConfig.LineGroupToken, Message, AppConfig.LineToken);

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void StartSpeedTest()
        {

        }
    }
}