using System.Runtime.InteropServices;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.App;

/// <summary>
/// Explicit, same-user macro recorder. It polls Windows input only while the
/// recording command is active; no hook or background capture exists while
/// idle. The recorded data remains in memory until it is stopped and validated.
/// </summary>
internal sealed class WindowsMacroInputRecorder : IMacroRecorder
{
    private const int PollMilliseconds = 15;
    private const int MaximumSteps = 10_000;
    private const int VirtualKeyCount = 256;
    private static readonly (int VirtualKey, int ButtonCode)[] MouseButtons =
    [
        (0x01, 1), // left
        (0x02, 2), // right
        (0x04, 3), // middle
        (0x05, 4), // X1
        (0x06, 5)  // X2
    ];

    private readonly object _sync = new();
    private readonly bool[] _observedKeys = new bool[VirtualKeyCount];
    private readonly bool[] _recordedKeys = new bool[VirtualKeyCount];
    private readonly Dictionary<int, bool> _observedButtons = [];
    private readonly Dictionary<int, bool> _recordedButtons = [];
    private readonly List<MacroStepV1> _steps = [];
    private CancellationTokenSource? _cancellation;
    private Task? _pump;
    private Point _lastCursor;
    private DateTimeOffset _lastEventAt;
    private DateTimeOffset _lastMoveAt;
    private Exception? _fault;

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _cancellation is not null;
            }
        }
    }

    public Task StartAsync(TimeSpan maximumDuration, CancellationToken cancellationToken)
    {
        if (maximumDuration < TimeSpan.FromSeconds(1) || maximumDuration > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        }

        lock (_sync)
        {
            if (_cancellation is not null)
            {
                throw new InvalidOperationException("A macro recording is already active.");
            }

            Array.Clear(_observedKeys);
            Array.Clear(_recordedKeys);
            _observedButtons.Clear();
            _recordedButtons.Clear();
            _steps.Clear();
            _fault = null;
            for (int key = 1; key < VirtualKeyCount; key++)
            {
                _observedKeys[key] = IsPressed(key);
            }
            foreach ((int virtualKey, int button) in MouseButtons)
            {
                _observedButtons[button] = IsPressed(virtualKey);
                _recordedButtons[button] = false;
            }
            _ = GetCursorPos(out _lastCursor);
            _lastEventAt = DateTimeOffset.UtcNow;
            _lastMoveAt = _lastEventAt;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellation.CancelAfter(maximumDuration);
            _pump = Task.Run(() => PollAsync(_cancellation.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<MacroStepV1>> StopAsync(CancellationToken cancellationToken)
    {
        Task? pump;
        CancellationTokenSource? recording;
        lock (_sync)
        {
            recording = _cancellation ?? throw new InvalidOperationException("No macro recording is active.");
            pump = _pump;
            recording.Cancel();
        }

        if (pump is not null)
        {
            await pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            try
            {
                if (_fault is not null)
                {
                    throw new InvalidOperationException("Macro recording failed.", _fault);
                }

                foreach (int key in Enumerable.Range(1, VirtualKeyCount - 1).Where(key => _recordedKeys[key]))
                {
                    AddStep(MacroStepKind.KeyUp, key, 0, 0, 0, DateTimeOffset.UtcNow);
                    _recordedKeys[key] = false;
                }
                foreach ((int button, bool pressed) in _recordedButtons.Where(pair => pair.Value).ToArray())
                {
                    AddStep(MacroStepKind.MouseButtonUp, button, 0, 0, 0, DateTimeOffset.UtcNow);
                    _recordedButtons[button] = false;
                }
                return _steps.ToArray();
            }
            finally
            {
                DisposeRecording_NoLock();
            }
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        Task? pump;
        CancellationTokenSource? recording;
        lock (_sync)
        {
            recording = _cancellation;
            pump = _pump;
            recording?.Cancel();
        }
        if (pump is not null)
        {
            try { await pump.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        }
        lock (_sync)
        {
            _steps.Clear();
            DisposeRecording_NoLock();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await CancelAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                CaptureTick();
                await Task.Delay(PollMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected explicit stop, cancel, or duration expiry.
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _fault = exception;
            }
        }
    }

    private void CaptureTick()
    {
        lock (_sync)
        {
            if (_cancellation is null)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int key = 1; key < VirtualKeyCount; key++)
            {
                if (MouseButtons.Any(button => button.VirtualKey == key))
                {
                    continue;
                }
                bool pressed = IsPressed(key);
                if (pressed == _observedKeys[key])
                {
                    continue;
                }
                _observedKeys[key] = pressed;
                if (pressed)
                {
                    AddStep(MacroStepKind.KeyDown, key, 0, 0, 0, now);
                    _recordedKeys[key] = true;
                }
                else if (_recordedKeys[key])
                {
                    AddStep(MacroStepKind.KeyUp, key, 0, 0, 0, now);
                    _recordedKeys[key] = false;
                }
            }

            foreach ((int virtualKey, int button) in MouseButtons)
            {
                bool pressed = IsPressed(virtualKey);
                bool previous = _observedButtons[button];
                if (pressed == previous)
                {
                    continue;
                }
                _observedButtons[button] = pressed;
                if (pressed)
                {
                    AddStep(MacroStepKind.MouseButtonDown, button, 0, 0, 0, now);
                    _recordedButtons[button] = true;
                }
                else if (_recordedButtons[button])
                {
                    AddStep(MacroStepKind.MouseButtonUp, button, 0, 0, 0, now);
                    _recordedButtons[button] = false;
                }
            }

            if (GetCursorPos(out Point cursor)
                && (Math.Abs(cursor.X - _lastCursor.X) >= 4 || Math.Abs(cursor.Y - _lastCursor.Y) >= 4)
                && now - _lastMoveAt >= TimeSpan.FromMilliseconds(30))
            {
                AddStep(MacroStepKind.MouseMove, 0, cursor.X, cursor.Y, 0, now);
                _lastCursor = cursor;
                _lastMoveAt = now;
            }
        }
    }

    private void AddStep(MacroStepKind kind, int code, int x, int y, int delta, DateTimeOffset now)
    {
        if (_steps.Count >= MaximumSteps)
        {
            throw new InvalidOperationException("Macro recording reached the 10,000-step safety limit.");
        }
        TimeSpan delay = now - _lastEventAt;
        _steps.Add(new MacroStepV1(kind, code, x, y, delta, delay < TimeSpan.Zero ? TimeSpan.Zero : delay));
        _lastEventAt = now;
    }

    private void DisposeRecording_NoLock()
    {
        _cancellation?.Dispose();
        _cancellation = null;
        _pump = null;
    }

    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}
