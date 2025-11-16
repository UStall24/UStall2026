using Newtonsoft.Json;
using System;
using System.IO;

namespace UStallGUI.Model
{
    public class ConfigLoader
    {
        public static ConfigGUI CurrentConfig { get; set; }
        public static readonly string currentConfigGuiFilePath = "configGUI.json";

        public static DirectionValues LoadControllerParameters(string path) => LoadOrCreateConfig<DirectionValues>(path);

        public static bool UpdateConfigurationFile(DirectionValues newConfig, string path) => UpdateConfigurationFile<DirectionValues>(newConfig, path);

        public static ConfigGUI LoadConfigGUI() => LoadOrCreateConfig<ConfigGUI>(currentConfigGuiFilePath);

        public static bool UpdateConfigGUI(ConfigGUI newConfig) => UpdateConfigurationFile<ConfigGUI>(newConfig, currentConfigGuiFilePath);
        public static bool UpdateConfigGUI() => UpdateConfigurationFile<ConfigGUI>(CurrentConfig, currentConfigGuiFilePath);

        public static T LoadOrCreateConfig<T>(string path) where T : class, new()
        {
            // Ensure the config file exists, creating it with default values if necessary
            if (!File.Exists(path))
            {
                var defaultConfig = new T(); // Create a new instance of T using the parameterless constructor
                string json = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
                File.WriteAllText(path, json);
            }

            // Load the configuration from the file
            T loadedValues = null;
            try
            {
                var json = File.ReadAllText(path);
                loadedValues = JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception ex)
            {
                // Optionally log the exception or handle it as needed
                Console.WriteLine($"Error loading config: {ex.Message}");
            }

            return loadedValues;
        }

        public static bool UpdateConfigurationFile<T>(T newConfig, string path) where T : class
        {
            bool successful = false;
            try
            {
                string json = JsonConvert.SerializeObject(newConfig, Formatting.Indented);
                File.WriteAllText(path, json);
                successful = true;
            }
            catch (Exception ex)
            {
                // Optionally log the exception or handle it as needed
                Console.WriteLine($"Error updating config file: {ex.Message}");
            }
            return successful;
        }
    }
}