using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using System;
using System.Text;
using System.Threading.Tasks;
using UStallGUI.Helpers;
using UStallGUI.Model;
using Timer = System.Threading.Timer;

namespace UStallGUI.ViewModel
{
    public class AccessoryBoxViewModel : ObservableObject
    {
        private bool _debug_mode = false;

        private IMqttClient mqttClient;
        private MqttClientOptions mqttClientOptions;
        public GripperModel GripperModel { get; set; } = new();

        private int _mqtt_port;

        public int MqttPort
        {
            get
            {
                _mqtt_port = ConfigLoader.CurrentConfig.MqttPort;
                return _mqtt_port;
            }
            set
            {
                Set(ref _mqtt_port, value);
                ConfigLoader.CurrentConfig.MqttPort = _mqtt_port;
                ConfigLoader.UpdateConfigGUI();
            }
        }

        private string _mqtt_ip_addr;

        public string MqttIpAddr
        {
            get
            {
                _mqtt_ip_addr = ConfigLoader.CurrentConfig.MqttIpAddr;
                return _mqtt_ip_addr;
            }
            set
            {
                Set(ref _mqtt_ip_addr, value);
                ConfigLoader.CurrentConfig.MqttIpAddr = _mqtt_ip_addr;
                ConfigLoader.UpdateConfigGUI();
            }
        }

        private MqttGripperSender _mqttSender;
        private readonly UsbCANWrapper can;

        public AccessoryBoxViewModel(UsbCANWrapper can)
        {
            GripperCommand = new RelayCommand<GripperAssignment>(ExecuteGripperCommand);
            ConnectMqtt_Command = new RelayCommand(ConnectMqtt);
            this.can = can;
        }

        public RelayCommand<GripperAssignment> GripperCommand { get; set; }

        public void ExecuteGripperCommand(GripperAssignment assignment) => ExecuteGripperCommand(assignment, StepSize);

        private void ExecuteGripperCommand(GripperAssignment assignment, int step)
        {
            if ((_mqttSender != null && _mqttSender.IsConnected) || _debug_mode)
            {
                bool mechpro_gripper_execution = false;
                switch (assignment)
                {
                    case GripperAssignment.Gripper1_Servo1Plus:
                        GripperModel.G1S1.CurrentValue += step;
                        break;

                    case GripperAssignment.Gripper1_Servo1Minus:
                        GripperModel.G1S1.CurrentValue -= step;
                        break;

                    case GripperAssignment.Gripper1_Servo2Plus:
                        GripperModel.G1S2.CurrentValue += step;
                        break;

                    case GripperAssignment.Gripper1_Servo2Minus:
                        GripperModel.G1S2.CurrentValue -= step;
                        break;

                    case GripperAssignment.Gripper1_Servo3Plus:
                        GripperModel.G1S3.CurrentValue += step;
                        break;

                    case GripperAssignment.Gripper1_Servo3Minus:
                        GripperModel.G1S3.CurrentValue -= step;
                        break;

                    default:
                        if (!_debug_mode) _mqttSender.SendMechProGripperValues(GripperModel.MechproGripperExecuteMessage(assignment));
                        mechpro_gripper_execution = true;
                        break;
                }
                if (!mechpro_gripper_execution && !_debug_mode) _ = _mqttSender.SendSimpleGripperValues();
                if (_debug_mode) Console.WriteLine(GripperModel);
            }
            else MainWindowViewModel.Instance.LogToControlBox("Connect to Accessory Box first");
        }

        public RelayCommand ConnectMqtt_Command { get; set; }

        private async void ConnectMqtt()
        {
            if (ControllerHandlerViewModel.Instance.CurrentControllerModel != null)
            {
                MainWindowViewModel.Instance.LogToControlBox("Trying to connect");
                bool connected = await Connect2MqttServer();

                if (connected)
                {
                    _mqttSender = new MqttGripperSender(
                    mqttClient: this.mqttClient,
                    gripperModel: GripperModel
                    );

                    MainWindowViewModel.Instance.LogToControlBox("Connected to Accessory Box RPi");
                    AssignControllerToGripperAction();
                }
                else MainWindowViewModel.Instance.LogToControlBox("Connection to Accessory Box failed");
            }
            else MainWindowViewModel.Instance.LogToControlBox("Connect Controller First");
        }

        private int _selectedGripper = 0;

        public int SelectedGripper
        {
            get => _selectedGripper;
            set
            {
                Set(ref _selectedGripper, value);
                MainWindowViewModel.Instance.SelectedGripper = value.ToString();
            }
        }

