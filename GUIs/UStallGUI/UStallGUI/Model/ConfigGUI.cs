namespace UStallGUI.Model
{
    public class ConfigGUI
    {
        public int ComPort { get; set; } = 15;
        public bool GyroEnabled { get; set; } = true;

        public int MqttPort { get; set; } = 5000;
        public string MqttIpAddr { get; set; } = "192.168.0.3";

        public int ControlStyle { get; set; } = 0;

        public bool DynamicThrottleEnabled { get; set; } = false;
    }
}