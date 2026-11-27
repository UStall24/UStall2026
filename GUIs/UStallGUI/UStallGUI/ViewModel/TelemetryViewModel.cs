using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UStallGUI.Helpers;

namespace UStallGUI.ViewModel
{
    public class TelemetryViewModel : ObservableObject
    {
        public static TelemetryViewModel? Instance { get; private set; }

        private const byte ImuMaskAll = 7; // Attitude (1) | Gyro (2) | Accel (4)

        private readonly UsbCANWrapper _can;
        private readonly object _dataLock = new();

        private bool _isTabVisible;
        private bool _userPaused;
        private bool _isStreaming;
        private byte _selectedRateHz = 10;

        private bool _hasAttitude;
        private bool _ahrsHealthy;
        private double _rollDeg;
        private double _pitchDeg;
        private double _yawDeg;

        private bool _hasGyro;
        private double _gyroX;
        private double _gyroY;
        private double _gyroZ;

        private bool _hasAccel;
        private double _accelX;
        private double _accelY;
        private double _accelZ;

        private long _frameCount;
        private string _lastUpdateText = "Waiting for Pixhawk...";

        public IReadOnlyList<byte> RateOptions { get; } = new byte[] { 5, 10, 15, 20, 25 };

        public bool IsTabVisible => _isTabVisible;

        public bool IsStreaming
        {
            get => _isStreaming;
            private set
            {
                if (Set(ref _isStreaming, value))
                {
                    RaisePropertyChanged(nameof(StreamButtonText));
                }
            }
        }

        public string StreamButtonText => IsStreaming ? "Stop Stream" : "Start Stream";

        public byte SelectedRateHz
        {
            get => _selectedRateHz;
            set
            {
                byte clamped = Math.Clamp(value, (byte)1, (byte)25);
                if (!Set(ref _selectedRateHz, clamped))
                    return;

                if (IsStreaming)
                {
                    _ = ApplyStreamConfigAsync(clamped, ImuMaskAll);
                }
            }
        }

        public bool HasAttitude { get { lock (_dataLock) return _hasAttitude; } }
        public bool AhrsHealthy { get { lock (_dataLock) return _ahrsHealthy; } }

        public string AhrsStatusText
        {
            get
            {
                lock (_dataLock)
                {
                    if (!_hasAttitude) return "NO DATA";
                    return _ahrsHealthy ? "AHRS HEALTHY" : "AHRS UNHEALTHY";
                }
            }
        }

        public string RollText { get { lock (_dataLock) return _hasAttitude ? FormatSigned(_rollDeg, "0.00", "°") : "--"; } }
        public string PitchText { get { lock (_dataLock) return _hasAttitude ? FormatSigned(_pitchDeg, "0.00", "°") : "--"; } }
        public string YawText { get { lock (_dataLock) return _hasAttitude ? FormatSigned(_yawDeg, "0.00", "°") : "--"; } }

        public string GyroXText { get { lock (_dataLock) return _hasGyro ? FormatSigned(_gyroX, "0.00", " °/s") : "--"; } }
        public string GyroYText { get { lock (_dataLock) return _hasGyro ? FormatSigned(_gyroY, "0.00", " °/s") : "--"; } }
        public string GyroZText { get { lock (_dataLock) return _hasGyro ? FormatSigned(_gyroZ, "0.00", " °/s") : "--"; } }

        public string AccelXText { get { lock (_dataLock) return _hasAccel ? FormatSigned(_accelX, "0.00", " m/s²") : "--"; } }
        public string AccelYText { get { lock (_dataLock) return _hasAccel ? FormatSigned(_accelY, "0.00", " m/s²") : "--"; } }
        public string AccelZText { get { lock (_dataLock) return _hasAccel ? FormatSigned(_accelZ, "0.00", " m/s²") : "--"; } }

        public string AccelNormText
        {
            get
            {
                lock (_dataLock)
                {
                    if (!_hasAccel) return "--";
                    double norm = Math.Sqrt(_accelX * _accelX + _accelY * _accelY + _accelZ * _accelZ);
                    double g = norm / 9.80665;
                    return $"{norm.ToString("0.00", CultureInfo.InvariantCulture)} m/s² ({g.ToString("0.00", CultureInfo.InvariantCulture)} g)";
                }
            }
        }

        public long FrameCount { get { lock (_dataLock) return _frameCount; } }
        public string LastUpdateText { get { lock (_dataLock) return _lastUpdateText; } }

        public RelayCommand ToggleStreamCommand { get; }