        private void AssignControllerToGripperAction()
        {
            ControllerHandlerViewModel.Instance.CurrentControllerModel.ButtonAPressed += () =>
            {
                SelectedGripper += 1;
                if (SelectedGripper > 3) SelectedGripper = 0;
            };

            // Control Style 1
            ControllerHandlerViewModel.Instance.CurrentControllerModel.DPadUpPressed += () =>
            {
                if (ControllerHandlerViewModel.Instance.CurrentControllerModel.ControlStyle == 0)
                {
                    if (SelectedGripper == 1) ExecuteGripperCommand(GripperAssignment.Gripper1_Servo1Plus);
                    if (SelectedGripper == 3) ExecuteGripperCommand(GripperAssignment.Gripper2_Servo1Plus);
                }
            };
            ControllerHandlerViewModel.Instance.CurrentControllerModel.DPadDownPressed += () =>
            {
                if (ControllerHandlerViewModel.Instance.CurrentControllerModel.ControlStyle == 0)
                {
                    if (SelectedGripper == 1) ExecuteGripperCommand(GripperAssignment.Gripper1_Servo1Minus);
                    if (SelectedGripper == 3) ExecuteGripperCommand(GripperAssignment.Gripper2_Servo1Minus);
                }
            };
            ControllerHandlerViewModel.Instance.CurrentControllerModel.DPadRightPressed += () =>
            {
                if (ControllerHandlerViewModel.Instance.CurrentControllerModel.ControlStyle == 0)
                {
                    if (SelectedGripper == 1) ExecuteGripperCommand(GripperAssignment.Gripper1_Servo2Plus);
                    if (SelectedGripper == 3) ExecuteGripperCommand(GripperAssignment.Gripper2_Servo2Plus);
                }
            };
            ControllerHandlerViewModel.Instance.CurrentControllerModel.DPadLeftPressed += () =>
            {
                if (ControllerHandlerViewModel.Instance.CurrentControllerModel.ControlStyle == 0)
                {
                    if (SelectedGripper == 1) ExecuteGripperCommand(GripperAssignment.Gripper1_Servo2Minus);
                    if (SelectedGripper == 3) ExecuteGripperCommand(GripperAssignment.Gripper2_Servo2Minus);
                }
            };
            ControllerHandlerViewModel.Instance.CurrentControllerModel.ButtonXPressed += () =>
            {
                if (ControllerHandlerViewModel.Instance.CurrentControllerModel.ControlStyle == 0)
                {
                    if (SelectedGripper == 1) ExecuteGripperCommand(GripperAssignment.Gripper1_Servo3Plus);
                }
                if (SelectedGripper == 3) ExecuteGripperCommand(GripperAssignment.Gripper2_PumpOn); // Both Control styles
            };
            ControllerHandlerViewModel.Instance.CurrentControllerModel.ButtonYPressed += () =>
            {
                if (ControllerHandlerViewModel.Instance.CurrentControllerModel.ControlStyle == 0)
                {
                    if (SelectedGripper == 1) ExecuteGripperCommand(GripperAssignment.Gripper1_Servo3Minus);
                }
                if (SelectedGripper == 3) ExecuteGripperCommand(GripperAssignment.Gripper2_DefaultPosition); // Both control styles
            };

            // Control Style 2
            _controlStyle2Timer = new Timer(ControlStyle2Execution, null, 0, updateInterval);
        }

        private Timer _controlStyle2Timer;
        private int updateInterval = 100; // 100 ms

        private void ControlStyle2Execution(object state)
        {
            if (ControllerHandlerViewModel.Instance.CurrentControllerModel.ControlStyle == 1)
            {
                int stepValue = (int)Math.Round(((ControllerHandlerViewModel.Instance.CurrentControllerModel.RightJoystickY - 127.5f) / 127.5f) * 5);

                //Console.WriteLine($"Right Joystick: {(ControllerHandlerViewModel.Instance.CurrentControllerModel.RightJoystickY - 127.5f) / 127.5f}. StepValue: {stepValue}");
                if (SelectedGripper == 1) ExecuteGripperCommand(GripperAssignment.Gripper1_Servo1Plus, stepValue);
                if (SelectedGripper == 2) ExecuteGripperCommand(GripperAssignment.Gripper1_Servo3Plus, stepValue);
                if (SelectedGripper == 3) ExecuteGripperCommand(GripperAssignment.Gripper2_Servo1Plus, stepValue);

                stepValue = (int)Math.Round(((ControllerHandlerViewModel.Instance.CurrentControllerModel.RightJoystickX - 127.5f) / 127.5f) * 5);
                if (SelectedGripper == 1 || SelectedGripper == 2) ExecuteGripperCommand(GripperAssignment.Gripper1_Servo2Plus, stepValue);
                if (SelectedGripper == 3) ExecuteGripperCommand(GripperAssignment.Gripper2_Servo2Plus, stepValue);
            }
        }

        public async Task<bool> Connect2MqttServer()
        {
            var factory = new MqttFactory();
            mqttClient = factory.CreateMqttClient();

            mqttClientOptions = new MqttClientOptionsBuilder()
                .WithClientId("GripperSenderClient")
                .WithTcpServer(MqttIpAddr, MqttPort)
                .WithCleanSession()
                .Build();

            mqttClient.ApplicationMessageReceivedAsync += HandleReceivedMessage;

            try
            {
                if (!mqttClient.IsConnected)
                {
                    await mqttClient.ConnectAsync(mqttClientOptions);
                }

                if (mqttClient.IsConnected)
                {
                    await SubscribeToTopics();
                }

                return mqttClient.IsConnected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task SubscribeToTopics()
        {
            await mqttClient.SubscribeAsync("temperature/rpi");
        }

        private Task HandleReceivedMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            string topic = e.ApplicationMessage.Topic;
            string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

            if (topic == "temperature/rpi")
            {
                MainWindowViewModel.Instance.RPiTemperature = payload;
            }

            return Task.CompletedTask;
        }

        #region Simple Gripper (G1)

        private int _stepSize = 5;
        public int StepSize { get => _stepSize; set => Set(ref _stepSize, value); }

        #endregion Simple Gripper (G1)
    }

    public enum GripperAssignment
    {
        Gripper1_Servo1Plus,
        Gripper1_Servo1Minus,
        Gripper1_Servo2Plus,
        Gripper1_Servo2Minus,
        Gripper1_Servo3Plus,
        Gripper1_Servo3Minus,
        Gripper2_Servo1Plus,
        Gripper2_Servo1Minus,
        Gripper2_Servo2Plus,
        Gripper2_Servo2Minus,
        Gripper2_PumpOn,
        Gripper2_DefaultPosition
    }
}