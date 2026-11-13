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

        public bool DepthHoldEnabled
        {
            get => SetupHandlerViewModel.Instance != null && SetupHandlerViewModel.Instance.DepthHoldEnabled;
            set
            {
                if (SetupHandlerViewModel.Instance != null)
                {
                    SetupHandlerViewModel.Instance.DepthHoldEnabled = value;
                    RaisePropertyChanged(nameof(DepthHoldEnabled));
                }
            }
        }

        public void NotifyDepthHoldChanged()
        {
            RaisePropertyChanged(nameof(DepthHoldEnabled));
        }

        public QuickSetupViewModel()
        {
            Instance = this;

            ConnectToPixhawk_Command = new RelayCommand(ConnectToControlUnit);
            ArmThrusters_Command = new RelayCommand(ArmThrusters);
            DisarmThrusters_Command = new RelayCommand(DisarmThrusters);
            CheckConnection_Command = new RelayCommand(SendHealthMessage);
            ConnectToController_Command = new RelayCommand(ConnectToController);
        }

        private void ConnectToControlUnit()
        {
            if (SetupHandlerViewModel.Instance != null)
            {
                SetupHandlerViewModel.Instance.ConnectToControlUnit();
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
                SetupHandlerViewModel.Instance.SendHealthMessage();
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
