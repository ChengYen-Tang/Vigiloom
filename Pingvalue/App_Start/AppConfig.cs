using Newtonsoft.Json;
using System;
using System.IO;

namespace Pingvalue
{
    public class AppConfig
    {
        public static string LineToken { get; private set; }
        public static string LineRetornMessage { get; private set; }
        public static string LineGroupToken { get; private set; }

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