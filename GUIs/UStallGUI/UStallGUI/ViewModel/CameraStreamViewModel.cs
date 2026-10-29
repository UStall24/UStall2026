using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using MjpegProcessor;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using SharpCompress.Archives.SevenZip;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class CameraStreamViewModel : ObservableObject
    {
        public RelayCommand TurnCamera1Command { get; set; }
        public RelayCommand TurnCamera2Command { get; set; }
        public RelayCommand TurnCamera3Command { get; set; }
        public RelayCommand ConnectAllCamerasCommand { get; set; }
        public RelayCommand ToggleAllLightsCommand { get; set; } // Not implemented in hardware yet. This can be continued when the custom Raspberry Pi Zero boards with the light control and the cameras are ready.
        public RelayCommand TakeAllCamerasScreenshotCommand { get; set; }
        public RelayCommand ToggleRecordingCommand { get; set; }

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

        // --- Recording Properties & Fields ---
        private bool _isAutoRecordEnabled;

        public bool IsAutoRecordEnabled
        {
            get => _isAutoRecordEnabled;
            set
            {
                if (_isAutoRecordEnabled == value) return;
                Set(ref _isAutoRecordEnabled, value);
                if (value)
                {
                    _ = HandleAutoRecordTurnedOnAsync();
                }
            }
        }

        private bool _isRecording;

        public bool IsRecording
        {
            get => _isRecording;
            set => Set(ref _isRecording, value);
        }

        private double _clipDurationMinutes = 2.0; // This value determines how much the videostream should be split into smaller clips. The default is 2 minutes, but it can be adjusted here or in the GUI.

        public double ClipDurationMinutes
        {
            get => _clipDurationMinutes;
            set => Set(ref _clipDurationMinutes, value <= 0 ? 1.0 : value);
        }

        private bool _isInstallingFfmpeg;
        private readonly Process[] _ffmpegProcesses = new Process[3];
        private readonly object[] _processLocks = { new object(), new object(), new object() };
        private readonly DateTime[] _lastFrameTimes = { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
        private readonly DispatcherTimer _watchdogTimer;

        private string FfmpegPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

        public CameraStreamViewModel()
        {
            TurnCamera1Command = new RelayCommand(TurnCamera1);
            TurnCamera2Command = new RelayCommand(TurnCamera2);
            TurnCamera3Command = new RelayCommand(TurnCamera3);

            ConnectAllCamerasCommand = new RelayCommand(ConnectAllCameras);
            ToggleAllLightsCommand = new RelayCommand(ToggleAllLights);
            TakeAllCamerasScreenshotCommand = new RelayCommand(TakeCamerasScreenshot);
            ToggleRecordingCommand = new RelayCommand(async () => await ToggleRecordingManualAsync());

            // Watchdog Timer überwacht aktive Streams und Verbindungsabbrüche (alle 1 Sekunde)
            _watchdogTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _watchdogTimer.Tick += WatchdogTimer_Tick;
            _watchdogTimer.Start();
        }

        // Prüft, ob mindestens eine Kamera in den letzten 3 Sekunden ein Frame geliefert hat
        private bool IsCameraReceivingFrames(int index) =>
            (DateTime.UtcNow - _lastFrameTimes[index]).TotalSeconds < 3.0;

        private bool IsAnyCameraConnected() =>
            IsCameraReceivingFrames(0) || IsCameraReceivingFrames(1) || IsCameraReceivingFrames(2);

        private void WatchdogTimer_Tick(object sender, EventArgs e)
        {
            bool anyConnected = IsAnyCameraConnected();

            if (IsRecording)
            {
                if (!anyConnected)
                {
                    // Alle 3 Streams sind abgebrochen (z.B. ROV Ethernet getrennt)
                    MainWindowViewModel.Instance.LogToControlBox("Connection to all cameras lost. Stopping recording.");
                    StopRecordingInternal();
                }
                else
                {
                    // Falls eine Kamera nachträglich online kommt während bereits aufgenommen wird
                    for (int i = 0; i < 3; i++)
                    {
                        if (IsCameraReceivingFrames(i) && _ffmpegProcesses[i] == null)
                        {
                            StartFfmpegForCamera(i);
                        }
                    }
                }
            }
            else if (IsAutoRecordEnabled && anyConnected && !_isInstallingFfmpeg && File.Exists(FfmpegPath))
            {
                // Auto-Record ist aktiv und mindestens ein Videofeed kommt rein
                MainWindowViewModel.Instance.LogToControlBox("Video feed detected (Auto-Record active). Starting recording...");
                StartRecordingInternal();
            }
        }

        private async Task HandleAutoRecordTurnedOnAsync()
        {
            bool ffmpegReady = await EnsureFfmpegInstalledAsync();
            if (!ffmpegReady)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _isAutoRecordEnabled = false;
                    RaisePropertyChanged(nameof(IsAutoRecordEnabled));
                });
                return;
            }

            if (IsAnyCameraConnected() && !IsRecording)
            {
                StartRecordingInternal();
            }
        }

        private async Task ToggleRecordingManualAsync()
        {
            if (IsRecording)
            {
                // Wenn manuell gestoppt wird, während Auto-Record an ist, Auto-Record ausschalten,
                // damit die Aufnahme nicht in der nächsten Sekunde sofort wieder anspringt.
                if (IsAutoRecordEnabled)
                {
                    _isAutoRecordEnabled = false;
                    RaisePropertyChanged(nameof(IsAutoRecordEnabled));
                    MainWindowViewModel.Instance.LogToControlBox("Auto-Record disabled due to manual stop.");
                }

                StopRecordingInternal();
                return;
            }

            // 1. FFmpeg prüfen / installieren
            bool ffmpegReady = await EnsureFfmpegInstalledAsync();
            if (!ffmpegReady)
                return;

            // 2. Prüfen ob Kameras überhaupt verbunden sind
            if (!IsAnyCameraConnected())
            {
                MessageBox.Show(
                    "Keine Kamera ist aktuell verbunden oder liefert Bilder.\n\nBitte zuerst die Kameras über 'Connect All Cameras' verbinden.",
                    "Kameras nicht angeschlossen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            // 3. Aufnahme starten
            StartRecordingInternal();
        }

        private async Task<bool> EnsureFfmpegInstalledAsync()
        {
            if (File.Exists(FfmpegPath))
            {
                Console.WriteLine($"[FFmpeg Check] Found existing ffmpeg.exe at: {FfmpegPath}");
                return true;
            }

            if (_isInstallingFfmpeg)
            {
                Console.WriteLine("[FFmpeg Check] Installation is already in progress. Ignoring request.");
                return false;
            }

            Console.WriteLine($"[FFmpeg Check] ffmpeg.exe not found at '{FfmpegPath}'. Prompting user for installation...");

            var result = MessageBox.Show(
                "FFmpeg wurde nicht gefunden, wird aber für die Videoaufnahme benötigt.\n\nSoll FFmpeg jetzt automatisch heruntergeladen (~34 MB) und eingerichtet werden?",
                "FFmpeg fehlt",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (result != MessageBoxResult.Yes)
            {
                Console.WriteLine("[FFmpeg Install] User declined FFmpeg installation.");
                return false;
            }

            _isInstallingFfmpeg = true;
            string archivePath = Path.Combine(Path.GetTempPath(), "ffmpeg_ustall_temp.7z");
            string downloadUrl = "https://github.com/GyanD/codexffmpeg/releases/download/2026-10-29-git-31ba826726/ffmpeg-2026-10-29-git-31ba826726-essentials_build.7z";

            try
            {
                Console.WriteLine("==================================================");
                Console.WriteLine("[FFmpeg Install] Starting FFmpeg (.7z) installation...");
                Console.WriteLine($"[FFmpeg Install] Download URL : {downloadUrl}");
                Console.WriteLine($"[FFmpeg Install] Temp 7z Path : {archivePath}");
                Console.WriteLine($"[FFmpeg Install] Target Path  : {FfmpegPath}");
                Console.WriteLine("==================================================");

                MainWindowViewModel.Instance.LogToControlBox("Downloading FFmpeg (.7z)... (0%)");

                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    Console.WriteLine("[FFmpeg Download] Sending HTTP GET request...");
                    using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    Console.WriteLine($"[FFmpeg Download] Server responded with status: {(int)response.StatusCode} ({response.StatusCode})");
                    response.EnsureSuccessStatusCode();

                    long? totalBytes = response.Content.Headers.ContentLength;
                    if (totalBytes.HasValue)
                    {
                        Console.WriteLine($"[FFmpeg Download] Total file size: {totalBytes.Value / (1024.0 * 1024.0):F2} MB ({totalBytes.Value:N0} bytes)");
                    }
                    else
                    {
                        Console.WriteLine("[FFmpeg Download] Warning: Content-Length header not provided by server.");
                    }

                    using var contentStream = await response.Content.ReadAsStreamAsync();
                    using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

                    byte[] buffer = new byte[65536]; // 64 KB Puffer
                    long totalRead = 0;
                    int bytesRead;
                    int lastReportedStep = 0;
                    long lastLoggedBytes = 0;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            int currentPercentage = (int)((totalRead * 100) / totalBytes.Value);
                            int currentStep = (currentPercentage / 5) * 5; // 5%-Schritte

                            if (currentStep > lastReportedStep && currentStep <= 100)
                            {
                                lastReportedStep = currentStep;
                                double downloadedMb = totalRead / (1024.0 * 1024.0);
                                double totalMb = totalBytes.Value / (1024.0 * 1024.0);

                                string progressMsg = $"Downloading FFmpeg: {currentStep}% ({downloadedMb:F1} MB / {totalMb:F1} MB)";
                                Console.WriteLine($"[FFmpeg Download] {progressMsg}");
                                MainWindowViewModel.Instance.LogToControlBox(progressMsg);
                            }
                        }
                        else
                        {
                            if (totalRead - lastLoggedBytes >= 5 * 1024 * 1024)
                            {
                                lastLoggedBytes = totalRead;
                                double downloadedMb = totalRead / (1024.0 * 1024.0);
                                Console.WriteLine($"[FFmpeg Download] Downloaded {downloadedMb:F1} MB (total size unknown)...");
                                MainWindowViewModel.Instance.LogToControlBox($"Downloading FFmpeg: {downloadedMb:F1} MB...");
                            }
                        }
                    }

                    Console.WriteLine($"[FFmpeg Download] Download finished! Total bytes written: {totalRead:N0}");
                }

                Console.WriteLine("[FFmpeg Extract] Opening 7z archive...");
                MainWindowViewModel.Instance.LogToControlBox("Extracting ffmpeg.exe from 7z (this may take a few seconds)...");

                await Task.Run(() =>
                {
                    using var archive = SevenZipArchive.OpenArchive(archivePath);
                    Console.WriteLine($"[FFmpeg Extract] 7z archive contains {archive.Entries.Count()} entries. Searching for 'ffmpeg.exe'...");

                    var ffmpegEntry = archive.Entries.FirstOrDefault(e =>
                        !e.IsDirectory && Path.GetFileName(e.Key).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));

                    if (ffmpegEntry == null)
                    {
                        Console.WriteLine("[FFmpeg Extract] ERROR: 'ffmpeg.exe' not found inside the 7z archive!");
                        throw new FileNotFoundException("ffmpeg.exe could not be found inside the downloaded 7z archive.");
                    }

                    Console.WriteLine($"[FFmpeg Extract] Found entry: '{ffmpegEntry.Key}'");
                    Console.WriteLine($"[FFmpeg Extract] Uncompressed size: {ffmpegEntry.Size / (1024.0 * 1024.0):F2} MB ({ffmpegEntry.Size:N0} bytes)");
                    Console.WriteLine("[FFmpeg Extract] Decompressing LZMA2 solid block and extracting ffmpeg.exe...");

                    using Stream entryStream = ffmpegEntry.OpenEntryStream();
                    using FileStream outputStream = new FileStream(FfmpegPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);

                    byte[] buffer = new byte[65536];
                    long totalExtracted = 0;
                    long totalSize = ffmpegEntry.Size;
                    int bytesRead;
                    int lastExtractStep = 0;

                    while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        outputStream.Write(buffer, 0, bytesRead);
                        totalExtracted += bytesRead;

                        if (totalSize > 0)
                        {
                            int currentPercentage = (int)((totalExtracted * 100) / totalSize);
                            int currentStep = (currentPercentage / 5) * 5;

                            if (currentStep > lastExtractStep && currentStep <= 100)
                            {
                                lastExtractStep = currentStep;
                                double extractedMb = totalExtracted / (1024.0 * 1024.0);
                                double totalMb = totalSize / (1024.0 * 1024.0);

                                Console.WriteLine($"[FFmpeg Extract] Extracting ffmpeg.exe: {currentStep}% ({extractedMb:F1} MB / {totalMb:F1} MB)");

                                Application.Current?.Dispatcher.Invoke(() =>
                                {
                                    MainWindowViewModel.Instance.LogToControlBox($"Extracting FFmpeg: {currentStep}%");
                                });
                            }
                        }
                    }

                    Console.WriteLine($"[FFmpeg Extract] Successfully extracted to '{FfmpegPath}'!");
                });

                Console.WriteLine("[FFmpeg Install] Installation completed successfully!");
                MainWindowViewModel.Instance.LogToControlBox("FFmpeg successfully installed!");
                MessageBox.Show("FFmpeg wurde erfolgreich installiert!", "Installation abgeschlossen", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("==================================================");
                Console.WriteLine($"[FFmpeg Install] CRITICAL ERROR: {ex.GetType().Name}");
                Console.WriteLine($"[FFmpeg Install] Message: {ex.Message}");
                Console.WriteLine($"[FFmpeg Install] StackTrace:\n{ex.StackTrace}");
                Console.WriteLine("==================================================");

                MainWindowViewModel.Instance.LogToControlBox($"FFmpeg install error: {ex.Message}");
                MessageBox.Show($"Fehler bei der Installation von FFmpeg:\n{ex.Message}", "Installationsfehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                if (File.Exists(archivePath))
                {
                    try
                    {
                        Console.WriteLine($"[FFmpeg Cleanup] Deleting temporary file: {archivePath}");
                        File.Delete(archivePath);
                        Console.WriteLine("[FFmpeg Cleanup] Temporary file deleted successfully.");
                    }
                    catch (Exception cleanupEx)
                    {
                        Console.WriteLine($"[FFmpeg Cleanup] Failed to delete temp file: {cleanupEx.Message}");
                    }
                }
                _isInstallingFfmpeg = false;
            }
        }

        private void StartRecordingInternal()
        {
            if (IsRecording) return;

            try
            {
                Directory.CreateDirectory(ScreenshotFolder);
                IsRecording = true;

                for (int i = 0; i < 3; i++)
                {
                    if (IsCameraReceivingFrames(i))
                    {
                        StartFfmpegForCamera(i);
                    }
                }

                MainWindowViewModel.Instance.LogToControlBox($"Video recording started (Split every {ClipDurationMinutes} min).");
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance.LogToControlBox($"Failed to start recording: {ex.Message}");
                IsRecording = false;
            }
        }

        private void StartFfmpegForCamera(int camIndex)
        {
            lock (_processLocks[camIndex])
            {
                if (_ffmpegProcesses[camIndex] != null)
                    return;

                int segmentSeconds = Math.Max(5, (int)Math.Round(ClipDurationMinutes * 60));
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string outputPattern = Path.Combine(ScreenshotFolder, $"Cam{camIndex + 1}_{timestamp}_part%03d.mp4");

                // Liest MJPEG-Frames über StandardInput (pipe:0), encodiert H.264 und splittet nahtlos nach segmentSeconds
                string args = $"-y -f mjpeg -i pipe:0 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -f segment -segment_time {segmentSeconds} -reset_timestamps 1 \"{outputPattern}\"";

                var startInfo = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true
                };

                var proc = new Process { StartInfo = startInfo };
                proc.ErrorDataReceived += (s, e) => { /* Verhindert Buffer-Deadlock */ };
                proc.Start();
                proc.BeginErrorReadLine();

                _ffmpegProcesses[camIndex] = proc;
                MainWindowViewModel.Instance.LogToControlBox($"Camera {camIndex + 1}: Recording stream active.");
            }
        }

        private void StopRecordingInternal()
        {
            if (!IsRecording) return;
            IsRecording = false;

            Task.Run(() =>
            {
                for (int i = 0; i < 3; i++)
                {
                    StopFfmpegForCamera(i);
                }
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    MainWindowViewModel.Instance.LogToControlBox("Video recording stopped and saved.");
                });
            });
        }

        private void StopFfmpegForCamera(int camIndex)
        {
            Process procToStop = null;
            lock (_processLocks[camIndex])
            {
                procToStop = _ffmpegProcesses[camIndex];
                _ffmpegProcesses[camIndex] = null;
            }

            if (procToStop != null)
            {
                try
                {
                    // Schließen des Input-Streams signalisiert FFmpeg EOF, sodass der MP4-Header sauber finalisiert wird
                    procToStop.StandardInput.BaseStream.Close();
                    if (!procToStop.WaitForExit(3000))
                    {
                        procToStop.Kill();
                    }
                }
                catch { }
                finally
                {
                    procToStop.Dispose();
                }
            }
        }

        // Nutzt den bereits vorhandenen MjpegDecoder in CameraStreamModel ohne CameraStreamModel.cs ändern zu müssen
        private void AttachRawFrameTap(CameraStreamModel model, int camIndex)
        {
            var field = typeof(CameraStreamModel).GetField("_njpegDecoder", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(model) is MjpegDecoder decoder)
            {
                decoder.FrameReady += (sender, e) =>
                {
                    _lastFrameTimes[camIndex] = DateTime.UtcNow;

                    if (!IsRecording || e.FrameBuffer == null)
                        return;

                    lock (_processLocks[camIndex])
                    {
                        var proc = _ffmpegProcesses[camIndex];
                        if (proc != null && !proc.HasExited)
                        {
                            try
                            {
                                proc.StandardInput.BaseStream.Write(e.FrameBuffer, 0, e.FrameBuffer.Length);
                                proc.StandardInput.BaseStream.Flush();
                            }
                            catch
                            {
                                // Falls FFmpeg gerade ein Segment abschließt oder beendet wird
                            }
                        }
                    }
                };
            }
        }

        private async void ConnectAllCameras()
        {
            const int timeoutMs = 5000;

            Console.WriteLine("Connecting to cameras...");
            MainWindowViewModel.Instance.LogToControlBox("Connecting to cameras...");

            _streamModel1?.Dispose();
            _streamModel2?.Dispose();
            _streamModel3?.Dispose();

            _streamModel1 = new CameraStreamModel("http://192.168.0.3:8082");
            _streamModel2 = new CameraStreamModel("http://192.168.0.3:8084");
            _streamModel3 = new CameraStreamModel("http://192.168.0.3:8083");

            AttachRawFrameTap(_streamModel1, 0);
            AttachRawFrameTap(_streamModel2, 1);
            AttachRawFrameTap(_streamModel3, 2);

            bool frame1Received = false, frame2Received = false, frame3Received = false;

            _streamModel1.FrameReady += (frame) => { _lastFrameTimes[0] = DateTime.UtcNow; CameraFrame1 = frame; frame1Received = true; };
            _streamModel2.FrameReady += (frame) => { _lastFrameTimes[1] = DateTime.UtcNow; CameraFrame2 = frame; frame2Received = true; };
            _streamModel3.FrameReady += (frame) => { _lastFrameTimes[2] = DateTime.UtcNow; CameraFrame3 = frame; frame3Received = true; };

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
            _watchdogTimer?.Stop();
            StopRecordingInternal();
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

        // Camera Rotations
        private int _rotation1;

        public int Rotation1
        {
            get => _rotation1;
            set => Set(ref _rotation1, value);
        }

        private int _rotation2;

        public int Rotation2
        {
            get => _rotation2;
            set => Set(ref _rotation2, value);
        }

        private int _rotation3;

        public int Rotation3
        {
            get => _rotation3;
            set => Set(ref _rotation3, value);
        }

        private void TurnCamera1()
        {
            Rotation1 = (Rotation1 + 90) % 360;
        }

        private void TurnCamera2()
        {
            Rotation2 = (Rotation2 + 90) % 360;
        }

        private void TurnCamera3()
        {
            Rotation3 = (Rotation3 + 90) % 360;
        }
    }
}