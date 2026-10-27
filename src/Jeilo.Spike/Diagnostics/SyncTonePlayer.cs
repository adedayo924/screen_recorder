using System.Media;
using System.Text;
using Jeilo.Core.Recording;

namespace Jeilo.Spike.Diagnostics;

public sealed class SyncTonePlayer : IDisposable
{
    private readonly MemoryStream _wavStream;
    private readonly SoundPlayer _player;
    private Thread? _thread;
    private CancellationTokenSource? _cts;

    public SyncTonePlayer()
    {
        _wavStream = CreateToneWav(48_000, 4_000, 140, 0.35);
        _player = new SoundPlayer(_wavStream);
        _player.Load();
    }

    public void Start(RecordingClock clock)
    {
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(clock, _cts.Token))
        {
            IsBackground = true,
            Name = "Jeilo sync tone"
        };
        _thread.Start();
    }

    private void Run(RecordingClock clock, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               !clock.WaitForStart(TimeSpan.FromMilliseconds(50)))
        {
        }

        var nextMarker = 1.0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var elapsed = clock.Elapsed.TotalSeconds;
            if (elapsed >= nextMarker)
            {
                try
                {
                    _player.Play();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Sync tone failed: {ex.Message}");
                }

                nextMarker += clock.SyncIntervalSeconds;
            }

            Thread.Sleep(10);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _player.Stop();
    }

    private static MemoryStream CreateToneWav(int sampleRate, double frequency, int durationMs, double gain)
    {
        var sampleCount = sampleRate * durationMs / 1000;
        var dataSize = sampleCount * sizeof(short);
        var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRate;
            var envelope = Math.Min(1.0, Math.Min(i / (sampleRate * 0.01), (sampleCount - i) / (sampleRate * 0.01)));
            var value = Math.Sin(2 * Math.PI * frequency * t) * gain * envelope;
            writer.Write((short)(value * short.MaxValue));
        }

        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _player.Dispose();
        _wavStream.Dispose();
    }
}

