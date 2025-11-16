using System.Windows.Controls;
using UStallGUI.ViewModel;

namespace UStallGUI.View
{
    /// <summary>
    /// Interaction logic for SetupHandlerView.xaml
    /// </summary>
    public partial class SetupHandlerView : UserControl
    {
        public SetupHandlerView()
        {
            InitializeComponent();
            DataContext = new SetupHandlerViewModel();
        }
    }
}