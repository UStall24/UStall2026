using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Input;
using UStallGUI.Helpers;
using UStallGUI.Model;
using Timer = System.Timers.Timer;

namespace UStallGUI.ViewModel
{
    public class SetupHandlerViewModel : ObservableObject
    {
        #region LCE Setup

        private byte _updateInterval;
        private int _controlType = 0;

        private ConfigGUI currentConfig;

        private UsbCANWrapper can;

        public byte UpdateInterval // in ms
        {
            get => _updateInterval;
            set => Set(ref _updateInterval, value);
        }

        /// <summary>
        /// 1 = Manual control, 2 = Controller, others = off
        /// </summary>
        public int ControlType
        {
            get => _controlType;
            set
            {
                if (!Set(ref _controlType, value))
                    return;

                switch (_controlType)
                {
                    case 1:
                        ControllerHandlerViewModel.SendControllerValues = false;
                        InitManualControl();
                        break;

                    case 2:
                        ControllerHandlerViewModel.SendControllerValues = true;
                        StopManualControl();
                        break;

                    default:
                        ControllerHandlerViewModel.SendControllerValues = false;
                        StopManualControl();
                        break;
                }
            }
        }

        public RelayCommand ConnectToLCE_Command { get; }
        public RelayCommand InitThrusters_Command { get; }
        public RelayCommand DeinitThrusters_Command { get; }
        public RelayCommand ApplyUpdateInterval_Command { get; }
        public RelayCommand ResetErrorLCE_Command { get; }
        public RelayCommand HealthCommand { get; }

        #endregion LCE Setup

        #region Manual Control

        private Timer _manualControlTimer;
        private MotorValues _manualControlValues;

        public MotorValues ManualControlValues
        {
            get => _manualControlValues;
            set => Set(ref _manualControlValues, value);
        }

        private float _manualControlAllVertical;

        public float ManualControlAllVertical
        {
            get => _manualControlAllVertical;
            set
            {
                var clamped = Math.Clamp(value, -1f, 1f);
                if (!Set(ref _manualControlAllVertical, clamped))
                    return;

                if (ManualControlValues != null)
                {
                    ManualControlValues.VM1 = clamped;
                    ManualControlValues.VM2 = clamped;
                    ManualControlValues.VM3 = clamped;
                }
            }
        }

        private float _manualControlAllHorizontal;

        public float ManualControlAllHorizontal
        {
            get => _manualControlAllHorizontal;
            set
            {
                var clamped = Math.Clamp(value, -1f, 1f);
                if (!Set(ref _manualControlAllHorizontal, clamped))
                    return;

                if (ManualControlValues != null)
                {
                    ManualControlValues.HM1 = clamped;
                    ManualControlValues.HM2 = clamped;
                    ManualControlValues.HM3 = clamped;
                }
            }
        }

        private float _manualControlAll;

        public float ManualControlAll
        {
            get => _manualControlAll;
            set
            {
                var clamped = Math.Clamp(value, -1f, 1f);
                if (!Set(ref _manualControlAll, clamped))
                    return;

                ManualControlAllVertical = clamped;
                ManualControlAllHorizontal = clamped;
            }
        }

        public RelayCommand ManualControlReset_Command { get; }

        #endregion Manual Control

        #region Gyro

        private bool _gyroEnabled;
        private float _gyroPValue;
        private float _gyroDValue;

        public bool GyroEnabled
        {
            get => _gyroEnabled;
            set
            {
                if (Set(ref _gyroEnabled, value))
                {
                    currentConfig.GyroEnabled = value;
                    UpdateConfig();
                }
            }
        }

        public float GyroPValue
        {
            get => _gyroPValue;
            set => Set(ref _gyroPValue, Clamp255Float(value));
        }

        public float GyroDValue
        {
            get => _gyroDValue;
            set => Set(ref _gyroDValue, Clamp255Float(value));
        }

        // Method to clamp the value between 0 and 2.55
        private static float Clamp255Float(float value)
        {
            if (value < 0f) return 0f;
            if (value > 2.55f) return 2.55f;
            return value;
        }

        public RelayCommand PullGyroValues_Command { get; }
        public RelayCommand ApplyGyroValues_Command { get; }

        #endregion Gyro

        #region Options

        private bool _dynamicThrottleEnabled;
        private byte _maxCurrent;

        public bool DynamicThrottleEnabled
        {
            get => currentConfig.DynamicThrottleEnabled;
            set
            {
                if (!Set(ref _dynamicThrottleEnabled, value))
                    return;

                currentConfig.DynamicThrottleEnabled = value;
                ControllerHandlerViewModel.UseDynamicThrottle = value;
                UpdateConfig();
            }
        }

        public byte MaxCurrent
        {
            get => _maxCurrent;
            set => Set(ref _maxCurrent, value);
        }

        #endregion Options

