using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Pbw.Core;

namespace Pbw.Windows;

public sealed class WindowsSnapshotSource(IWindowService windows, IElementAutomationService automation) : ISnapshotSource
{
    public Task<Snapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var id = "snapshot-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = new Snapshot(
            PbwSchema.Version,
            id,
            DateTimeOffset.UtcNow,
            new DisplayContext("primary", new Bounds(0, 0, Native.GetSystemMetrics(0), Native.GetSystemMetrics(1)), 1, true),
            windows.ListWindows(),
            automation.ReadTree(),
            Array.Empty<OcrTextSnapshot>(),
            Metadata: new Dictionary<string, object?>
            {
                ["captureMethod"] = "win32-window-enumeration",
                ["ocrStatus"] = "degraded",
                ["annotationStatus"] = "available-for-json-only"
            });
        return Task.FromResult(snapshot);
    }
}

public sealed class WindowsWindowService : IWindowService
{
    public IReadOnlyList<WindowSnapshot> ListWindows()
    {
        var result = new List<WindowSnapshot>();
        var foreground = Native.GetForegroundWindow();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            var title = GetTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            Native.GetWindowRect(hwnd, out var rect);
            var pid = 0;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            var processName = TryProcessName(pid);
            result.Add(new WindowSnapshot(
                hwnd.ToInt32(),
                title,
                processName,
                new Bounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
                true,
                Native.IsIconic(hwnd),
                hwnd == foreground));
            return true;
        }, IntPtr.Zero);
        return result.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ActionResult Focus(int handle)
    {
        var ok = Native.SetForegroundWindow(new IntPtr(handle));
        return Result("window.focus", ok, "SetForegroundWindow", handle);
    }

    public ActionResult Move(int handle, int x, int y)
    {
        Native.GetWindowRect(new IntPtr(handle), out var rect);
        var ok = Native.MoveWindow(new IntPtr(handle), x, y, rect.Right - rect.Left, rect.Bottom - rect.Top, true);
        return Result("window.move", ok, "MoveWindow", handle);
    }

    public ActionResult Resize(int handle, int width, int height)
    {
        Native.GetWindowRect(new IntPtr(handle), out var rect);
        var ok = Native.MoveWindow(new IntPtr(handle), rect.Left, rect.Top, width, height, true);
        return Result("window.resize", ok, "MoveWindow", handle);
    }

    public ActionResult SetBounds(int handle, Bounds bounds)
    {
        var ok = Native.MoveWindow(new IntPtr(handle), bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
        return Result("window.set-bounds", ok, "MoveWindow", handle);
    }

    public ActionResult Minimize(int handle) => Show("window.minimize", handle, 6);
    public ActionResult Maximize(int handle) => Show("window.maximize", handle, 3);
    public ActionResult Restore(int handle) => Show("window.restore", handle, 9);

    public ActionResult Close(int handle)
    {
        var ok = Native.PostMessage(new IntPtr(handle), 0x0010, IntPtr.Zero, IntPtr.Zero);
        return Result("window.close", ok, "WM_CLOSE", handle);
    }

    private static ActionResult Show(string action, int handle, int command)
    {
        var ok = Native.ShowWindow(new IntPtr(handle), command);
        return Result(action, ok, "ShowWindow", handle);
    }

    private static ActionResult Result(string action, bool ok, string method, int handle) =>
        new(action, ok, method, handle.ToString(), ok ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message);

    private static string GetTitle(IntPtr hwnd)
    {
        var length = Native.GetWindowTextLength(hwnd);
        var sb = new StringBuilder(length + 1);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TryProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return "unknown"; }
    }
}

public sealed class WindowsAppService(IWindowService windows) : IAppService
{
    public IReadOnlyList<AppInfo> ListApps() => Process.GetProcesses()
        .Where(p => !string.IsNullOrWhiteSpace(Safe(() => p.ProcessName)))
        .Select(p => new AppInfo(p.Id, Safe(() => p.ProcessName) ?? "unknown", Safe(() => p.MainWindowTitle), Safe(() => p.MainWindowHandle.ToInt32())))
        .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public ActionResult Launch(string path, string? arguments)
    {
        var start = new ProcessStartInfo(path, arguments ?? "") { UseShellExecute = true };
        var process = Process.Start(start);
        return new ActionResult("app.launch", process is not null, "Process.Start", process?.Id.ToString());
    }

    public ActionResult Focus(string processName)
    {
        var app = ListApps().FirstOrDefault(a => a.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase) && a.MainWindowHandle is not null);
        if (app is null) return new ActionResult("app.focus", false, "SetForegroundWindow", null, "Process with a main window was not found.");
        return windows.Focus(app.MainWindowHandle!.Value);
    }

    public ActionResult Switch(string processName) => Focus(processName) with { Action = "app.switch" };

    public ActionResult Quit(string processName)
    {
        var matched = Process.GetProcessesByName(processName);
        foreach (var process in matched)
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow();
            }
            catch { }
        }
        return new ActionResult("app.quit", matched.Length > 0, "CloseMainWindow", processName);
    }

    private static T? Safe<T>(Func<T> func)
    {
        try { return func(); }
        catch { return default; }
    }
}

