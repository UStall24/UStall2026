using System.Windows.Controls;
using UStallGUI.ViewModel;

namespace UStallGUI.View
{
    /// <summary>
    /// Interaction logic for ControllerHandlerView.xaml
    /// </summary>
    public partial class ControllerHandlerView : UserControl
    {
        public ControllerHandlerView()
        {
            InitializeComponent();
            DataContext = new ControllerHandlerViewModel();
        }
    }
}