using System.Windows;
using System.Windows.Controls;

namespace UStallGUI.View
{
    public partial class TelemetryView : UserControl
    {
        public TelemetryView()
        {
            InitializeComponent();
            DataContext = App.Instance.TelemetryViewModelGetter;
            IsVisibleChanged += TelemetryView_IsVisibleChanged;
        }

        private void TelemetryView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible)
            {
                App.Instance.TelemetryViewModelGetter?.OnTabVisibilityChanged(isVisible);
            }
        }
    }
}
