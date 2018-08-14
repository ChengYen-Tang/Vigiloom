using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.IO;

namespace Pingvalue
{
    public class LogGenerator
    {
        private readonly static string LogPath
            = AppDomain.CurrentDomain.BaseDirectory + @"Log\";
        private static object FileLock = new object();

        public static void Add(string Message)
        {
            //今日日期
            DateTime Date = DateTime.Now;
            string TodyMillisecond = Date.ToString("yyyy-MM-dd HH:mm:ss");
            string Tody = Date.ToString("yyyy-MM-dd");

            //如果此路徑沒有資料夾
            if (!Directory.Exists(LogPath))
            {
                //新增資料夾
                Directory.CreateDirectory(LogPath);
            }

            //把內容寫到目的檔案，若檔案存在則附加在原本內容之後(換行)
            lock (FileLock)
                File.AppendAllText(LogPath + Tody + ".txt", TodyMillisecond + "：" + Message + "\r\n");
        }
    }
}