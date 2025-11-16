using System.Windows.Controls;
using UStallGUI.ViewModel;

namespace UStallGUI.View
{
    /// <summary>
    /// Interaction logic for CameraStreamView.xaml
    /// </summary>
    public partial class CameraStreamView : UserControl
    {
        public CameraStreamView()
        {
            InitializeComponent();
            DataContext = new CameraStreamViewModel();
        }
    }
}