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

        private readonly static string ConfigFile 
            = AppDomain.CurrentDomain.BaseDirectory + "Config.json";

        public static void LoadConfig()
        {
            try
            {
                string ConfigContent = File.ReadAllText(ConfigFile);
                dynamic JsonObject = JsonConvert.DeserializeObject(ConfigContent);

                LineToken = JsonObject["LineToken"];
                LineRetornMessage = JsonObject["LineRetornMessage"];
                LineGroupToken = JsonObject["LineGroupToken"];
            }
            catch(Exception ex)
            {
                LogGenerator.Add("Failed to load config: " + ex.Message);
                LineToken = "";
                LineRetornMessage = "";
                LineGroupToken = "";
                SaveConfig();
            }
        }

        public static void SaveConfig()
        {
            File.WriteAllText(ConfigFile, JsonConvert.SerializeObject(
                    new
                    {
                        LineToken,
                        LineRetornMessage,
                        LineGroupToken
                    }));
        }
    }
}