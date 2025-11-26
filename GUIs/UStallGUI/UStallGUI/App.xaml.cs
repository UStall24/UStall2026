using System.Windows;
using UStallGUI.ViewModel;

namespace UStallGUI
{
    public partial class App : Application
    {
        public MainWindowViewModel MainWindowViewModelGetter { get; }
        public SetupHandlerViewModel SetupHandlerViewModelGetter { get; }
        public AccessoryBoxViewModel AccessoryBoxViewModelGetter { get; }
        public ControllerHandlerViewModel ControllerHandlerViewModelGetter { get; }
        public ControlParameterHandlerViewModel ControlParamterHandlerViewModelGetter { get; }
        public CameraStreamViewModel CameraStreamViewModelGetter { get; }

        public static App Instance { get; private set; }

        private App()
        {
            Instance = this;
            MainWindowViewModelGetter = new MainWindowViewModel();
            SetupHandlerViewModelGetter = new SetupHandlerViewModel();
            AccessoryBoxViewModelGetter = new AccessoryBoxViewModel(SetupHandlerViewModelGetter.GetCANWrapper());
            ControllerHandlerViewModelGetter = new ControllerHandlerViewModel(SetupHandlerViewModelGetter.GetCANWrapper());
            ControlParamterHandlerViewModelGetter = new ControlParameterHandlerViewModel(SetupHandlerViewModelGetter.GetCANWrapper());
            CameraStreamViewModelGetter = new CameraStreamViewModel();
        }
    }
}