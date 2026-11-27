using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UStallGUI.Helpers;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class PidParameterItem : ObservableObject
    {
        public byte ParamId { get; }
        public string Name { get; }
        public float DefaultValue { get; }
        public float MinValue { get; }
        public float MaxValue { get; }

        private string _valueText;
        public string ValueText
        {
            get => _valueText;
            set => Set(ref _valueText, value);
        }

        public string ToolTipText =>
            $"{Name} (Default: {FormatFloat(DefaultValue)}, Range: {FormatFloat(MinValue)}-{FormatFloat(MaxValue)})";

        public PidParameterItem(byte paramId, string name, float defaultValue, float minValue, float maxValue)
        {
            ParamId = paramId;
            Name = name;
            DefaultValue = defaultValue;
            MinValue = minValue;
            MaxValue = maxValue;
            _valueText = FormatFloat(defaultValue);
        }

        public static string FormatFloat(float value) =>
            value.ToString("0.####", CultureInfo.InvariantCulture);

        public void SetValue(float value)
        {
            ValueText = FormatFloat(value);
        }

        public void ResetToDefault()
        {
            SetValue(DefaultValue);
        }

        public bool TryGetParsedValue(out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(ValueText))
                return false;

            string normalized = ValueText.Trim().Replace(',', '.');
            if (!float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return false;

            if (!float.IsFinite(parsed) || parsed < MinValue || parsed > MaxValue)
                return false;

            value = parsed;
            return true;
        }
    }

    public class SetupHandlerViewModel : ObservableObject
    {
        public static SetupHandlerViewModel Instance { get; private set; }

        #region Pixhawk Setup

        private byte _updateInterval = 10;
        private int _controlType = 1;
        private string _comPort = "Auto";
        private readonly object _telemetryLock = new();
        private bool _isArmed = false;
        private string _activeArduSubModeText = string.Empty;
        private bool _isConnecting = false;
        private bool _isArming = false;
        private bool _isDisarming = false;

        public bool CanConnect => !_isConnecting && !_isArming;
        public bool CanArm => !_isConnecting && !_isArming && !_isDisarming;
        public bool CanDisarm => !_isDisarming;

        private void NotifyActionBusyChanged()
        {
            RaisePropertyChanged(nameof(CanConnect));
            RaisePropertyChanged(nameof(CanArm));
            RaisePropertyChanged(nameof(CanDisarm));
            ConnectToPixhawk_Command?.RaiseCanExecuteChanged();
            ArmThrusters_Command?.RaiseCanExecuteChanged();
            DisarmThrusters_Command?.RaiseCanExecuteChanged();
            QuickSetupViewModel.Instance?.NotifyActionBusyChanged();
        }

        private ConfigGUI currentConfig;

        private UsbCANWrapper can;

        public string ComPort
        {
            get => _comPort;
            set => Set(ref _comPort, value);
        }

        public bool IsArmed
        {
            get { lock (_telemetryLock) return _isArmed; }
            private set
            {
                bool changed;
                lock (_telemetryLock)
                {
                    changed = _isArmed != value;
                    _isArmed = value;
                }
                if (changed)
                {
                    RaisePropertyChanged(nameof(IsArmed));
                    RaisePropertyChanged(nameof(ArmedStatusText));
                    QuickSetupViewModel.Instance?.NotifyArmedStateChanged();
                }
            }
        }

        public string ArmedStatusText
        {
            get
            {
                bool armed;
                string modeText;
                lock (_telemetryLock)
                {
                    armed = _isArmed;
                    modeText = _activeArduSubModeText;
                }
                return armed
                    ? (string.IsNullOrEmpty(modeText) ? "ARMED" : $"ARMED ({modeText})")
                    : "DISARMED";
            }
        }

        private static string FormatArduSubMode(byte arduSubMode) => arduSubMode switch
        {
            0 => "STABILIZE",
            2 => "DEPTH_HOLD",
            19 => "MANUAL",
            _ => $"MODE {arduSubMode}",
        };

        private void OnPixhawkTelemetryReceived(bool isArmed, byte arduSubMode)
        {
            string modeText = FormatArduSubMode(arduSubMode);
            bool modeChanged;
            bool armedChanged;

            lock (_telemetryLock)
            {
                modeChanged = _activeArduSubModeText != modeText;
                _activeArduSubModeText = modeText;

                armedChanged = _isArmed != isArmed;
                _isArmed = isArmed;
            }

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

        #region PID Parameters (Attitude & Depth Hold)

        private bool _pidSync_Enabled = true;
        public bool PidSync_Enabled
        {
            get => _pidSync_Enabled;
            private set
            {
                if (Set(ref _pidSync_Enabled, value))
                {
                    PullPids_Command?.RaiseCanExecuteChanged();
                    ApplyPids_Command?.RaiseCanExecuteChanged();
                }
            }
        }

        // Attitude Roll PIDs
        public PidParameterItem RollAngP { get; } = new(1, "ATC_ANG_RLL_P", 6.0f, 0f, 20f);
        public PidParameterItem RollRateP { get; } = new(2, "ATC_RAT_RLL_P", 0.135f, 0f, 2f);
        public PidParameterItem RollRateI { get; } = new(3, "ATC_RAT_RLL_I", 0.09f, 0f, 2f);
        public PidParameterItem RollRateD { get; } = new(4, "ATC_RAT_RLL_D", 0.0036f, 0f, 0.5f);
        public PidParameterItem RollRateIMax { get; } = new(5, "ATC_RAT_RLL_IMAX", 0.444f, 0f, 1f);

        // Attitude Pitch PIDs
        public PidParameterItem PitchAngP { get; } = new(6, "ATC_ANG_PIT_P", 6.0f, 0f, 20f);
        public PidParameterItem PitchRateP { get; } = new(7, "ATC_RAT_PIT_P", 0.135f, 0f, 2f);
        public PidParameterItem PitchRateI { get; } = new(8, "ATC_RAT_PIT_I", 0.09f, 0f, 2f);
        public PidParameterItem PitchRateD { get; } = new(9, "ATC_RAT_PIT_D", 0.0036f, 0f, 0.5f);
        public PidParameterItem PitchRateIMax { get; } = new(10, "ATC_RAT_PIT_IMAX", 0.444f, 0f, 1f);

        // Attitude Yaw PIDs
        public PidParameterItem YawAngP { get; } = new(11, "ATC_ANG_YAW_P", 6.0f, 0f, 20f);
        public PidParameterItem YawRateP { get; } = new(12, "ATC_RAT_YAW_P", 0.18f, 0f, 2f);
        public PidParameterItem YawRateI { get; } = new(13, "ATC_RAT_YAW_I", 0.018f, 0f, 2f);
        public PidParameterItem YawRateD { get; } = new(14, "ATC_RAT_YAW_D", 0.0f, 0f, 0.5f);
        public PidParameterItem YawRateIMax { get; } = new(15, "ATC_RAT_YAW_IMAX", 0.222f, 0f, 1f);

        // Depth Hold (ALT_HOLD) Z-axis PIDs
        public PidParameterItem DepthPosP { get; } = new(16, "PSC_D_POS_P", 3.0f, 0f, 15f);
        public PidParameterItem DepthVelP { get; } = new(17, "PSC_D_VEL_P", 8.0f, 0f, 25f);
        public PidParameterItem DepthVelI { get; } = new(18, "PSC_D_VEL_I", 0.0f, 0f, 10f);
        public PidParameterItem DepthVelD { get; } = new(19, "PSC_D_VEL_D", 0.0f, 0f, 5f);
        public PidParameterItem DepthAccP { get; } = new(20, "PSC_D_ACC_P", 0.05f, 0f, 2f);
        public PidParameterItem DepthAccI { get; } = new(21, "PSC_D_ACC_I", 0.01f, 0f, 2f);
        public PidParameterItem DepthAccD { get; } = new(22, "PSC_D_ACC_D", 0.0f, 0f, 0.5f);

        private readonly PidParameterItem[] _allPidParams;

        public RelayCommand PullPids_Command { get; }
        public RelayCommand ApplyPids_Command { get; }
        public RelayCommand ResetPidsToDefault_Command { get; }

        #endregion PID Parameters (Attitude & Depth Hold)

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

            ArmThrusters_Command = new RelayCommand(() => _ = ArmThrustersAsync(), () => CanArm);
            DisarmThrusters_Command = new RelayCommand(() => _ = DisarmThrustersAsync(), () => CanDisarm);
            ApplyUpdateInterval_Command = new RelayCommand(() => _ = ApplyUpdateIntervalAsync());
            ResetWatchdog_Command = new RelayCommand(() => _ = ResetWatchdogAsync());

            ConnectToPixhawk_Command = new RelayCommand(() => _ = ConnectToControlUnitAsync(), () => CanConnect);
            HealthCommand = new RelayCommand(() => _ = SendHealthMessageAsync());

            _allPidParams = new[]
            {
                RollAngP, RollRateP, RollRateI, RollRateD, RollRateIMax,
                PitchAngP, PitchRateP, PitchRateI, PitchRateD, PitchRateIMax,
                YawAngP, YawRateP, YawRateI, YawRateD, YawRateIMax,
                DepthPosP, DepthVelP, DepthVelI, DepthVelD, DepthAccP, DepthAccI, DepthAccD
            };

            PullPids_Command = new RelayCommand(() => _ = PullPidsFromPixhawkAsync(), () => PidSync_Enabled);
            ApplyPids_Command = new RelayCommand(() => _ = ApplyPidsToPixhawkAsync(), () => PidSync_Enabled);
            ResetPidsToDefault_Command = new RelayCommand(ResetPidsToDefaults);
        }

        private Task<byte[]> SendAndWaitForResponseAsync(
            ushort sendId,
            ushort recvId,
            byte[]? payload = null,
            int timeoutMs = 400,
            Func<byte[], bool>? predicate = null)
        {
            if (can == null || !can.IsOpen)
                return Task.FromResult(Array.Empty<byte>());

            return can.SendAndWaitForResponseAsync(sendId, recvId, payload, timeoutMs, predicate);
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
            if (_isConnecting || _isArming)
                return;

            _isConnecting = true;
            NotifyActionBusyChanged();
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
                    // Only apply the GUI's selected flight mode when disarmed so reconnecting
                    // never interrupts an already-armed ROV's active mode.
                    if (!IsArmed)
                    {
                        SendFlightModeFrame(SelectedFlightMode);
                    }
                    // Pull persisted motor config, axis gains, and global speed limit from Pixhawk
                    if (App.Instance?.ControlParamterHandlerViewModelGetter != null)
                    {
                        await App.Instance.ControlParamterHandlerViewModelGetter.PullFromPixhawkAsync();
                    }
                    await PullPidsFromPixhawkAsync();
                    if (TelemetryViewModel.Instance != null)
                    {
                        await TelemetryViewModel.Instance.SyncStreamIfOpenAsync();
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
            finally
            {
                _isConnecting = false;
                NotifyActionBusyChanged();
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
            if (_isConnecting || _isArming || _isDisarming)
                return;

            _isArming = true;
            NotifyActionBusyChanged();
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
                    OnPixhawkTelemetryReceived(true, modePayload[0]);
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
            finally
            {
                _isArming = false;
                NotifyActionBusyChanged();
            }
        }

        public async Task DisarmThrustersAsync()
        {
            if (_isDisarming)
                return;

            _isDisarming = true;
            NotifyActionBusyChanged();
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
            finally
            {
                _isDisarming = false;
                NotifyActionBusyChanged();
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

        #region PID Sync Helpers

        public async Task PullPidsFromPixhawkAsync()
        {
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance?.LogToControlBox("Cannot pull PIDs: USB-CAN adapter not connected.");
                return;
            }

            if (!PidSync_Enabled)
                return;

            PidSync_Enabled = false;
            try
            {
                int okCount = 0;
                foreach (var item in _allPidParams)
                {
                    var resp = await SendAndWaitForResponseAsync(
                        (ushort)SendAddressesControlUnit.PidParam,
                        (ushort)RecieveAddressesControlUnit.PidParam,
                        new byte[] { item.ParamId },
                        300,
                        data => data.Length >= 6 && data[0] == item.ParamId);

                    if (resp.Length >= 6 && resp[0] == item.ParamId && resp[1] == 1)
                    {
                        float val = BinaryPrimitives.ReadSingleLittleEndian(resp.AsSpan(2, 4));
                        item.SetValue(val);
                        okCount++;
                    }
                }

                if (okCount == _allPidParams.Length)
                    MainWindowViewModel.Instance?.LogToControlBox($"Pulled {okCount}/{_allPidParams.Length} PID parameters from Pixhawk.");
                else
                    MainWindowViewModel.Instance?.LogToControlBox($"PID pull incomplete ({okCount}/{_allPidParams.Length} received - check CAN link).");
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Pull PIDs error: {ex.Message}");
            }
            finally
            {
                PidSync_Enabled = true;
            }
        }

        public async Task ApplyPidsToPixhawkAsync()
        {
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance?.LogToControlBox("Cannot apply PIDs: USB-CAN adapter not connected.");
                return;
            }

            if (!PidSync_Enabled)
                return;

            // Validate all fields first so we never write a partial/broken PID set
            var parsedValues = new float[_allPidParams.Length];
            for (int i = 0; i < _allPidParams.Length; i++)
            {
                var item = _allPidParams[i];
                if (!item.TryGetParsedValue(out parsedValues[i]))
                {
                    MainWindowViewModel.Instance?.LogToControlBox(
                        $"Invalid value for {item.Name}: '{item.ValueText}' (allowed range: {PidParameterItem.FormatFloat(item.MinValue)}-{PidParameterItem.FormatFloat(item.MaxValue)}).");
                    return;
                }
            }

            PidSync_Enabled = false;
            try
            {
                int okCount = 0;
                byte[] payload = new byte[6];
                for (int i = 0; i < _allPidParams.Length; i++)
                {
                    var item = _allPidParams[i];
                    payload[0] = item.ParamId;
                    payload[1] = 1; // save_flag = 1 (param:set_and_save)
                    BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(2, 4), parsedValues[i]);

                    var resp = await SendAndWaitForResponseAsync(
                        (ushort)SendAddressesControlUnit.PidParam,
                        (ushort)RecieveAddressesControlUnit.PidParam,
                        payload,
                        300,
                        data => data.Length >= 6 && data[0] == item.ParamId);

                    if (resp.Length >= 6 && resp[0] == item.ParamId && resp[1] == 1)
                    {
                        float confirmed = BinaryPrimitives.ReadSingleLittleEndian(resp.AsSpan(2, 4));
                        item.SetValue(confirmed);
                        okCount++;
                    }
                }

                if (okCount == _allPidParams.Length)
                    MainWindowViewModel.Instance?.LogToControlBox($"Applied & saved {okCount}/{_allPidParams.Length} PID parameters to Pixhawk.");
                else
                    MainWindowViewModel.Instance?.LogToControlBox($"PID apply incomplete ({okCount}/{_allPidParams.Length} confirmed - check CAN link).");
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Apply PIDs error: {ex.Message}");
            }
            finally
            {
                PidSync_Enabled = true;
            }
        }

        private void ResetPidsToDefaults()
        {
            foreach (var item in _allPidParams)
                item.ResetToDefault();

            MainWindowViewModel.Instance?.LogToControlBox("PID fields reset to ArduSub defaults (click 'Apply to Pixhawk' to save).");
        }

        #endregion PID Sync Helpers
    }
}