        public TelemetryViewModel(UsbCANWrapper can)
        {
            Instance = this;
            _can = can;

            _can.ImuAttitudeReceived += OnImuAttitudeReceived;
            _can.ImuGyroReceived += OnImuGyroReceived;
            _can.ImuAccelReceived += OnImuAccelReceived;

            ToggleStreamCommand = new RelayCommand(() => _ = ToggleStreamAsync());
        }

        private static string FormatSigned(double value, string fmt, string unit)
        {
            string sign = value >= 0 ? "+" : string.Empty;
            return $"{sign}{value.ToString(fmt, CultureInfo.InvariantCulture)}{unit}";
        }

        private void OnImuAttitudeReceived(ImuAttitudeSample sample)
        {
            lock (_dataLock)
            {
                _hasAttitude = true;
                _rollDeg = sample.RollDeg;
                _pitchDeg = sample.PitchDeg;
                _yawDeg = sample.YawDeg;
                _ahrsHealthy = sample.AhrsHealthy;
                _frameCount++;
                _lastUpdateText = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }

            RaisePropertyChanged(nameof(HasAttitude));
            RaisePropertyChanged(nameof(AhrsHealthy));
            RaisePropertyChanged(nameof(AhrsStatusText));
            RaisePropertyChanged(nameof(RollText));
            RaisePropertyChanged(nameof(PitchText));
            RaisePropertyChanged(nameof(YawText));
            RaisePropertyChanged(nameof(FrameCount));
            RaisePropertyChanged(nameof(LastUpdateText));
        }

        private void OnImuGyroReceived(ImuVectorSample sample)
        {
            lock (_dataLock)
            {
                _hasGyro = true;
                _gyroX = sample.X;
                _gyroY = sample.Y;
                _gyroZ = sample.Z;
                _frameCount++;
                _lastUpdateText = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }

            RaisePropertyChanged(nameof(GyroXText));
            RaisePropertyChanged(nameof(GyroYText));
            RaisePropertyChanged(nameof(GyroZText));
            RaisePropertyChanged(nameof(FrameCount));
            RaisePropertyChanged(nameof(LastUpdateText));
        }

        private void OnImuAccelReceived(ImuVectorSample sample)
        {
            lock (_dataLock)
            {
                _hasAccel = true;
                _accelX = sample.X;
                _accelY = sample.Y;
                _accelZ = sample.Z;
                _frameCount++;
                _lastUpdateText = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }

            RaisePropertyChanged(nameof(AccelXText));
            RaisePropertyChanged(nameof(AccelYText));
            RaisePropertyChanged(nameof(AccelZText));
            RaisePropertyChanged(nameof(AccelNormText));
            RaisePropertyChanged(nameof(FrameCount));
            RaisePropertyChanged(nameof(LastUpdateText));
        }

        public void OnTabVisibilityChanged(bool isVisible)
        {
            _isTabVisible = isVisible;
            if (isVisible)
            {
                if (!_userPaused)
                {
                    IsStreaming = true;
                    _ = ApplyStreamConfigAsync(_selectedRateHz, ImuMaskAll);
                }
            }
            else
            {
                if (IsStreaming)
                {
                    IsStreaming = false;
                    if (_can.IsOpen)
                    {
                        _can.SendFrame((ushort)SendAddressesControlUnit.ImuConfig, new byte[] { 0, ImuMaskAll });
                    }
                }
            }
        }

        public void StopStreamImmediate()
        {
            _isTabVisible = false;
            IsStreaming = false;
            if (_can.IsOpen)
            {
                _can.SendFrame((ushort)SendAddressesControlUnit.ImuConfig, new byte[] { 0, ImuMaskAll });
            }
        }

        public async Task SyncStreamIfOpenAsync()
        {
            if (_isTabVisible && !_userPaused)
            {
                IsStreaming = true;
                await ApplyStreamConfigAsync(_selectedRateHz, ImuMaskAll);
            }
        }

        private async Task ToggleStreamAsync()
        {
            if (IsStreaming)
            {
                _userPaused = true;
                IsStreaming = false;
                await ApplyStreamConfigAsync(0, ImuMaskAll);
            }
            else
            {
                _userPaused = false;
                IsStreaming = true;
                await ApplyStreamConfigAsync(_selectedRateHz, ImuMaskAll);
            }
        }

        private async Task ApplyStreamConfigAsync(byte rateHz, byte mask)
        {
            if (!_can.IsOpen)
                return;

            try
            {
                await _can.SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.ImuConfig,
                    (ushort)RecieveAddressesControlUnit.ImuConfig,
                    new byte[] { rateHz, mask },
                    400);
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"IMU stream config error: {ex.Message}");
            }
        }
    }
}
