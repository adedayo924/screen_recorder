using NAudio.CoreAudioApi;

namespace Jeilo.Core.Audio;

public sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    DataFlow Flow,
    bool IsDefaultConsole,
    bool IsDefaultCommunications,
    string MixFormat,
    int SampleRate,
    int Channels,
    int BitsPerSample);

public static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioEndpointInfo> Enumerate(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultConsoleId = TryGetDefaultId(enumerator, flow, Role.Console);
        var defaultCommunicationsId = TryGetDefaultId(enumerator, flow, Role.Communications);

        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var results = new List<AudioEndpointInfo>(devices.Count);

        foreach (var device in devices)
        {
            var mixFormat = TryGetMixFormat(device);
            results.Add(new AudioEndpointInfo(
                device.ID,
                device.FriendlyName,
                flow,
                string.Equals(device.ID, defaultConsoleId, StringComparison.OrdinalIgnoreCase),
                string.Equals(device.ID, defaultCommunicationsId, StringComparison.OrdinalIgnoreCase),
                mixFormat?.ToString() ?? "<unavailable>",
                mixFormat?.SampleRate ?? 0,
                mixFormat?.Channels ?? 0,
                mixFormat?.BitsPerSample ?? 0));
        }

        return results;
    }

    public static MMDevice GetDefault(DataFlow flow, Role role)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(flow, role);
    }

    public static MMDevice Find(DataFlow flow, string query)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);

        if (int.TryParse(query, out var index) && index >= 0 && index < devices.Count)
        {
            return devices[index];
        }

        foreach (var device in devices)
        {
            if (device.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                device.ID.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        var available = string.Join(Environment.NewLine, devices.Select((d, i) => $"  [{i}] {d.FriendlyName}"));
        throw new InvalidOperationException(
            $"No {flow} audio device matched '{query}'.{Environment.NewLine}Available devices:{Environment.NewLine}{available}");
    }

    public static void Print(TextWriter writer)
    {
        writer.WriteLine("Capture endpoints:");
        PrintFlow(writer, DataFlow.Capture);

        writer.WriteLine();
        writer.WriteLine("Render endpoints:");
        PrintFlow(writer, DataFlow.Render);
    }

    private static void PrintFlow(TextWriter writer, DataFlow flow)
    {
        var endpoints = Enumerate(flow);
        if (endpoints.Count == 0)
        {
            writer.WriteLine("  <none>");
            return;
        }

        for (var i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];
            var defaults = new List<string>();
            if (endpoint.IsDefaultConsole) defaults.Add("default");
            if (endpoint.IsDefaultCommunications) defaults.Add("communications");
            var suffix = defaults.Count > 0 ? $" [{string.Join(", ", defaults)}]" : string.Empty;

            writer.WriteLine($"  [{i}] {endpoint.FriendlyName}{suffix}");
            writer.WriteLine($"      id: {endpoint.Id}");
            writer.WriteLine($"      format: {endpoint.MixFormat}");
        }
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    private static NAudio.Wave.WaveFormat? TryGetMixFormat(MMDevice device)
    {
        try
        {
            using var client = device.AudioClient;
            return client.MixFormat;
        }
        catch
        {
            return null;
        }
    }
}
