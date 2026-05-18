using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;
using SharpDX.XInput;
using System.Threading.Tasks;
using UStallGUI.Helpers;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class QuickSetupViewModel : ObservableObject
    {
        public static QuickSetupViewModel Instance;

        public RelayCommand InitThrusters_Command { get; }
        public RelayCommand CheckConnection_Command { get; }
        public RelayCommand ConnectToController_Command { get; }

        public QuickSetupViewModel()
        {
            Instance = this;

            InitThrusters_Command = new RelayCommand(ConnectToControlUnit);
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
                MainWindowViewModel.Instance.LogToControlBox("Fehler: ControllerHandlerViewModel ist noch nicht initialisiert.");
            }
        }
    }
}