public sealed class WindowsClipboardService : IClipboardService
{
    private static string? memoryFallback;

    public string? GetText()
    {
        try
        {
            if (!Native.IsClipboardFormatAvailable(13) || !Native.OpenClipboard(IntPtr.Zero)) return memoryFallback;
            try
            {
                var handle = Native.GetClipboardData(13);
                if (handle == IntPtr.Zero) return memoryFallback;
                var pointer = Native.GlobalLock(handle);
                if (pointer == IntPtr.Zero) return memoryFallback;
                try
                {
                    memoryFallback = Marshal.PtrToStringUni(pointer);
                    return memoryFallback;
                }
                finally
                {
                    Native.GlobalUnlock(handle);
                }
            }
            finally
            {
                Native.CloseClipboard();
            }
        }
        catch
        {
            return memoryFallback;
        }
    }

    public ActionResult SetText(string text)
    {
        memoryFallback = text;
        try
        {
            if (!Native.OpenClipboard(IntPtr.Zero))
            {
                return new ActionResult("clipboard.set", true, "in-process-fallback", Details: new Dictionary<string, object?> { ["degraded"] = true });
            }

            try
            {
                Native.EmptyClipboard();
                var bytes = Encoding.Unicode.GetBytes(text + "\0");
                var handle = Native.GlobalAlloc(0x0042, (UIntPtr)bytes.Length);
                if (handle == IntPtr.Zero) return new ActionResult("clipboard.set", true, "in-process-fallback", Details: new Dictionary<string, object?> { ["degraded"] = true });
                var pointer = Native.GlobalLock(handle);
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                Native.GlobalUnlock(handle);
                var ok = Native.SetClipboardData(13, handle) != IntPtr.Zero;
                return new ActionResult("clipboard.set", ok, ok ? "Win32Clipboard" : "in-process-fallback", Details: ok ? null : new Dictionary<string, object?> { ["degraded"] = true });
            }
            finally
            {
                Native.CloseClipboard();
            }
        }
        catch
        {
            return new ActionResult("clipboard.set", true, "in-process-fallback", Details: new Dictionary<string, object?> { ["degraded"] = true });
        }
    }

    public ActionResult Clear()
    {
        memoryFallback = null;
        try
        {
            if (!Native.OpenClipboard(IntPtr.Zero))
            {
                return new ActionResult("clipboard.clear", true, "in-process-fallback", Details: new Dictionary<string, object?> { ["degraded"] = true });
            }
            try
            {
                Native.EmptyClipboard();
                return new ActionResult("clipboard.clear", true, "Win32Clipboard");
            }
            finally
            {
                Native.CloseClipboard();
            }
        }
        catch
        {
            return new ActionResult("clipboard.clear", true, "in-process-fallback", Details: new Dictionary<string, object?> { ["degraded"] = true });
        }
    }
}

public sealed class WindowsInputService : IInputService
{
    public ActionResult Click(int x, int y, string button = "left")
    {
        Native.SetCursorPos(x, y);
        var (down, up) = button.Equals("right", StringComparison.OrdinalIgnoreCase) ? (0x0008, 0x0010) : (0x0002, 0x0004);
        Native.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Native.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        return new("click", true, "Win32Input", Details: new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["button"] = button });
    }

    public ActionResult Move(int x, int y)
    {
        var ok = Native.SetCursorPos(x, y);
        return new("move", ok, "SetCursorPos", Details: new Dictionary<string, object?> { ["x"] = x, ["y"] = y });
    }

    public ActionResult TypeText(string text)
    {
        foreach (var c in text)
        {
            var vk = Native.VkKeyScan(c);
            if (vk == -1) continue;
            var needsShift = (vk & 0x0100) != 0;
            if (needsShift) Native.keybd_event(0x10, 0, 0, UIntPtr.Zero);
            PressVirtualKey((byte)(vk & 0xff));
            if (needsShift) Native.keybd_event(0x10, 0, 0x0002, UIntPtr.Zero);
        }
        return new("type", true, "Win32Input", Details: new Dictionary<string, object?> { ["length"] = text.Length });
    }

    public ActionResult Press(string key)
    {
        PressVirtualKey(KeyToVirtualKey(key));
        return new("press", true, "Win32Input", Details: new Dictionary<string, object?> { ["key"] = key });
    }

    public ActionResult Hotkey(IReadOnlyList<string> keys)
    {
        var virtualKeys = keys.Select(KeyToVirtualKey).ToArray();
        foreach (var key in virtualKeys) Native.keybd_event(key, 0, 0, UIntPtr.Zero);
        foreach (var key in virtualKeys.Reverse()) Native.keybd_event(key, 0, 0x0002, UIntPtr.Zero);
        return new("hotkey", true, "Win32Input", Details: new Dictionary<string, object?> { ["keys"] = keys });
    }

    public ActionResult Scroll(int delta, int? x = null, int? y = null)
    {
        if (x is not null && y is not null) Native.SetCursorPos(x.Value, y.Value);
        Native.mouse_event(0x0800, 0, 0, delta, UIntPtr.Zero);
        return new("scroll", true, "Win32Input", Details: new Dictionary<string, object?> { ["delta"] = delta, ["x"] = x, ["y"] = y });
    }

    public ActionResult Drag(int fromX, int fromY, int toX, int toY)
    {
        Native.SetCursorPos(fromX, fromY);
        Native.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        Native.SetCursorPos(toX, toY);
        Native.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        return new("drag", true, "Win32Input", Details: new Dictionary<string, object?> { ["fromX"] = fromX, ["fromY"] = fromY, ["toX"] = toX, ["toY"] = toY });
    }

    private static void PressVirtualKey(byte key)
    {
        Native.keybd_event(key, 0, 0, UIntPtr.Zero);
        Native.keybd_event(key, 0, 0x0002, UIntPtr.Zero);
    }

    private static byte KeyToVirtualKey(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11,
            "shift" => 0x10,
            "alt" => 0x12,
            "enter" => 0x0D,
            "esc" or "escape" => 0x1B,
            "tab" => 0x09,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ when key.Length == 1 => (byte)(Native.VkKeyScan(key[0]) & 0xff),
            _ => throw new ArgumentException($"Unsupported key '{key}'.")
        };
    }
}

