using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;

namespace Pingvalue
{
    public class AppConfig
    {
        public static string LineToken { get; private set; }
        public static string LineRetornMessage { get; private set; }

        public static void LoadConfig()
        {
            string ConfigPath = AppDomain.CurrentDomain.BaseDirectory + "Config.json";
            string ConfigContent = File.ReadAllText(ConfigPath);
            dynamic JsonObject = JsonConvert.DeserializeObject(ConfigContent);

            LineToken = JsonObject["LineToken"];
            LineRetornMessage = JsonObject["LineRetornMessage"];
        }
    }
}