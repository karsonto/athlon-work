using System.Runtime.InteropServices;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.App.Services.ComputerUse;

public sealed class ComputerUseInputService
{
    public async Task ExecuteAsync(
        string action,
        int x,
        int y,
        int? endX,
        int? endY,
        string? text,
        string? key,
        int scrollDelta,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (action)
        {
            case "click":
                MoveCursor(x, y);
                SendMouseClick(MouseLeftDown, MouseLeftUp);
                break;
            case "double_click":
                MoveCursor(x, y);
                SendMouseClick(MouseLeftDown, MouseLeftUp);
                await Task.Delay(80, CancellationToken.None).ConfigureAwait(false);
                SendMouseClick(MouseLeftDown, MouseLeftUp);
                break;
            case "right_click":
                MoveCursor(x, y);
                SendMouseClick(MouseRightDown, MouseRightUp);
                break;
            case "type_text":
                if (text is null)
                {
                    throw new ArgumentException("type_text requires text.");
                }
                SendUnicodeText(text);
                break;
            case "key":
            case "hotkey":
                SendKeyExpression(key);
                break;
            case "scroll":
                MoveCursor(x, y);
                SendMouse(MouseWheel, unchecked((uint)scrollDelta));
                break;
            case "drag":
                if (endX is null || endY is null)
                {
                    throw new ArgumentException("drag requires end_x and end_y.");
                }
                await DragAsync(x, y, endX.Value, endY.Value).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Unsupported Computer Use action '{action}'.");
        }
    }

    private static async Task DragAsync(int startX, int startY, int endX, int endY)
    {
        MoveCursor(startX, startY);
        var mouseDown = false;
        try
        {
            SendMouse(MouseLeftDown, 0);
            mouseDown = true;
            // Give the target time to arm press-and-drag before movement starts.
            await Task.Delay(120, CancellationToken.None).ConfigureAwait(false);

            var path = ComputerUseDragPath.Build(startX, startY, endX, endY);
            foreach (var (pointX, pointY) in path)
            {
                MoveCursor(pointX, pointY);
                await Task.Delay(16, CancellationToken.None).ConfigureAwait(false);
            }

            await Task.Delay(80, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (mouseDown)
            {
                TrySendMouseUp(MouseLeftUp);
            }
        }
    }

    private static void MoveCursor(int x, int y)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        var normalizedX = (int)Math.Round((x - left) * 65535d / Math.Max(1, width - 1));
        var normalizedY = (int)Math.Round((y - top) * 65535d / Math.Max(1, height - 1));
        SendMouse(
            MouseMove | MouseAbsolute | MouseVirtualDesk,
            0,
            Math.Clamp(normalizedX, 0, 65535),
            Math.Clamp(normalizedY, 0, 65535));
    }

    private static void SendMouse(uint flags, uint mouseData, int dx = 0, int dy = 0)
    {
        Send([MouseInputFor(flags, mouseData, dx, dy)]);
    }

    private static Input MouseInputFor(uint flags, uint mouseData, int dx = 0, int dy = 0) =>
        new()
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = mouseData,
                    Flags = flags
                }
            }
        };

    private static void SendMouseClick(uint downFlag, uint upFlag)
    {
        try
        {
            Send([MouseInputFor(downFlag, 0), MouseInputFor(upFlag, 0)]);
        }
        catch
        {
            TrySendMouseUp(upFlag);
            throw;
        }
    }

    private static void SendUnicodeText(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(KeyboardInputFor(character, KeyEventUnicode));
            inputs.Add(KeyboardInputFor(character, KeyEventUnicode | KeyEventKeyUp));
        }

        try
        {
            Send(inputs.ToArray());
        }
        catch
        {
            try
            {
                Send(text
                    .Select(character => KeyboardInputFor(
                        character,
                        KeyEventUnicode | KeyEventKeyUp))
                    .ToArray());
            }
            catch
            {
                // Preserve the original SendInput error.
            }

            throw;
        }
    }

    private static void SendKeyExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("key/hotkey requires key.");
        }

        var parts = expression.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var virtualKeys = parts.Select(ParseVirtualKey).ToArray();
        if (virtualKeys.Length == 0)
        {
            throw new ArgumentException("No valid key was provided.");
        }

        var inputs = new List<Input>(virtualKeys.Length * 2);
        foreach (var virtualKey in virtualKeys)
        {
            inputs.Add(KeyboardInputFor(virtualKey, 0));
        }

        for (var index = virtualKeys.Length - 1; index >= 0; index--)
        {
            inputs.Add(KeyboardInputFor(virtualKeys[index], KeyEventKeyUp));
        }

        try
        {
            Send(inputs.ToArray());
        }
        catch
        {
            // SendInput can report a partial write. Best-effort key-up avoids leaving
            // Ctrl/Alt/Shift held if Windows accepted only the prefix.
            TryReleaseKeys(virtualKeys);
            throw;
        }
    }

    private static ushort ParseVirtualKey(string key)
    {
        var normalized = key.Trim().ToUpperInvariant();
        if (normalized.Length == 1)
        {
            var code = VkKeyScan(normalized[0]);
            if (code != -1)
            {
                return (ushort)(code & 0xFF);
            }
        }

        return normalized switch
        {
            "CTRL" or "CONTROL" => 0x11,
            "SHIFT" => 0x10,
            "ALT" => 0x12,
            "WIN" or "WINDOWS" => 0x5B,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "SPACE" => 0x20,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => throw new ArgumentException($"Unsupported key '{key}'.")
        };
    }

    private static Input KeyboardInputFor(char character, uint flags) =>
        new()
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    Scan = character,
                    Flags = flags
                }
            }
        };

    private static Input KeyboardInputFor(ushort virtualKey, uint flags) =>
        new()
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = flags
                }
            }
        };

    private static void Send(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                "Windows rejected the requested input. The target may be elevated or on the secure desktop.");
        }
    }

    private static void TrySendMouseUp(uint upFlag)
    {
        try
        {
            SendMouse(upFlag, 0);
        }
        catch
        {
            // Preserve the original drag failure; this is a best-effort safety release.
        }
    }

    private static void TryReleaseKeys(IReadOnlyList<ushort> virtualKeys)
    {
        try
        {
            var releases = virtualKeys
                .Reverse()
                .Select(key => KeyboardInputFor(key, KeyEventKeyUp))
                .ToArray();
            Send(releases);
        }
        catch
        {
            // Preserve the original SendInput error.
        }
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseWheel = 0x0800;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseAbsolute = 0x8000;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInput);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char character);
}
