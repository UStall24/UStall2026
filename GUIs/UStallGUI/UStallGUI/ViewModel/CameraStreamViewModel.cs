using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class CameraStreamViewModel : ObservableObject
    {
        public RelayCommand ConnectAllCamerasCommand { get; set; }
        public RelayCommand ToggleAllLightsCommand { get; set; }

        private CameraStreamModel _streamModel1;
        private CameraStreamModel _streamModel2;
        private CameraStreamModel _streamModel3;

        private BitmapImage _cameraFrame1;

        public BitmapImage CameraFrame1
        {
            get => _cameraFrame1;
            set => Set(ref _cameraFrame1, value);
        }

        private BitmapImage _cameraFrame2;

        public BitmapImage CameraFrame2
        {
            get => _cameraFrame2;
            set => Set(ref _cameraFrame2, value);
        }

        private BitmapImage _cameraFrame3;

        public BitmapImage CameraFrame3
        {
            get => _cameraFrame3;
            set => Set(ref _cameraFrame3, value);
        }

        private bool _areAllLightsOn;

        public bool AreAllLightsOn
        {
            get => _areAllLightsOn;
            set => Set(ref _areAllLightsOn, value);
        }

        public CameraStreamViewModel()
        {
            ConnectAllCamerasCommand = new RelayCommand(ConnectAllCameras);
            ToggleAllLightsCommand = new RelayCommand(ToggleAllLights);
            TakeAllCamerasScreenshotCommand = new RelayCommand(TakeCamerasScreenshot);
        }

        private async void ConnectAllCameras()
        {
            const int timeoutMs = 5000;

            Console.WriteLine("Connecting to cameras...");
            MainWindowViewModel.Instance.LogToControlBox("Connecting to cameras...");

            _streamModel1 = new CameraStreamModel("http://192.168.0.3:8082");
            _streamModel2 = new CameraStreamModel("http://192.168.0.3:8084");
            _streamModel3 = new CameraStreamModel("http://192.168.0.3:8083");

            bool frame1Received = false, frame2Received = false, frame3Received = false;

            _streamModel1.FrameReady += (frame) => { CameraFrame1 = frame; frame1Received = true; };
            _streamModel2.FrameReady += (frame) => { CameraFrame2 = frame; frame2Received = true; };
            _streamModel3.FrameReady += (frame) => { CameraFrame3 = frame; frame3Received = true; };

            await Task.Delay(timeoutMs);

            string msg1 = frame1Received ? "Camera 1: Connected." : "Camera 1: Timeout.";
            string msg2 = frame2Received ? "Camera 2: Connected." : "Camera 2: Timeout.";
            string msg3 = frame3Received ? "Camera 3: Connected." : "Camera 3: Timeout.";

            Console.WriteLine(msg1); MainWindowViewModel.Instance.LogToControlBox(msg1);
            Console.WriteLine(msg2); MainWindowViewModel.Instance.LogToControlBox(msg2);
            Console.WriteLine(msg3); MainWindowViewModel.Instance.LogToControlBox(msg3);

            // Trigger popup if any camera fails to connect within the timeout
            if (!frame1Received || !frame2Received || !frame3Received)
            {
                MessageBox.Show(
                    "One or more cameras timed out.\n\nDid you set the manual IP Address of the ethernet connection to 192.168.0.5?",
                    "Connection Timeout",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
        }

        private void ToggleAllLights()
        {
            AreAllLightsOn = !AreAllLightsOn;
            // TODO: send light state to hardware
        }

        public void Cleanup()
        {
            _streamModel1?.Dispose();
            _streamModel2?.Dispose();
            _streamModel3?.Dispose();
        }

        private string _screenshotFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "UStall_Runtime");
        private int _screenshotIndex = 0;

        public string ScreenshotFolder
        {
            get => _screenshotFolder;
            set => Set(ref _screenshotFolder, value);
        }

        public RelayCommand TakeAllCamerasScreenshotCommand { get; set; }

        private void TakeCamerasScreenshot()
        {
            try
            {
                Console.WriteLine(ScreenshotFolder);
                Directory.CreateDirectory(ScreenshotFolder);

                SaveFrame(CameraFrame1, $"S1_{_screenshotIndex:D3}.png");
                SaveFrame(CameraFrame2, $"S2_{_screenshotIndex:D3}.png");
                SaveFrame(CameraFrame3, $"S3_{_screenshotIndex:D3}.png");

                MainWindowViewModel.Instance.LogToControlBox(
                    $"Screenshots S1_{_screenshotIndex:D3}.png, S2_{_screenshotIndex:D3}.png, S3_{_screenshotIndex:D3}.png saved to '{ScreenshotFolder}' (Index: {_screenshotIndex:D3})"
                );

                _screenshotIndex++;
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance.LogToControlBox(
                    $"Screenshot error: {ex.Message}"
                );
            }
        }

        private void SaveFrame(BitmapImage frame, string fileName)
        {
            if (frame == null) return;

            string filePath = Path.Combine(ScreenshotFolder, fileName);

            // Save BitmapImage as PNG
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                encoder.Save(fileStream);
            }
        }
    }
}