        public SetupHandlerViewModel()
        {
            can = new UsbCANWrapper();

            ManualControlValues = new MotorValues([0, 0, 0, 0, 0, 0]);
            currentConfig = ConfigLoader.LoadConfigGUI();

            ControlType = 2; // default to controller

            //InitThrusters_Command = new RelayCommand(() => _ = InitThrustersAsync());
            //DeinitThrusters_Command = new RelayCommand(() => _ = DeinitThrustersAsync());
            //ApplyUpdateInterval_Command = new RelayCommand(() => _ = ApplyUpdateIntervalAsync());
            //ResetErrorLCE_Command = new RelayCommand(() => _ = ResetErrorLCEAsync());
            //PullGyroValues_Command = new RelayCommand(() => _ = PullGyroValuesAsync());
            //ApplyGyroValues_Command = new RelayCommand(() => _ = ApplyGyroValuesAsync());
            ManualControlReset_Command = new RelayCommand(ManualControlReset);

            ConnectToLCE_Command = new RelayCommand(ConnectToControlUnit);
            HealthCommand = new RelayCommand(SendHealthMessage);
        }

        async void SendHealthMessage()
        {

            MainWindowViewModel.Instance.LogToControlBox("Checking Health...");
            bool ok = await can.TestConnectionAsync(100);
            if (ok)
                MainWindowViewModel.Instance.LogToControlBox("Health Update ok");
            else MainWindowViewModel.Instance.LogToControlBox("Health Update not ok");
        }

        #region Config helpers

        private void UpdateConfig() => ConfigLoader.UpdateConfigGUI(currentConfig);

        #endregion Config helpers

        #region Connection / CAN

