using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System.Windows.Media.Imaging;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class CameraStreamViewModel : ObservableObject
    {
        public RelayCommand StartStream_Command { get; set; }

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

        public CameraStreamViewModel()
        {
            StartStream_Command = new RelayCommand(StartStream);
        }

        private void StartStream()
        {
            _streamModel1 = new CameraStreamModel("http://192.168.0.3:8082");
            _streamModel1.FrameReady += (frame) => CameraFrame1 = frame;

            _streamModel2 = new CameraStreamModel("http://192.168.0.3:8084");
            _streamModel2.FrameReady += (frame) => CameraFrame2 = frame;

            _streamModel3 = new CameraStreamModel("http://192.168.0.3:8083");
            _streamModel3.FrameReady += (frame) => CameraFrame3 = frame;
        }

        public void Cleanup()
        {
            _streamModel1?.Dispose();
            _streamModel2?.Dispose();
            _streamModel3?.Dispose();
        }
    }
}