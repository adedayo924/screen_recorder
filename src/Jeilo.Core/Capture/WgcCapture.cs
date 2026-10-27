using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Jeilo.Core.Recording;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Jeilo.Core.Capture;

public sealed class WgcCapture : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly int _fps;
    private readonly bool _includeCursor;
    private readonly FramePool _frames;
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private readonly Stopwatch _captureClock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IDirect3DDevice? _direct3DDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private Thread? _writerThread;
    private Stream? _videoOutput;
    private RecordingClock? _clock;
    private TimeSpan _duration;
    private long _lastCaptureTicks;
    private long _framesCaptured;
    private long _framesDropped;
    private long _framesWritten;
    private int _width;
    private int _height;
    private bool _disposed;

    public WgcCapture(GraphicsCaptureItem item, int fps, bool includeCursor = true)
    {
        _item = item;
        _fps = fps;
        _includeCursor = includeCursor;
        _width = item.Size.Width;
        _height = item.Size.Height;

        if (_width <= 0 || _height <= 0)
        {
            throw new InvalidOperationException($"Capture item has invalid size {_width}x{_height}.");
        }

        _frames = new FramePool(_width * _height * 4, bufferCount: 6, readyCapacity: 3);
    }

    public int Width => _width;

    public int Height => _height;

    public string SourceName => _item.DisplayName;

    public void Start()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        _device = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels);
        _context = _device.ImmediateContext;

        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        {
            var hr = NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            _direct3DDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            3,
            _item.Size);

        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _session.IsCursorCaptureEnabled = _includeCursor;
        }

        _session.StartCapture();
    }

    public long FramesCaptured => Volatile.Read(ref _framesCaptured);

    public long FramesDropped => Volatile.Read(ref _framesDropped);

    public long FramesWritten => Volatile.Read(ref _framesWritten);

    public bool WaitForFirstFrame(TimeSpan timeout) => _firstFrame.Wait(timeout);

    public void StartOutput(RecordingClock clock, Stream output, TimeSpan duration)
    {
        lock (_stateLock)
        {
            if (_writerThread is not null)
            {
                throw new InvalidOperationException("Video output has already been started.");
            }

            _clock = clock;
            _videoOutput = output;
            _duration = duration;
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "Jeilo video writer",
                Priority = ThreadPriority.AboveNormal
            };
            _writerThread.Start();
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            _firstFrame.Set();

            var now = _captureClock.ElapsedTicks;
            var interval = Stopwatch.Frequency / _fps;
            if (now - Interlocked.Read(ref _lastCaptureTicks) < interval)
            {
                return;
            }

            Interlocked.Exchange(ref _lastCaptureTicks, now);

            var contentSize = frame.ContentSize;
            if (contentSize.Width != _width || contentSize.Height != _height)
            {
                throw new InvalidOperationException(
                    $"Capture size changed from {_width}x{_height} to {contentSize.Width}x{contentSize.Height}. " +
                    "The Phase 0 spike does not support resizing during a recording.");
            }

            var access = WinRT.CastExtensions.As<WgcInterop.IDirect3DDxgiInterfaceAccess>(frame.Surface);
            var texturePointer = access.GetInterface(WgcInterop.IidD3D11Texture2D);
            using var texture = new ID3D11Texture2D(texturePointer);

            EnsureStaging();
            _context!.CopyResource(_staging!, texture);

            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                if (_frames.TryWrite(mapped.DataPointer, checked((int)mapped.RowPitch), _width, _height))
                {
                    Interlocked.Increment(ref _framesCaptured);
                }
                else
                {
                    Interlocked.Increment(ref _framesDropped);
                }
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WGC frame error: {ex}");
        }
    }

    private void EnsureStaging()
    {
        if (_staging is not null)
        {
            return;
        }

        var description = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        _staging = _device!.CreateTexture2D(description);
    }

    private void WriterLoop()
    {
        try
        {
            var clock = _clock ?? throw new InvalidOperationException("Recording clock was not set.");
            while (!_cts.IsCancellationRequested && !clock.WaitForStart(TimeSpan.FromMilliseconds(50)))
            {
            }

            if (_cts.IsCancellationRequested)
            {
                return;
            }

            var totalFrames = (long)Math.Round(_duration.TotalSeconds * _fps);
            var frameBytes = _width * _height * 4;

            for (long frameIndex = 0; frameIndex < totalFrames && !_cts.IsCancellationRequested; frameIndex++)
            {
                var target = TimeSpan.FromSeconds((double)(frameIndex + 1) / _fps);
                var wait = target - clock.Elapsed;
                if (wait > TimeSpan.FromMilliseconds(1))
                {
                    Thread.Sleep(wait);
                }

                _frames.TryTake(out var buffer);
                if (buffer is null)
                {
                    Thread.Sleep(1);
                    frameIndex--;
                    continue;
                }

                _videoOutput!.Write(buffer, 0, frameBytes);
                Interlocked.Increment(ref _framesWritten);
            }

            _videoOutput!.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Video writer stopped: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Video writer failed: {ex}");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        _writerThread?.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _session?.Dispose();
        _framePool?.Dispose();
        _direct3DDevice?.Dispose();
        _staging?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _firstFrame.Dispose();
        _cts.Dispose();
    }

    private static class NativeMethods
    {
        [DllImport("d3d11.dll", ExactSpelling = true)]
        public static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice,
            out IntPtr graphicsDevice);
    }

    private sealed class FramePool
    {
        private readonly ConcurrentBag<byte[]> _free = new();
        private readonly Queue<byte[]> _ready = new();
        private readonly object _lock = new();
        private readonly int _frameBytes;
        private readonly int _readyCapacity;
        private byte[]? _last;

        public FramePool(int frameBytes, int bufferCount, int readyCapacity)
        {
            _frameBytes = frameBytes;
            _readyCapacity = readyCapacity;

            for (var i = 0; i < bufferCount; i++)
            {
                _free.Add(new byte[frameBytes]);
            }
        }

        public unsafe bool TryWrite(IntPtr source, int sourceStride, int width, int height)
        {
            if (!_free.TryTake(out var buffer))
            {
                return false;
            }

            var rowBytes = width * 4;
            fixed (byte* destination = buffer)
            {
                var sourceBytes = (byte*)source;
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        sourceBytes + (long)y * sourceStride,
                        destination + (long)y * rowBytes,
                        rowBytes,
                        rowBytes);
                }
            }

            lock (_lock)
            {
                if (_ready.Count >= _readyCapacity)
                {
                    _free.Add(_ready.Dequeue());
                }

                _ready.Enqueue(buffer);
            }

            return true;
        }

        public bool TryTake(out byte[]? buffer)
        {
            lock (_lock)
            {
                if (_ready.Count > 0)
                {
                    var next = _ready.Dequeue();
                    if (_last is not null)
                    {
                        _free.Add(_last);
                    }

                    _last = next;
                }

                buffer = _last;
                return buffer is not null;
            }
        }
    }
}
