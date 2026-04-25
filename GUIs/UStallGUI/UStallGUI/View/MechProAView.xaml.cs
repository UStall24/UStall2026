using System.Windows.Controls;
using UStallGUI.ViewModel;

namespace UStallGUI.View
{
    /// <summary>
    /// Interaction logic for AccessoryBoxSetupView.xaml
    /// </summary>
    public partial class MechProAView : UserControl
    {
        public MechProAView()
        {
            InitializeComponent();
            DataContext = App.Instance.AccessoryBoxViewModelGetter;
        }
    }
}