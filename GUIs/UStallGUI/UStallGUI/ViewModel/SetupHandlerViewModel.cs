using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using System.Threading;
using System.Threading.Tasks;
using UStallGUI.Helpers;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class SetupHandlerViewModel : ObservableObject
    {
        public static SetupHandlerViewModel Instance { get; private set; }

        #region Pixhawk Setup

        private byte _updateInterval = 10;
        private int _controlType = 1;
        private string _comPort = "Auto";
        private bool _isArmed = false;
        private string _activeArduSubModeText = string.Empty;

        private ConfigGUI currentConfig;

        private UsbCANWrapper can;

        public string ComPort
        {
            get => _comPort;
            set => Set(ref _comPort, value);
        }

        public bool IsArmed
        {
            get => _isArmed;
            private set
            {
                if (Set(ref _isArmed, value))
                {
                    RaisePropertyChanged(nameof(ArmedStatusText));
                    QuickSetupViewModel.Instance?.NotifyArmedStateChanged();
                }
            }
        }

        public string ArmedStatusText => IsArmed
            ? (string.IsNullOrEmpty(_activeArduSubModeText) ? "ARMED" : $"ARMED ({_activeArduSubModeText})")
            : "DISARMED";

        private void OnPixhawkTelemetryReceived(bool isArmed, byte arduSubMode)
        {
            string modeText = arduSubMode switch
            {
                0 => "STABILIZE",
                2 => "DEPTH_HOLD",
                19 => "MANUAL",
                _ => $"MODE {arduSubMode}",
            };

            bool modeChanged = _activeArduSubModeText != modeText;
            _activeArduSubModeText = modeText;

            bool armedChanged = _isArmed != isArmed;
            _isArmed = isArmed;

            if (armedChanged || modeChanged)
            {
                RaisePropertyChanged(nameof(IsArmed));
                RaisePropertyChanged(nameof(ArmedStatusText));
                QuickSetupViewModel.Instance?.NotifyArmedStateChanged();
            }
        }

        public byte UpdateInterval // in ms
        {
            get => _updateInterval;
            set => Set(ref _updateInterval, value);
        }

        /// <summary>
        /// 0 = Off, 1 = Controller
        /// </summary>
        public int ControlType
        {
            get => _controlType;
            set
            {
                if (!Set(ref _controlType, value))
                    return;

                if (ControllerHandlerViewModel.Instance != null)
                {
                    ControllerHandlerViewModel.Instance.SendControllerValues = (_controlType == 1);
                }
            }
        }

        public void NotifyControllerSendStateChanged(bool isEnabled)
        {
            int targetIndex = isEnabled ? 1 : 0;
            if (_controlType != targetIndex)
            {
                _controlType = targetIndex;
                RaisePropertyChanged(nameof(ControlType));
            }
        }

        public RelayCommand ConnectToPixhawk_Command { get; }
        public RelayCommand ArmThrusters_Command { get; }
        public RelayCommand DisarmThrusters_Command { get; }
        public RelayCommand ApplyUpdateInterval_Command { get; }
        public RelayCommand ResetWatchdog_Command { get; }
        public RelayCommand HealthCommand { get; }

        #endregion Pixhawk Setup

        #region Flight Mode Options

        private FlightMode _selectedFlightMode;

        public FlightMode SelectedFlightMode
        {
            get => _selectedFlightMode;
            set
            {
                if (!Set(ref _selectedFlightMode, value))
                    return;

                currentConfig.FlightMode = value;
                ControllerHandlerViewModel.CurrentFlightMode = value;
                UpdateConfig();
                RaisePropertyChanged(nameof(SelectedFlightModeIndex));
                QuickSetupViewModel.Instance?.NotifyFlightModeChanged();

                string modeMessage = value switch
                {
                    FlightMode.Manual => "Flight Mode set to MANUAL (Direct Control, No Attitude/Depth Hold)",
                    FlightMode.DepthHold => "Flight Mode set to DEPTH_HOLD (Automatic Depth + Attitude Hold)",
                    _ => "Flight Mode set to STABILIZE (Attitude Hold)",
                };
                MainWindowViewModel.Instance?.LogToControlBox(modeMessage);
                SendFlightModeFrame(value);
            }
        }

        public int SelectedFlightModeIndex
        {
            get => (int)SelectedFlightMode;
            set
            {
                if (Enum.IsDefined(typeof(FlightMode), value))
                    SelectedFlightMode = (FlightMode)value;
            }
        }

        private int _maxThrusterSpeedPercent = 100;
        private CancellationTokenSource _speedLimitDebounceCts;

        public int MaxThrusterSpeedPercent
        {
            get => _maxThrusterSpeedPercent;
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                if (!Set(ref _maxThrusterSpeedPercent, clamped))
                    return;

                currentConfig.MaxThrusterSpeedPercent = clamped;
                ControllerHandlerViewModel.MaxThrusterSpeedPercent = clamped;
                UpdateConfig();
                QuickSetupViewModel.Instance?.NotifyMaxThrusterSpeedChanged();
                _ = DebouncedSendSpeedLimitAsync(clamped);
            }
        }

        public void SyncSpeedLimitFromPixhawk(int percent)
        {
            int clamped = Math.Clamp(percent, 0, 100);
            if (_maxThrusterSpeedPercent == clamped)
                return;

            _maxThrusterSpeedPercent = clamped;
            currentConfig.MaxThrusterSpeedPercent = clamped;
            ControllerHandlerViewModel.MaxThrusterSpeedPercent = clamped;
            UpdateConfig();
            RaisePropertyChanged(nameof(MaxThrusterSpeedPercent));
            QuickSetupViewModel.Instance?.NotifyMaxThrusterSpeedChanged();
        }

        private async Task DebouncedSendSpeedLimitAsync(int percent)
        {
            _speedLimitDebounceCts?.Cancel();
            _speedLimitDebounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _speedLimitDebounceCts = cts;

            try
            {
                await Task.Delay(120, cts.Token);
                await SendSpeedLimitAsync(percent);
            }
            catch (TaskCanceledException)
            {
                // Superseded by a newer slider update
            }
        }

        #endregion Flight Mode Options

        public SetupHandlerViewModel()
        {
            Instance = this;
            can = new UsbCANWrapper();
            can.TelemetryReceived += OnPixhawkTelemetryReceived;
            ComPort = !string.IsNullOrEmpty(can.ComPort) ? can.ComPort : "Not Connected";

            currentConfig = ConfigLoader.LoadConfigGUI();
            _selectedFlightMode = currentConfig.FlightMode;
            ControllerHandlerViewModel.CurrentFlightMode = _selectedFlightMode;
            _maxThrusterSpeedPercent = Math.Clamp(currentConfig.MaxThrusterSpeedPercent, 0, 100);
            ControllerHandlerViewModel.MaxThrusterSpeedPercent = _maxThrusterSpeedPercent;

            ArmThrusters_Command = new RelayCommand(() => _ = ArmThrustersAsync());
            DisarmThrusters_Command = new RelayCommand(() => _ = DisarmThrustersAsync());
            ApplyUpdateInterval_Command = new RelayCommand(() => _ = ApplyUpdateIntervalAsync());
            ResetWatchdog_Command = new RelayCommand(() => _ = ResetWatchdogAsync());

            ConnectToPixhawk_Command = new RelayCommand(() => _ = ConnectToControlUnitAsync());
            HealthCommand = new RelayCommand(() => _ = SendHealthMessageAsync());
        }

        private Task<byte[]> SendAndWaitForResponseAsync(ushort sendId, ushort recvId, byte[] payload = null, int timeoutMs = 400)
        {
            if (can == null || !can.IsOpen)
                return Task.FromResult(Array.Empty<byte>());

            return can.SendAndWaitForResponseAsync(sendId, recvId, payload, timeoutMs);
        }

        public async Task SendHealthMessageAsync()
        {
            try
            {
                MainWindowViewModel.Instance.LogToControlBox("Checking Pixhawk CAN link...");
                if (can == null || !can.IsOpen)
                {
                    MainWindowViewModel.Instance.LogToControlBox("CAN adapter is not open.");
                    return;
                }

                bool ok = await can.TestConnectionAsync(300);
                if (ok)
                    MainWindowViewModel.Instance.LogToControlBox($"Health Ping OK (Pixhawk responded — {ArmedStatusText})");
                else
                    MainWindowViewModel.Instance.LogToControlBox("Health Ping failed (no response from Pixhawk)");
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Health Ping error: {ex.Message}");
            }
        }

        #region Config helpers

        private void UpdateConfig() => ConfigLoader.UpdateConfigGUI(currentConfig);

        public async Task ConnectToControlUnitAsync()
        {
            try
            {
                bool wasAlreadyOpen = can != null && can.IsOpen;
                if (can == null)
                {
                    can = new UsbCANWrapper();
                    can.TelemetryReceived += OnPixhawkTelemetryReceived;
                }
                else
                {
                    can.ConnectToCanAdapter();
                }

                ComPort = !string.IsNullOrEmpty(can.ComPort) ? can.ComPort : "Not Connected";

                // Check if the adapter is connected before proceeding
                if (can == null || !can.IsOpen)
                {
                    MainWindowViewModel.Instance.LogToControlBox("USB serial CAN adapter not connected.");
                    MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.ErrorConnecting);
                    return;
                }

                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.Connecting);
                var initResp = await SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.InitConnection,
                    (ushort)RecieveAddressesControlUnit.InitConnection,
                    null,
                    500);

                bool ok = (initResp.Length > 0 && initResp[0] == 1) || await can.TestConnectionAsync(500);

                // If the port was already open (e.g. wedged CH340 USB-serial adapter) and didn't reply, force-reopen once
                if (!ok && wasAlreadyOpen)
                {
                    MainWindowViewModel.Instance.LogToControlBox("No reply on open port — reopening USB-CAN adapter...");
                    if (can.ConnectToCanAdapter(force: true) && can.IsOpen)
                    {
                        ComPort = !string.IsNullOrEmpty(can.ComPort) ? can.ComPort : "Not Connected";
                        initResp = await SendAndWaitForResponseAsync(
                            (ushort)SendAddressesControlUnit.InitConnection,
                            (ushort)RecieveAddressesControlUnit.InitConnection,
                            null,
                            500);
                        ok = (initResp.Length > 0 && initResp[0] == 1) || await can.TestConnectionAsync(500);
                    }
                }

                if (ok)
                {
                    MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.Connected);
                    // Probe health once so armed state & active ArduSub mode are synced immediately
                    await can.TestConnectionAsync(300);
                    MainWindowViewModel.Instance.LogToControlBox($"Connected to Pixhawk on {ComPort} ({ArmedStatusText}).");
                    await SendSpeedLimitAsync(MaxThrusterSpeedPercent);
                    SendFlightModeFrame(SelectedFlightMode);
                    if (App.Instance?.ControlParamterHandlerViewModelGetter != null)
                    {
                        await App.Instance.ControlParamterHandlerViewModelGetter.PullFromPixhawkAsync();
                    }
                }
                else
                {
                    MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.ErrorConnecting);
                    MainWindowViewModel.Instance.LogToControlBox("CAN adapter open, but no reply from Pixhawk.");
                }
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.UpdateConnectionStatusLCE(LceConnectionStatus.ErrorConnecting);
                MainWindowViewModel.Instance?.LogToControlBox($"CAN connect error: {ex.Message}");
            }
        }

        public void DisarmBeforeClose()
        {
            if (can == null || !can.IsOpen)
                return;

            try
            {
                can.SendFrame((ushort)SendAddressesControlUnit.DisarmThrusters);
                Thread.Sleep(50); // Allow USB-serial TX buffer to flush before port closes
                IsArmed = false;
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Disarm before close error: {ex.Message}");
            }
        }

        public void DisconnectFromLCE()
        {
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

            // Keep the shared UsbCANWrapper instance intact so other ViewModels don't hold stale references
            IsArmed = false;
            ComPort = "Disconnected";
        }

        public UsbCANWrapper GetCANWrapper()
        {
            return can;
        }

        #endregion Config helpers

        #region Arm / Disarm Thrusters

        public async Task ArmThrustersAsync()
        {
            try
            {
                if (can == null)
                {
                    can = new UsbCANWrapper();
                    can.TelemetryReceived += OnPixhawkTelemetryReceived;
                }
                else if (!can.IsOpen)
                {
                    can.ConnectToCanAdapter();
                }

                ComPort = !string.IsNullOrEmpty(can.ComPort) ? can.ComPort : "Not Connected";

                if (can == null || !can.IsOpen)
                {
                    MainWindowViewModel.Instance.LogToControlBox("Cannot arm thrusters: USB-CAN adapter not connected.");
                    return;
                }

                await SendSpeedLimitAsync(MaxThrusterSpeedPercent);

                var modePayload = new byte[] { GetArduSubModeByte(SelectedFlightMode) };
                var response = await SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.ArmThrusters,
                    (ushort)RecieveAddressesControlUnit.ArmThrusters,
                    modePayload,
                    500);

                if (response.Length > 0 && response[0] == 1)
                {
                    IsArmed = true;
                    MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.Connected);
                    MainWindowViewModel.Instance.LogToControlBox("Thrusters ARMED successfully.");
                }
                else if (response.Length > 0 && response[0] == 0)
                {
                    IsArmed = false;
                    MainWindowViewModel.Instance.LogToControlBox("Thrusters arming REJECTED by Pixhawk (check pre-arm checks / safety switch).");
                }
                else
                {
                    MainWindowViewModel.Instance.LogToControlBox("Arming failed (no CAN response from Pixhawk).");
                }
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Arm thrusters error: {ex.Message}");
            }
        }

        public async Task DisarmThrustersAsync()
        {
            try
            {
                if (can == null || !can.IsOpen)
                {
                    MainWindowViewModel.Instance.LogToControlBox("Cannot disarm thrusters: USB-CAN adapter not connected.");
                    return;
                }

                var response = await SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.DisarmThrusters,
                    (ushort)RecieveAddressesControlUnit.DisarmThrusters,
                    null,
                    500);

                if (response.Length > 0 && response[0] == 1)
                {
                    IsArmed = false;
                    MainWindowViewModel.Instance.LogToControlBox("Thrusters DISARMED safely.");
                }
                else
                {
                    MainWindowViewModel.Instance.LogToControlBox("Thrusters disarm failed or timed out.");
                }
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Disarm thrusters error: {ex.Message}");
            }
        }

        #endregion Arm / Disarm Thrusters

        #region Update Interval

        private async Task ApplyUpdateIntervalAsync()
        {
            try
            {
                if (can == null || !can.IsOpen)
                {
                    MainWindowViewModel.Instance.LogToControlBox("Cannot update cycle time: CAN not open.");
                    return;
                }

                byte targetInterval = Math.Clamp(UpdateInterval, (byte)5, (byte)100);
                UpdateInterval = targetInterval;

                byte[] resp = await SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.UpdateCycleTime,
                    (ushort)RecieveAddressesControlUnit.UpdateCycleTime,
                    new byte[] { targetInterval },
                    300);

                if (resp.Length > 0)
                {
                    UpdateInterval = resp[0];
                    MainWindowViewModel.Instance.LogToControlBox($"Pixhawk CAN bridge cycle time updated to {resp[0]} ms.");
                }
                else
                {
                    MainWindowViewModel.Instance.LogToControlBox("Updating cycle time failed (no response).");
                }
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Update cycle time error: {ex.Message}");
            }
        }

        #endregion Update Interval

        #region Watchdog Reset

        private async Task ResetWatchdogAsync()
        {
            try
            {
                if (can == null || !can.IsOpen)
                {
                    MainWindowViewModel.Instance.LogToControlBox("Cannot reset watchdog: CAN not open.");
                    return;
                }

                byte[] response = await SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.ResetWatchdog,
                    (ushort)RecieveAddressesControlUnit.ResetWatchdog,
                    null,
                    300);

                if (response.Length != 0 && response[0] == 0x01)
                {
                    MainWindowViewModel.Instance.LogToControlBox("Pixhawk Watchdog reset successful.");
                }
                else
                {
                    MainWindowViewModel.Instance.LogToControlBox("Watchdog reset failed.");
                }
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Reset watchdog error: {ex.Message}");
            }
        }

        #endregion Watchdog Reset

        #region Flight Mode & Speed Limit Helpers

        private static byte GetArduSubModeByte(FlightMode mode) => mode switch
        {
            FlightMode.Manual => 19,
            FlightMode.DepthHold => 2,
            _ => 0,
        };

        private void SendFlightModeFrame(FlightMode mode)
        {
            if (can == null || !can.IsOpen)
                return;

            var cmd = mode switch
            {
                FlightMode.Manual => SendAddressesControlUnit.ApplyControllerValues_Manual,
                FlightMode.DepthHold => SendAddressesControlUnit.ApplyControllerValues_DepthHold,
                _ => SendAddressesControlUnit.ApplyControllerValues_Stabilize,
            };

            can.SendFrame((ushort)cmd, new byte[] { 127, 127, 127, 127, 127, 127 });
        }

        private async Task SendSpeedLimitAsync(int percent)
        {
            if (can == null || !can.IsOpen)
                return;

            byte clamped = (byte)Math.Clamp(percent, 0, 100);
            var resp = await SendAndWaitForResponseAsync(
                (ushort)SendAddressesControlUnit.SetSpeedLimit,
                (ushort)RecieveAddressesControlUnit.SetSpeedLimit,
                new byte[] { clamped },
                300);

            if (resp.Length > 0)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Pixhawk max thruster speed set to {resp[0]}%.");
            }
        }

        #endregion Flight Mode & Speed Limit Helpers
    }
}