public sealed class WindowsElementAutomationService : IElementAutomationService
{
    public IReadOnlyList<ElementSnapshot> ReadTree() => Array.Empty<ElementSnapshot>();
    public ActionResult SetValue(TargetSpec target, string value) => new("set-value", false, "UIAutomation", Message: "UI Automation value pattern is unavailable in this build.", Details: Degraded());
    public ActionResult PerformAction(TargetSpec target, string action) => new("perform-action", false, "UIAutomation", Message: "UI Automation action pattern is unavailable in this build.", Details: Degraded());
    public IReadOnlyList<MenuItemInfo> ListMenus(TargetSpec target) => Array.Empty<MenuItemInfo>();
    public ActionResult ClickMenu(TargetSpec target, string menu) => new("menu.click", false, "UIAutomation", menu, "Menu item was not found.", Degraded());
    public IReadOnlyList<DialogInfo> ListDialogs() => Array.Empty<DialogInfo>();
    public ActionResult ClickDialog(TargetSpec target, string button) => new("dialog.click", false, "UIAutomation", button, "Dialog button was not found.", Degraded());
    public ActionResult InputDialog(TargetSpec target, string value) => new("dialog.input", false, "UIAutomation", Message: "Dialog input target was not found.", Details: Degraded());
    public ActionResult DismissDialog(TargetSpec target) => new("dialog.dismiss", false, "UIAutomation", Message: "Dialog target was not found.", Details: Degraded());
    private static Dictionary<string, object?> Degraded() => new() { ["degraded"] = true, ["reason"] = "uia_adapter_placeholder" };
}

public sealed class WindowsDoctorCheckService : IDoctorCheckService
{
    public IReadOnlyList<DoctorCheck> RunChecks(PbwConfig config)
    {
        var checks = new List<DoctorCheck>
        {
            new("os", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ok" : "warning", RuntimeInformation.OSDescription),
            new("uia", "warning", "UIA tree reading is available through the adapter contract; full pattern automation is degraded in this build."),
            new("capture", "warning", "Window enumeration capture is available; bitmap capture falls back to degraded metadata."),
            new("ocr", "warning", "OCR service is a safe no-op in this build."),
            new("dpi", "ok", "Coordinate converter uses display scale metadata.", new Dictionary<string, object?> { ["systemMetricsWidth"] = Native.GetSystemMetrics(0), ["systemMetricsHeight"] = Native.GetSystemMetrics(1) }),
            new("integrity", "ok", "Integrity level check is not elevated-specific in this build."),
            new("mcp", config.Mcp.StdioEnabled && !config.Mcp.RemoteListenerEnabled ? "ok" : "error", "MCP stdio transport configured; remote listener disabled.")
        };
        try
        {
            Directory.CreateDirectory(config.SnapshotDirectory);
            var probe = Path.Combine(config.SnapshotDirectory, ".probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            checks.Add(new DoctorCheck("snapshotDirectory", "ok", config.SnapshotDirectory));
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("snapshotDirectory", "error", ex.Message));
        }

        var validation = new ConfigLoader().Validate(config);
        checks.Add(new DoctorCheck("config", validation.Valid ? "ok" : "error", validation.Valid ? "Config is valid." : string.Join("; ", validation.Errors)));
        return checks;
    }
}

internal static partial class Native
{
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] internal static extern void keybd_event(byte bVk, byte bScan, int dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern short VkKeyScan(char ch);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GlobalUnlock(IntPtr hMem);
}

internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
