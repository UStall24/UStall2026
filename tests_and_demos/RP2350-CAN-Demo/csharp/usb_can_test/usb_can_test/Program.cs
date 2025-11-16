using System.Reflection.Metadata.Ecma335;
using System.Text;
using usb_can_test;

// Cancellation setup
var cts = new CancellationTokenSource();
var token = cts.Token;

Console.CancelKeyPress += (s, e) =>
{
    Console.WriteLine("Stopping...");
    cts.Cancel();
    e.Cancel = true;
};

var usbCAN = new UsbCANWrapper
{
    DebugMode = false
};

//Thread senderThread = new Thread(() =>
//{
//    int counter = 0;
//    try
//    {
//        while (!token.IsCancellationRequested)
//        {
//            string messageToSend = counter.ToString("0000");
//            byte[] data = Encoding.ASCII.GetBytes(messageToSend);

//            usbCAN.SendCanFrame(0x123, data);
//            Console.WriteLine($"[TX] {DateTime.Now:HH:mm:ss} ID 0x123 Data: {messageToSend}");
//            counter++;

//            Thread.Sleep(500); // 10 × 100 ms = 1 s
//        }
//    }
//    catch (Exception ex)
//    {
//        Console.WriteLine($"Sender thread error: {ex.Message}");
//    }
//});

//senderThread.IsBackground = true;
//senderThread.Start();

//// --- RECEIVER THREAD: poll + print incoming frames ---

//Thread receiverThread = new Thread(() =>
//{
//    while (!token.IsCancellationRequested)
//    {
//        // Example: only messages with ID 0x123
//        byte[] msg = usbCAN.GetFromBuffer(0x123, delete: true);

//        if (msg != null && msg.Length > 0)
//        {
//            string text = Encoding.ASCII.GetString(msg);
//            Console.WriteLine($"[RX] {DateTime.Now:HH:mm:ss} ID 0x123 Data: {text} ({BitConverter.ToString(msg)})");
//        }

//        Thread.Sleep(1000);
//    }
//});

//receiverThread.IsBackground = true;
//receiverThread.Start();

// --- MAIN LOOP: wait until Ctrl+C ---
async Task RunAtFixedRate(Func<Task> action, int intervalMs, CancellationToken token)
{
    long intervalTicks = TimeSpan.TicksPerMillisecond * intervalMs;
    long nextRun = DateTime.UtcNow.Ticks;

    while (!token.IsCancellationRequested)
    {
        long now = DateTime.UtcNow.Ticks;

        // Run your action
        await action();

        // Schedule next execution
        nextRun += intervalTicks;

        long delayTicks = nextRun - DateTime.UtcNow.Ticks;
        if (delayTicks > 0)
        {
            int delayMs = (int)(delayTicks / TimeSpan.TicksPerMillisecond);
            await Task.Delay(delayMs, token);
        }
        else
        {
            // We are late → skip sleep and catch up
            nextRun = DateTime.UtcNow.Ticks;
        }
    }
}

void PrintCanData(ushort rxId, byte[] data)
{
    string text = Encoding.ASCII.GetString(data);
    Console.WriteLine($"Pulled CAN data: ID 0x{rxId:X3} Data: {text}");
}

usbCAN.RegisterCallback(0x123, PrintCanData);

Console.WriteLine("Waiting for threads to finish...");

async Task SendTestData()
{
    long epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    string eightDigits = epochMs.ToString().Substring(epochMs.ToString().Length - 8);
    byte[] payload = Encoding.ASCII.GetBytes(eightDigits);

    usbCAN.SendFrame(0x101, payload);
    Console.WriteLine($"Sending -> {eightDigits}");
}

RunAtFixedRate(SendTestData, 500, token);

while (!token.IsCancellationRequested)
{
    await Task.Delay(100);
}

if (usbCAN is IDisposable d)
    d.Dispose();

Console.WriteLine("Exited cleanly.");