        async void ConnectToControlUnit()
        {
            can.ConnectToCanAdapter();
            MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.Connecting);
            can.SendFrame((ushort)SendAddressesControlUnit.InitThrusters);
            bool ok = await can.TestConnectionAsync(); 
            if (ok) MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.Connected);
            else MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.ErrorConnecting);
                //byte[] response = await WaitForResponseAsync(
                //    (ushort)RecieveAddressesControlUnit.InitThrusters_Response,
                //    300
                //).ConfigureAwait(false);

                //if (response.Length != 0)
                //{
                //    MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.Connected);

                //    // Display first status byte as human-readable message, if mapping exists
                //    if (response.Length > 0 &&
                //        LCECommunicationHelper.LCE_MessageAddresses.TryGetValue(response[0], out var msg))
                //    {
                //        MainWindowViewModel.Instance.LogToControlBox(msg);
                //    }

                //    UpdateInterval = await PullUpdateInterval().ConfigureAwait(false);
                //}
                //else
                //{
                //    MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.ErrorConnecting);
                //}
            }

        public void DisconnectFromLCE()
        {
            StopManualControl();

            if (can != null && can.IsOpen)
            {
                bool closingSuccessful = can.Close();
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(
                    closingSuccessful ? LceConnectionStatus.ClosingSuccessful : LceConnectionStatus.ClosingFailed
                );
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("CAN interface is currently closed");
            }

            can = null;
        }

        public UsbCANWrapper GetCANWrapper()
        {
            return can;
        }

        #endregion Connection / CAN

        #region Thruster Init/Deinit (stubs – fill in your protocol)

        //private async Task InitThrustersAsync()
        //{
        //    if (can == null || !can.IsOpen)
        //        return;

        //    // Example pattern – change IDs to your real protocol
        //    can.SendFrame((ushort)SendAddressesControlUnit.InitThrusters);

        //    var response = await WaitForResponseAsync(
        //        (ushort)RecieveAddressesControlUnit.InitThrusters_Response,
        //        200
        //    ).ConfigureAwait(false);

        //    if (response.Length > 0)
        //    {
        //        MainWindowViewModel.Instance.LogToControlBox("Thrusters init successful.");
        //    }
        //    else
        //    {
        //        MainWindowViewModel.Instance.LogToControlBox("Thrusters init failed.");
        //    }
        //}

        //private async Task DeinitThrustersAsync()
        //{
        //    if (can == null || !can.IsOpen)
        //        return;

        //    // Example pattern – adjust IDs
        //    can.SendFrame((ushort)SendAddressesControlUnit.DeinitThrusters);

        //    var response = await WaitForResponseAsync(
        //        (ushort)RecieveAddressesControlUnit.DeinitThrusters_Response,
        //        200
        //    ).ConfigureAwait(false);

        //    if (response.Length > 0)
        //    {
        //        MainWindowViewModel.Instance.LogToControlBox("Thrusters deinit successful.");
        //    }
        //    else
        //    {
        //        MainWindowViewModel.Instance.LogToControlBox("Thrusters deinit failed.");
        //    }
        //}

        #endregion Thruster Init/Deinit (stubs – fill in your protocol)

        #region Manual Control Timer

        private void InitManualControl()
        {
            StopManualControl();

            if (UpdateInterval == 0)
                UpdateInterval = 10; // fallback

            _manualControlTimer = new Timer(UpdateInterval * 2); // interval in ms
            _manualControlTimer.Elapsed += ApplyManualControl;
            _manualControlTimer.AutoReset = true;
            _manualControlTimer.Enabled = true;
        }

        private void StopManualControl()
        {
            if (_manualControlTimer != null)
            {
                _manualControlTimer.Elapsed -= ApplyManualControl;
                _manualControlTimer.Stop();
                _manualControlTimer.Dispose();
                _manualControlTimer = null;
            }
        }

        private void ApplyManualControl(object sender, ElapsedEventArgs e)
        {
            if (can != null && can.IsOpen && ManualControlValues != null)
            {
                can.SendFrame((ushort)SendAddressesControlUnit.ApplyManualControl, ManualControlValues.GetAsByte());
            }
        }

        private void ManualControlReset()
        {
            ManualControlValues?.UpdateAsArray([0, 0, 0, 0, 0, 0]);
            ManualControlAll = 0f;
        }


        private void Health(object commandParameter)
        {
        }
        #endregion Manual Control Timer

        #region Update Interval

        //private async Task ApplyUpdateIntervalAsync()
        //{
        //    if (can == null || !can.IsOpen)
        //        return;

        //    byte oldCycleInterval = await PullUpdateInterval().ConfigureAwait(false);
        //    if (oldCycleInterval == 0)
        //        return;

        //    can.SendFrame((ushort)SendAddressesControlUnit.UpdateCylceTime, new byte[] { UpdateInterval });

        //    byte[] resp = await WaitForResponseAsync(
        //        (ushort)RecieveAddressesControlUnit.UpdateCycleTime_Response,
        //        100
        //    ).ConfigureAwait(false);

        //    if (resp.Length > 0)
        //    {
        //        string result = oldCycleInterval == resp[0] ? "Failed!" : "was Successful!";
        //        MainWindowViewModel.Instance.LogToControlBox($"Updating Cycle Time from {oldCycleInterval} to {resp[0]} {result}");
        //    }
        //}

        //private async Task<byte> PullUpdateInterval()
        //{
        //    if (can == null || !can.IsOpen)
        //        return 0x00;

        //    can.SendFrame((ushort)SendAddressesControlUnit.UpdateCylceTime, new byte[] { 0x00 });

        //    byte[] response = await WaitForResponseAsync(
        //        (ushort)RecieveAddressesControlUnit.UpdateCycleTime_Response,
        //        100
        //    ).ConfigureAwait(false);

        //    return response.Length > 0 ? response[0] : (byte)0x00;
        //}

        #endregion Update Interval

        #region Error Reset

        //private async Task ResetErrorLCEAsync()
        //{
        //    if (can == null || !can.IsOpen)
        //        return;

        //    can.SendFrame((ushort)SendAddressesControlUnit.ResetError);

        //    byte[] response = await WaitForResponseAsync(
        //        (ushort)RecieveAddressesControlUnit.ResetError_Response,
        //        100
        //    ).ConfigureAwait(false);

        //    if (response.Length != 0 && response[0] == 0x01)
        //    {
        //        MainWindowViewModel.Instance.LogToControlBox("Resetting Error was successful");
        //    }
        //    else
        //    {
        //        MainWindowViewModel.Instance.LogToControlBox("Resetting Error failed");
        //    }
        //}

        #endregion Error Reset

        #region Gyro Commands

        //private async Task PullGyroValuesAsync()
        //{
        //    if (can == null || !can.IsOpen)
        //        return;

        //    can.SendFrame((ushort)SendAddressesControlUnit.PullPidValues);

        //    byte[] response = await WaitForResponseAsync(
        //        (ushort)RecieveAddressesControlUnit.PullPidValues_Response,
        //        100
        //    ).ConfigureAwait(false);

        //    if (response.Length > 0)
        //    {
        //        if (response.Length >= 2)
        //        {
        //            GyroPValue = LCECommunicationHelper.ConvertByteTo255Float(response[0]);
        //            GyroDValue = LCECommunicationHelper.ConvertByteTo255Float(response[1]);
        //        }

        //        if (response.Length >= 3)
        //        {
        //            GyroEnabled = response[2] == 1;
        //        }

        //        MainWindowViewModel.Instance.LogToControlBox("Pulling PID values was Successful!");
        //    }
        //    else
        //    {
        //        MainWindowViewModel.Instance.LogToControlBox("Pulling PID values failed!");
        //    }
        //}

        //private async Task ApplyGyroValuesAsync()
        //{
        //    if (can == null || !can.IsOpen)
        //        return;

        //    byte[] payload =
        //    {
        //        LCECommunicationHelper.Convert255FloatToByte(GyroPValue),
        //        LCECommunicationHelper.Convert255FloatToByte(GyroDValue),
        //        (byte)(GyroEnabled ? 0x01 : 0x00)
        //    };

        //    can.SendFrame((ushort)SendAddressesControlUnit.ApplyPidValues, payload);

        //    byte[] response = await WaitForResponseAsync(
        //        (ushort)RecieveAddressesControlUnit.ApplyPidValues_Response,
        //        100
        //    ).ConfigureAwait(false);

        //    MainWindowViewModel.Instance.LogToControlBox(
        //        response.Length > 0
        //            ? "Applying PID values was Successful!"
        //            : "Applying PID values failed!");
        //}

        #endregion Gyro Commands
    }
}