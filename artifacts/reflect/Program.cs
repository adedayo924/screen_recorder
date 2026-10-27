using NAudio.CoreAudioApi;

using var enumerator = new MMDeviceEnumerator();
foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
{
    Console.WriteLine($"{device.FriendlyName}");
    Console.WriteLine($"  state={device.State}");
    try
    {
        var volume = device.AudioEndpointVolume;
        Console.WriteLine($"  mute={volume.Mute} volume={volume.MasterVolumeLevelScalar:0.000} " +
                          $"db={volume.MasterVolumeLevel:0.00}");
        Console.WriteLine($"  channels={volume.Channels}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  volume error: {ex.Message}");
    }

    try
    {
        var meter = device.AudioMeterInformation;
        Console.WriteLine($"  peak={meter.MasterPeakValue:0.00000}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  meter error: {ex.Message}");
    }

    using var client = device.AudioClient;
    Console.WriteLine($"  mix={client.MixFormat}");
    Console.WriteLine($"  latency={client.DefaultDevicePeriod}");
}

Console.WriteLine();
Console.WriteLine("Polling default capture peak for 10 seconds. Speak now.");
using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
var defaultMeter = defaultDevice.AudioMeterInformation;
for (var i = 0; i < 100; i++)
{
    Console.WriteLine($"{i / 10.0:0.0}s peak={defaultMeter.MasterPeakValue:0.00000}");
    Thread.Sleep(100);
}
