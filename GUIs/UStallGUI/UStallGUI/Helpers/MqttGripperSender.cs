using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using UStallGUI.Model;

namespace UStallGUI.Helpers
{
    public class MqttGripperSender
    {
        private readonly IMqttClient _mqttClient;
        private readonly MqttClientOptions _mqttOptions;
        private readonly GripperModel _gripperModel;
        private readonly string _simple_gripper_topic = "greifer/values";
        private readonly string _mechpro_gripper_topic = "gamepad/input";
        public bool IsConnected { get => _mqttClient?.IsConnected == true; }

        public MqttGripperSender(IMqttClient mqttClient, GripperModel gripperModel)
        {
            _mqttClient = mqttClient;
            _gripperModel = gripperModel;
        }

        public async Task SendMechProGripperValues(string payload)
        {
            if (!_mqttClient.IsConnected)
                return;

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(_mechpro_gripper_topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(message);
        }

        public async Task SendSimpleGripperValues()
        {
            if (!_mqttClient.IsConnected)
                return;

            byte[] gripperBytes = _gripperModel.GetGripperBytes();

            var payload = new
            {
                Timestamp = DateTime.UtcNow,
                GripperValues = gripperBytes
            };

            string jsonPayload = JsonSerializer.Serialize(payload);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(_simple_gripper_topic)
                .WithPayload(jsonPayload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(message);
        }
    }
}