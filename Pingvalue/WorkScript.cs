using NSpeedTest;
using NSpeedTest.Models;
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

        private static SpeedTestClient client;
        private static Settings settings;
        private const string DefaultCountry = "Taiwan";
        private static List<string> ServerList = new List<string>() { "SEEDNET", "NCIC Telecom", "Far Eastone Telecommunications Co., Ltd.", "FarEasTone Telecom" };

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
            DateTime PingTime = Convert.ToDateTime(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
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

        public static async Task StartSpeedTestAsync()
        {
            DateTime SpeedTestTime = Convert.ToDateTime(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

            client = new SpeedTestClient();
            settings = client.GetSettings();


            var servers = SelectServers();
            var bestServer = SelectBestServer(servers);

            SpeedTest SpeetTestData = new SpeedTest();
            try
            {
                SpeetTestData.ISP = settings.Client.Isp;
                SpeetTestData.ClientLatitude = settings.Client.Latitude;
                SpeetTestData.ClientLongitude = settings.Client.Longitude;
                SpeetTestData.Server = bestServer.Host;
                SpeetTestData.ServerLatitude = bestServer.Latitude;
                SpeetTestData.ServerLongitude = bestServer.Longitude;
                SpeetTestData.DelayTime = bestServer.Latency;
                var downloadSpeed = client.TestDownloadSpeed(bestServer, settings.Download.ThreadsPerUrl);
                SpeetTestData.SpeedDownload = PrintSpeed(downloadSpeed);
                var uploadSpeed = client.TestUploadSpeed(bestServer, settings.Upload.ThreadsPerUrl);
                SpeetTestData.SpeetUpload = PrintSpeed(uploadSpeed);
            }
            catch
            {
                SpeetTestData.ClientLatitude = 0;
                SpeetTestData.ClientLongitude = 0;
                SpeetTestData.ISP = "Failed";
                SpeetTestData.DelayTime = -1;
                SpeetTestData.Server = "Failed";
                SpeetTestData.ServerLatitude = 0;
                SpeetTestData.ServerLongitude = 0;
                SpeetTestData.SpeedDownload = "Failed";
                SpeetTestData.SpeetUpload = "Failed";
            }

            SpeetTestData.Id = Guid.NewGuid();
            SpeetTestData.TestTime = SpeedTestTime;

            try
            {
                db.SpeedTests.Add(SpeetTestData);
                await db.SaveChangesAsync();
            }
            catch
            {

            }
        }

        private static Server SelectBestServer(IEnumerable<Server> servers)
        {
            var bestServer = servers.OrderBy(x => x.Latency).First();
            return bestServer;
        }

        private static IEnumerable<Server> SelectServers()
        {
            var servers = settings.Servers.Where(c => ServerList.Contains(c.Sponsor)).OrderBy(x => x.Sponsor).ToList();

            List<Server> ErrorServer = new List<Server>();
            foreach (var server in servers)
            {
                try
                {
                    server.Latency = client.TestServerLatency(server);
                }
                catch
                {
                    ErrorServer.Add(server);
                }
            }
            servers.RemoveAll(c => ErrorServer.Contains(c));
            return servers;
        }

        private static string PrintSpeed(double speed)
        {
            if (speed > 1024)
                if (speed / 1024 < 1024)
                    return string.Format("{0} Mbps", Math.Round(speed / 1024, 2));
                else
                    return "Failed";
            else
                return string.Format("{0} Kbps", Math.Round(speed, 2));
        }
    }
}