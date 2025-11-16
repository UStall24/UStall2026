using System;
using System.Diagnostics;
using System.Windows;
using UStallGUI.Model;
using UStallGUI.ViewModel;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace UStallGUI
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            // Ensure console is shown (optional, only needed if not already visible)
            AllocConsole();

            // Load config before window is initialized
            ConfigLoader.CurrentConfig = ConfigLoader.LoadConfigGUI();

            InitializeComponent();
            DataContext = new MainWindowViewModel();

            // Position the window in the right half of the screen
            PositionWindowInRightHalf();
        }

        private void PositionWindowInRightHalf()
        {
            double screenWidth = SystemParameters.FullPrimaryScreenWidth;
            double screenHeight = SystemParameters.FullPrimaryScreenHeight;

            this.Left = screenWidth / 2;
            this.Top = 0;
            this.Width = screenWidth / 2;
            this.Height = screenHeight;
        }

        // Import to attach console if needed
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();
    }
}