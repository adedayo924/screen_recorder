using System.Runtime.InteropServices;
using Jeilo.Core.Recording;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jeilo.Core.Audio;

public sealed class AudioMixRecorder : IDisposable
{
    public const int OutputSampleRate = 48_000;
    public const int OutputChannels = 2;
    public const int OutputBitsPerSample = 16;

    private readonly WasapiCapture? _micCapture;
    private readonly WasapiLoopbackCapture? _systemCapture;
    private readonly BufferedWaveProvider? _micBuffer;
    private readonly BufferedWaveProvider? _systemBuffer;
    private readonly MixingSampleProvider _mixer;
    private readonly Stream _pcmOutput;
    private readonly WaveFileWriter? _mixedWav;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _peakLock = new();

    private Thread? _writerThread;
    private TimeSpan _duration;
    private float _micPeak;
    private float _systemPeak;
    private long _samplesWritten;
    private bool _disposed;

    public AudioMixRecorder(
        MMDevice? micDevice,
        MMDevice? renderDevice,
        Stream pcmOutput,
        string? mixedWavPath = null,
        float micGain = 1.0f,
        float systemGain = 1.0f)
    {
        if (micDevice is null && renderDevice is null)
        {
            throw new ArgumentException("At least one audio source is required.");
        }

        _pcmOutput = pcmOutput;
        var inputs = new List<ISampleProvider>(2);

        if (micDevice is not null)
        {
            _micCapture = new WasapiCapture(micDevice);
            _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _micCapture.DataAvailable += OnMicDataAvailable;
            inputs.Add(new VolumeSampleProvider(
                new WdlResamplingSampleProvider(ToStereo(_micBuffer.ToSampleProvider()), OutputSampleRate))
            {
                Volume = micGain
            });
        }

        if (renderDevice is not null)
        {
            _systemCapture = new WasapiLoopbackCapture(renderDevice);
            _systemBuffer = new BufferedWaveProvider(_systemCapture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _systemCapture.DataAvailable += OnSystemDataAvailable;
            inputs.Add(new VolumeSampleProvider(
                new WdlResamplingSampleProvider(ToStereo(_systemBuffer.ToSampleProvider()), OutputSampleRate))
            {
                Volume = systemGain
            });
        }

        _mixer = new MixingSampleProvider(inputs)
        {
            ReadFully = true
        };

        if (!string.IsNullOrWhiteSpace(mixedWavPath))
        {
            _mixedWav = new WaveFileWriter(
                mixedWavPath,
                new WaveFormat(OutputSampleRate, OutputBitsPerSample, OutputChannels));
        }
    }

    public long SamplesWritten => Volatile.Read(ref _samplesWritten);

    public void StartCaptures()
    {
        _micCapture?.StartRecording();
        _systemCapture?.StartRecording();

        Thread.Sleep(350);
        ResetBuffers();
    }

    public void ResetBuffers()
    {
        _micBuffer?.ClearBuffer();
        _systemBuffer?.ClearBuffer();
    }

    public void StartOutput(RecordingClock clock, TimeSpan duration)
    {
        _duration = duration;
        _writerThread = new Thread(() => WriterLoop(clock))
        {
            IsBackground = true,
            Name = "Jeilo audio writer",
            Priority = ThreadPriority.AboveNormal
        };
        _writerThread.Start();
    }

    public (float Mic, float System) ConsumePeaks()
    {
        lock (_peakLock)
        {
            var result = (_micPeak, _systemPeak);
            _micPeak = 0;
            _systemPeak = 0;
            return result;
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        _writerThread?.Join(TimeSpan.FromSeconds(5));
        _micCapture?.StopRecording();
        _systemCapture?.StopRecording();
    }

    private void WriterLoop(RecordingClock clock)
    {
        try
        {
            while (!_cts.IsCancellationRequested && !clock.WaitForStart(TimeSpan.FromMilliseconds(50)))
            {
            }

            if (_cts.IsCancellationRequested)
            {
                return;
            }

            var chunkFrames = OutputSampleRate / 50;
            var chunkSamples = chunkFrames * OutputChannels;
            var totalSamples = (long)(_duration.TotalSeconds * OutputSampleRate) * OutputChannels;
            var floatBuffer = new float[chunkSamples];
            var pcmBuffer = new byte[chunkSamples * sizeof(short)];
            long samplesWritten = 0;

            while (!_cts.IsCancellationRequested && samplesWritten < totalSamples)
            {
                var remaining = totalSamples - samplesWritten;
                var samplesToWrite = (int)Math.Min(chunkSamples, remaining);

                var targetElapsed = TimeSpan.FromSeconds((double)(samplesWritten + samplesToWrite) / (OutputSampleRate * OutputChannels));
                var wait = targetElapsed - clock.Elapsed;
                if (wait > TimeSpan.FromMilliseconds(1))
                {
                    Thread.Sleep(wait);
                }

                Array.Clear(floatBuffer, 0, samplesToWrite);
                _mixer.Read(floatBuffer, 0, samplesToWrite);

                for (var i = 0; i < samplesToWrite; i++)
                {
                    var sample = Math.Clamp(floatBuffer[i], -1.0f, 1.0f);
                    var value = (short)(sample * short.MaxValue);
                    pcmBuffer[i * 2] = (byte)(value & 0xFF);
                    pcmBuffer[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
                }

                var byteCount = samplesToWrite * sizeof(short);
                _pcmOutput.Write(pcmBuffer, 0, byteCount);
                _mixedWav?.Write(pcmBuffer, 0, byteCount);

                samplesWritten += samplesToWrite;
                Volatile.Write(ref _samplesWritten, samplesWritten);
            }

            _pcmOutput.Flush();
            _mixedWav?.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Audio writer stopped: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Audio writer failed: {ex}");
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_micBuffer is null || _micCapture is null)
        {
            return;
        }

        _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        UpdatePeak(_micCapture.WaveFormat, e.Buffer, e.BytesRecorded, isMic: true);
    }

    private void OnSystemDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_systemBuffer is null || _systemCapture is null)
        {
            return;
        }

        _systemBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        UpdatePeak(_systemCapture.WaveFormat, e.Buffer, e.BytesRecorded, isMic: false);
    }

    private void UpdatePeak(WaveFormat format, byte[] buffer, int bytesRecorded, bool isMic)
    {
        if (bytesRecorded <= 0)
        {
            return;
        }

        var peak = CalculatePeak(format, buffer, bytesRecorded);
        lock (_peakLock)
        {
            if (isMic)
            {
                _micPeak = Math.Max(_micPeak, peak);
            }
            else
            {
                _systemPeak = Math.Max(_systemPeak, peak);
            }
        }
    }

    private static float CalculatePeak(WaveFormat format, byte[] buffer, int bytesRecorded)
    {
        var peak = 0.0f;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRecorded));
            foreach (var sample in samples)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytesRecorded));
            foreach (var sample in samples)
            {
                peak = Math.Max(peak, Math.Abs(sample / (float)short.MaxValue));
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 24)
        {
            for (var i = 0; i + 2 < bytesRecorded; i += 3)
            {
                var value = (buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16)) << 8 >> 8;
                peak = Math.Max(peak, Math.Abs(value / 8388608.0f));
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
        {
            var samples = MemoryMarshal.Cast<byte, int>(buffer.AsSpan(0, bytesRecorded));
            foreach (var sample in samples)
            {
                peak = Math.Max(peak, Math.Abs(sample / (float)int.MaxValue));
            }
        }

        return Math.Clamp(peak, 0.0f, 1.0f);
    }

    private static ISampleProvider ToStereo(ISampleProvider source)
    {
        return source.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(source),
            2 => source,
            _ => throw new NotSupportedException(
                $"Audio source has {source.WaveFormat.Channels} channels. The Phase 0 spike supports mono or stereo only.")
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _writerThread?.Join(TimeSpan.FromSeconds(2));
        _micCapture?.Dispose();
        _systemCapture?.Dispose();
        _mixer.RemoveAllMixerInputs();
        _mixedWav?.Dispose();
        _cts.Dispose();
    }
}
