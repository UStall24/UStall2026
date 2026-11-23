using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;

namespace UStallGUI.ViewModel
{
    public class QuickSetupViewModel : ObservableObject
    {
        public static QuickSetupViewModel Instance;

        public RelayCommand ConnectToPixhawk_Command { get; }
        public RelayCommand ArmThrusters_Command { get; }
        public RelayCommand DisarmThrusters_Command { get; }
        public RelayCommand CheckConnection_Command { get; }
        public RelayCommand ConnectToController_Command { get; }

        public bool IsArmed => SetupHandlerViewModel.Instance?.IsArmed ?? false;

        public string ArmedStatusText => SetupHandlerViewModel.Instance?.ArmedStatusText ?? "DISARMED";

        public void NotifyArmedStateChanged()
        {
            RaisePropertyChanged(nameof(IsArmed));
            RaisePropertyChanged(nameof(ArmedStatusText));
        }

        public int SelectedFlightModeIndex
        {
            get => SetupHandlerViewModel.Instance != null ? SetupHandlerViewModel.Instance.SelectedFlightModeIndex : 1;
            set
            {
                if (SetupHandlerViewModel.Instance != null)
                {
                    SetupHandlerViewModel.Instance.SelectedFlightModeIndex = value;
                    RaisePropertyChanged(nameof(SelectedFlightModeIndex));
                }
            }
        }

        public void NotifyFlightModeChanged()
        {
            RaisePropertyChanged(nameof(SelectedFlightModeIndex));
        }

        public int MaxThrusterSpeedPercent
        {
            get => SetupHandlerViewModel.Instance != null ? SetupHandlerViewModel.Instance.MaxThrusterSpeedPercent : 100;
            set
            {
                if (SetupHandlerViewModel.Instance != null)
                {
                    SetupHandlerViewModel.Instance.MaxThrusterSpeedPercent = value;
                    RaisePropertyChanged(nameof(MaxThrusterSpeedPercent));
                }
            }
        }

        public void NotifyMaxThrusterSpeedChanged()
        {
            RaisePropertyChanged(nameof(MaxThrusterSpeedPercent));
        }

        public void NotifyActionBusyChanged()
        {
            ConnectToPixhawk_Command?.RaiseCanExecuteChanged();
            ArmThrusters_Command?.RaiseCanExecuteChanged();
            DisarmThrusters_Command?.RaiseCanExecuteChanged();
        }

        public QuickSetupViewModel()
        {
            Instance = this;

            ConnectToPixhawk_Command = new RelayCommand(ConnectToControlUnit, () => SetupHandlerViewModel.Instance?.CanConnect ?? true);
            ArmThrusters_Command = new RelayCommand(ArmThrusters, () => SetupHandlerViewModel.Instance?.CanArm ?? true);
            DisarmThrusters_Command = new RelayCommand(DisarmThrusters, () => SetupHandlerViewModel.Instance?.CanDisarm ?? true);
            CheckConnection_Command = new RelayCommand(SendHealthMessage);
            ConnectToController_Command = new RelayCommand(ConnectToController);
        }

        private void ConnectToControlUnit()
        {
            if (SetupHandlerViewModel.Instance != null)
            {
                _ = SetupHandlerViewModel.Instance.ConnectToControlUnitAsync();
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("SetupHandlerViewModel not initialized.");
            }
        }

        private void ArmThrusters()
        {
            if (SetupHandlerViewModel.Instance != null)
            {
                _ = SetupHandlerViewModel.Instance.ArmThrustersAsync();
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("SetupHandlerViewModel not initialized.");
            }
        }

        private void DisarmThrusters()
        {
            if (SetupHandlerViewModel.Instance != null)
            {
                _ = SetupHandlerViewModel.Instance.DisarmThrustersAsync();
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("SetupHandlerViewModel not initialized.");
            }
        }

        private void SendHealthMessage()
        {
            if (SetupHandlerViewModel.Instance != null)
            {
                _ = SetupHandlerViewModel.Instance.SendHealthMessageAsync();
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("SetupHandlerViewModel not initialized.");
            }
        }

        private void ConnectToController()
        {
            if (ControllerHandlerViewModel.Instance != null)
            {
                ControllerHandlerViewModel.Instance.SetController(0);
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("ControllerHandlerViewModel not initialized.");
            }
        }
    }
}
