using System;
using System.IO;
using System.Windows.Media.Imaging;
using GalaSoft.MvvmLight;
using MjpegProcessor;

namespace UStallGUI.Model
{
    public class CameraStreamModel : ObservableObject
    {
        private readonly MjpegDecoder _njpegDecoder = new MjpegDecoder();

        public event Action<BitmapImage> FrameReady;

        public CameraStreamModel(string streamUrl)
        {
            _njpegDecoder.FrameReady += (sender, frameData) =>
            {
                var bitmap = ConvertToBitmapImage(frameData);
                FrameReady?.Invoke(bitmap); // Notify subscribers (like the ViewModel)
            };

            _njpegDecoder.ParseStream(new Uri(streamUrl));
        }

        private BitmapImage ConvertToBitmapImage(object frameData)
        {
            if (frameData is FrameReadyEventArgs args)
            {
                using var stream = new MemoryStream(args.FrameBuffer);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze(); // Safe for WPF binding across threads
                return bitmap;
            }

            throw new NotSupportedException("Unsupported frame data type");
        }

        public void Dispose() => _njpegDecoder.StopStream();
    }
}