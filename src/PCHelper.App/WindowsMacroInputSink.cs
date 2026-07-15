using System.ComponentModel;
using System.Runtime.InteropServices;
using PCHelper.Core;

namespace PCHelper.App;

internal sealed class WindowsMacroInputSink : IMacroInputSink
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyUpFlag = 0x0002;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheelFlag = 0x0800;

    public void KeyDown(int code) => SendKeyboard(code, keyUp: false);

    public void KeyUp(int code) => SendKeyboard(code, keyUp: true);

    public void MouseButtonDown(int code) => SendMouse(ButtonFlag(code, down: true), code is 4 or 5 ? (uint)(code - 3) : 0);

    public void MouseButtonUp(int code) => SendMouse(ButtonFlag(code, down: false), code is 4 or 5 ? (uint)(code - 3) : 0);

    public void MouseMove(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Macro cursor movement failed.");
        }
    }

    public void MouseWheel(int delta) => SendMouse(MouseWheelFlag, unchecked((uint)delta));

    public void MediaKey(int code)
    {
        SendKeyboard(code, keyUp: false);
        SendKeyboard(code, keyUp: true);
    }

    private static void SendKeyboard(int code, bool keyUp)
    {
        if (code is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }
        Input input = new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput { VirtualKey = (ushort)code, Flags = keyUp ? KeyUpFlag : 0 }
            }
        };
        Send(input);
    }

    private static void SendMouse(uint flags, uint data)
    {
        Input input = new()
        {
            Type = InputMouse,
            Data = new InputUnion { Mouse = new MouseInput { MouseData = data, Flags = flags } }
        };
        Send(input);
    }

    private static uint ButtonFlag(int code, bool down) => (code, down) switch
    {
        (1, true) => MouseLeftDown,
        (1, false) => MouseLeftUp,
        (2, true) => MouseRightDown,
        (2, false) => MouseRightUp,
        (3, true) => MouseMiddleDown,
        (3, false) => MouseMiddleUp,
        (4 or 5, true) => MouseXDown,
        (4 or 5, false) => MouseXUp,
        _ => throw new ArgumentOutOfRangeException(nameof(code), "Mouse button code must be 1-5.")
    };

    private static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected macro input.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
