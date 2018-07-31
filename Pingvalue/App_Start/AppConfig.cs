using Newtonsoft.Json;
using System;
using System.IO;

namespace Pingvalue
{
    public class AppConfig
    {
        public static string LineToken { get; set; }
        public static string LineRetornMessage { get; set; }
        public static string LineGroupToken { get; set; }

        private readonly static string ConfigPath 
            = AppDomain.CurrentDomain.BaseDirectory + "Config.json";

        public static void LoadConfig()
        {
            try
            {
                string ConfigContent = File.ReadAllText(ConfigPath);
                dynamic JsonObject = JsonConvert.DeserializeObject(ConfigContent);

                LineToken = JsonObject["LineToken"];
                LineRetornMessage = JsonObject["LineRetornMessage"];
                LineGroupToken = JsonObject["LineGroupToken"];
            }
            catch
            {
                LineToken = "";
                LineRetornMessage = "";
                LineGroupToken = "";
                SaveConfig();
            }
        }

        public static void SaveConfig()
        {
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(
                    new
                    {
                        LineToken,
                        LineRetornMessage,
                        LineGroupToken
                    }));
        }
    }
}