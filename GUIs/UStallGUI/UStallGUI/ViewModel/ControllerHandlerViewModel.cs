using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using SharpDX.XInput;
using System;
using System.Threading.Tasks;
using UStallGUI.Helpers;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class ControllerHandlerViewModel : ObservableObject
    {
        public static ControllerHandlerViewModel Instance;
        public static bool SendControllerValues = false;

        public static bool UseDynamicThrottle = false;

        private readonly int pollingFrequency = 200;
        private readonly int sendingDividor = 10;
        private bool keepPolling = false;

        // Properties for binding
        public ControllerModel CurrentControllerModel { get; set; } = new();

        public ControllerHandlerViewModel()
        {
            Instance = this;
            ConnectToController = new RelayCommand(() => SetController(0)); // Default to controller index 0
        }

        private void SetController(int index)
        {
            Controller controller = new Controller((UserIndex)index);
            CurrentControllerModel.Controller = controller;

            // Run the polling task in the background
            _ = Task.Run(ControllerTimerCallback);

            MainWindowViewModel.Instance.ControlBoxConsoleText = "Controller with Index 0 is connected";
            MainWindowViewModel.Instance.ConnectionStatusController = "Connected";
        }

        private async Task ControllerTimerCallback()
        {
            keepPolling = true;
            int delayMs = pollingFrequency > 0 ? 1000 / pollingFrequency : 10;
            int dividorCounter = 0;

            try
            {
                while (keepPolling)
                {
                    CurrentControllerModel.UpdateControllerValues();

                    if (dividorCounter == 0)
                    {
                        var command = UseDynamicThrottle
                            ? LCE_CommandAddresses.ApplyControllerValues_DynamicThrottle
                            : LCE_CommandAddresses.ApplyControllerValues;

                        if (SendControllerValues)
                            SetupHandlerViewModel.can?.WriteBytes((ushort)command, CurrentControllerModel.GetMovementBytes());

                        Console.WriteLine($"Sending: {SendControllerValues} Dynamic Throttle: {UseDynamicThrottle}: {CurrentControllerModel.GetMovementBytesAsString()}");
                        dividorCounter = sendingDividor;
                    }
                    else
                    {
                        dividorCounter--;
                    }

                    RaisePropertyChanged(nameof(CurrentControllerModel));
                    await Task.Delay(delayMs);
                }
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance.ControlBoxConsoleText = $"Controller Exception: {ex}";
            }
        }

        public RelayCommand ConnectToController { get; set; }
    }
}