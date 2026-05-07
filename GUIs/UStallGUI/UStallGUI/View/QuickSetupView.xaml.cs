using System.Windows.Controls;

namespace UStallGUI.View
{
    /// <summary>
    /// Interaction logic for QuickSetupView.xaml
    /// </summary>
    public partial class QuickSetupView : UserControl
    {
        public QuickSetupView()
        {
            InitializeComponent();
            DataContext = App.Instance.SetupHandlerViewModelGetter;
        }
    }
}