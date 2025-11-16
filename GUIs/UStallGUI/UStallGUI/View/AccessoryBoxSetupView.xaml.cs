using System.Windows.Controls;
using UStallGUI.ViewModel;

namespace UStallGUI.View
{
    /// <summary>
    /// Interaction logic for AccessoryBoxSetupView.xaml
    /// </summary>
    public partial class AccessoryBoxSetupView : UserControl
    {
        public AccessoryBoxSetupView()
        {
            InitializeComponent();
            DataContext = new AccessoryBoxViewModel();

        }
    }
}
