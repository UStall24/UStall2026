using System.Windows;
using UStallGUI.ViewModel;

namespace UStallGUI
{
    public partial class App : Application
    {
        public MainWindowViewModel MainWindowViewModelGetter { get; }
        public SetupHandlerViewModel SetupHandlerViewModelGetter { get; }
        public MechProAViewModel AccessoryBoxViewModelGetter { get; }
        public ControllerHandlerViewModel ControllerHandlerViewModelGetter { get; }
        public ControlParameterHandlerViewModel ControlParamterHandlerViewModelGetter { get; }
        public CameraStreamViewModel CameraStreamViewModelGetter { get; }
        public QuickSetupViewModel QuickSetupViewModelGetter { get; }

        public static App Instance { get; private set; }

        private App()
        {
            Instance = this;
            MainWindowViewModelGetter = new MainWindowViewModel();
            SetupHandlerViewModelGetter = new SetupHandlerViewModel();
            AccessoryBoxViewModelGetter = new MechProAViewModel(SetupHandlerViewModelGetter.GetCANWrapper());
            ControllerHandlerViewModelGetter = new ControllerHandlerViewModel(SetupHandlerViewModelGetter.GetCANWrapper());
            ControlParamterHandlerViewModelGetter = new ControlParameterHandlerViewModel(SetupHandlerViewModelGetter.GetCANWrapper());
            CameraStreamViewModelGetter = new CameraStreamViewModel();
            QuickSetupViewModelGetter = new QuickSetupViewModel();
        }
    }
}