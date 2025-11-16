using System.Windows.Controls;
using UStallGUI.ViewModel;

namespace UStallGUI.View
{
    /// <summary>
    /// Interaction logic for ControlParameterView.xaml
    /// </summary>
    public partial class ControlParameterHandlerView : UserControl
    {
        public ControlParameterHandlerView()
        {
            InitializeComponent();
            DataContext = new ControlParameterHandlerViewModel();
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }
    }
}