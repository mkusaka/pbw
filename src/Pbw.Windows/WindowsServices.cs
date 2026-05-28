using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Pbw.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using MsaaAccessible = Accessibility.IAccessible;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Pbw.Tests")]

namespace Pbw.Windows;

public interface IWindowsCaptureService
{
    CaptureResult CaptureDesktop(string imagePath, IReadOnlyList<WindowSnapshot> windows, IReadOnlyList<ElementSnapshot> elements);
    CaptureResult CaptureWindow(int handle, string imagePath, IReadOnlyList<ElementSnapshot> elements);
}

public interface IWindowsOcrService
{
    IReadOnlyList<OcrTextSnapshot> Recognize(string? imagePath);
}

internal interface IWindowsMsaaAutomationAdapter
{
    bool HasKnownLegacyWindows();
    IReadOnlyList<ElementSnapshot> ReadTree(int? windowHandle = null);
    IReadOnlyList<ElementSnapshot> ReadLegacyWindowTrees();
    ActionResult Click(TargetSpec target);
    ActionResult PerformAction(TargetSpec target, string action);
}

public sealed record CaptureResult(
    bool Success,
    string Method,
    string? ImagePath,
    string? Message = null,
    string Status = CaptureQualityStatus.Ok,
    IReadOnlyDictionary<string, object?>? Details = null);

public static class CaptureQualityStatus
{
    public const string Ok = "ok";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
}

public sealed record CaptureAttempt(
    string Method,
    string Status,
    string? Message = null,
    IReadOnlyDictionary<string, object?>? Details = null);

public sealed record BmpQualityInfo(
    bool IsBmp,
    int Width,
    int Height,
    long PixelCount,
    long BlackPixels,
    double BlackRatio,
    bool IsMostlyBlack,
    string Status,
    string? Message = null);

public static class BmpCaptureDiagnostics
{
    private const double MostlyBlackThreshold = 0.995d;
    private const int MinimumBmpHeaderLength = 54;
    private const long MaxInspectablePixels = 100_000_000L;

    public static BmpQualityInfo Analyze(string path)
    {
        if (!File.Exists(path))
        {
            return Unavailable("BMP file was not found.");
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < MinimumBmpHeaderLength || bytes[0] != 'B' || bytes[1] != 'M')
            {
                return Unavailable("Capture output was not a BMP file.", isBmp: false);
            }

            var pixelOffset = (long)BitConverter.ToInt32(bytes, 10);
            var dibHeaderSize = BitConverter.ToInt32(bytes, 14);
            var width = BitConverter.ToInt32(bytes, 18);
            var signedHeight = BitConverter.ToInt32(bytes, 22);
            var bitCount = BitConverter.ToInt16(bytes, 28);
            var compression = BitConverter.ToInt32(bytes, 30);
            var height = signedHeight == int.MinValue ? (long)int.MaxValue + 1 : Math.Abs((long)signedHeight);
            if (width <= 0 || height <= 0)
            {
                return BmpUnavailable(width, height, "BMP had no pixels.");
            }

            if (dibHeaderSize < 40 || pixelOffset < MinimumBmpHeaderLength || pixelOffset > bytes.LongLength)
            {
                return BmpUnavailable(width, height, "BMP header had an invalid DIB size or pixel data offset.");
            }

            if (compression != 0 || bitCount is not (24 or 32))
            {
                return BmpUnavailable(width, height, $"Unsupported BMP format: {bitCount} bpp, compression {compression}.");
            }

            var bytesPerPixel = bitCount / 8;
            if (!TryCalculateLayout(width, height, bitCount, pixelOffset, out var stride, out var pixelCount, out var requiredLength, out var layoutError))
            {
                return BmpUnavailable(width, height, layoutError);
            }

            if (pixelCount > MaxInspectablePixels)
            {
                return BmpUnavailable(width, height, $"BMP dimensions exceed supported inspection size: {width}x{height} ({pixelCount} pixels).");
            }

            if (requiredLength > bytes.LongLength)
            {
                return BmpUnavailable(width, height, $"BMP pixel data was incomplete: required {requiredLength} bytes, found {bytes.LongLength}.");
            }

            long blackPixels = 0;
            var inspectHeight = (int)height;
            for (var y = 0; y < inspectHeight; y++)
            {
                var row = pixelOffset + (y * stride);
                for (var x = 0; x < width; x++)
                {
                    var index = (int)(row + ((long)x * bytesPerPixel));
                    if (bytes[index] == 0 && bytes[index + 1] == 0 && bytes[index + 2] == 0)
                    {
                        blackPixels++;
                    }
                }
            }

            var blackRatio = pixelCount == 0 ? 0 : (double)blackPixels / pixelCount;
            var mostlyBlack = pixelCount > 0 && blackRatio >= MostlyBlackThreshold;
            return new BmpQualityInfo(
                true,
                width,
                inspectHeight,
                pixelCount,
                blackPixels,
                blackRatio,
                mostlyBlack,
                mostlyBlack ? CaptureQualityStatus.Degraded : CaptureQualityStatus.Ok,
                mostlyBlack ? "BMP was mostly black; capture may contain no useful rendered pixels." : null);
        }
        catch (Exception ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private static bool TryCalculateLayout(
        int width,
        long height,
        short bitCount,
        long pixelOffset,
        out long stride,
        out long pixelCount,
        out long requiredLength,
        out string message)
    {
        stride = 0;
        pixelCount = 0;
        requiredLength = 0;
        message = "";

        try
        {
            checked
            {
                var bitsPerRow = (long)width * bitCount;
                stride = ((bitsPerRow + 31) / 32) * 4;
                pixelCount = (long)width * height;
                requiredLength = pixelOffset + (stride * height);
            }
        }
        catch (OverflowException)
        {
            message = "BMP dimensions or pixel offset were too large to inspect safely.";
            return false;
        }

        if (stride <= 0 || pixelCount <= 0 || requiredLength <= pixelOffset)
        {
            message = "BMP layout was invalid or empty.";
            return false;
        }

        return true;
    }

    private static BmpQualityInfo BmpUnavailable(int width, long height, string message) =>
        new(true, width, ToReportedDimension(height), 0, 0, 0, false, CaptureQualityStatus.Unavailable, message);

    private static int ToReportedDimension(long value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;

    private static BmpQualityInfo Unavailable(string message, bool isBmp = true) =>
        new(isBmp, 0, 0, 0, 0, 0, false, CaptureQualityStatus.Unavailable, message);
}

public sealed class WindowsSnapshotSource(
    IWindowService windows,
    IElementAutomationService automation,
    IWindowsCaptureService capture,
    IWindowsOcrService ocr,
    string imageDirectory) : ISnapshotSource
{
    public Task<Snapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var id = "snapshot-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowSnapshots = windows.ListWindows();
        var elements = automation.ReadTree();
        Directory.CreateDirectory(imageDirectory);
        var imagePath = Path.Combine(imageDirectory, id + ".bmp");
        var captureResult = capture.CaptureDesktop(imagePath, windowSnapshots, elements);
        var ocrText = ocr.Recognize(captureResult.ImagePath);
        var snapshot = new Snapshot(
            PbwSchema.Version,
            id,
            DateTimeOffset.UtcNow,
            new DisplayContext("primary", new Bounds(0, 0, Native.GetSystemMetrics(0), Native.GetSystemMetrics(1)), 1, true),
            windowSnapshots,
            elements,
            ocrText,
            captureResult.ImagePath,
            new Dictionary<string, object?>
            {
                ["captureMethod"] = captureResult.Method,
                ["captureStatus"] = captureResult.Status,
                ["captureMessage"] = captureResult.Message,
                ["captureDetails"] = captureResult.Details,
                ["ocrStatus"] = ocrText.Count > 0 ? "ok" : "empty",
                ["annotationStatus"] = captureResult.ImagePath is null ? "unavailable" : "ok"
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

public interface IWin32InputBackend
{
    IntPtr GetForegroundWindow();
    bool SetForegroundWindow(IntPtr hwnd);
    bool SetCursorPos(int x, int y);
    void MouseEvent(int flags, int dx, int dy, int data, UIntPtr extraInfo);
    void KeybdEvent(byte virtualKey, byte scanCode, int flags, UIntPtr extraInfo);
    short VkKeyScan(char character);
    uint MapVirtualKey(uint code, uint mapType);
    IntPtr WindowFromPoint(int x, int y);
    IntPtr GetRootWindow(IntPtr hwnd);
    bool IsWindow(IntPtr hwnd);
    string GetClassName(IntPtr hwnd);
    bool ScreenToClient(IntPtr hwnd, ref int x, ref int y);
    IntPtr ChildWindowFromPointEx(IntPtr hwnd, int x, int y, uint flags);
    bool IsChild(IntPtr parent, IntPtr child);
    bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    string LastErrorMessage();
}

public sealed class NativeWin32InputBackend : IWin32InputBackend
{
    public IntPtr GetForegroundWindow() => Native.GetForegroundWindow();
    public bool SetForegroundWindow(IntPtr hwnd) => Native.SetForegroundWindow(hwnd);
    public bool SetCursorPos(int x, int y) => Native.SetCursorPos(x, y);
    public void MouseEvent(int flags, int dx, int dy, int data, UIntPtr extraInfo) => Native.mouse_event(flags, dx, dy, data, extraInfo);
    public void KeybdEvent(byte virtualKey, byte scanCode, int flags, UIntPtr extraInfo) => Native.keybd_event(virtualKey, scanCode, flags, extraInfo);
    public short VkKeyScan(char character) => Native.VkKeyScan(character);
    public uint MapVirtualKey(uint code, uint mapType) => Native.MapVirtualKey(code, mapType);
    public IntPtr WindowFromPoint(int x, int y) => Native.WindowFromPoint(new POINT { X = x, Y = y });
    public IntPtr GetRootWindow(IntPtr hwnd)
    {
        var root = Native.GetAncestor(hwnd, Native.GaRoot);
        return root == IntPtr.Zero ? hwnd : root;
    }

    public bool IsWindow(IntPtr hwnd) => Native.IsWindow(hwnd);
    public string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "<unknown>";
        }

        var sb = new StringBuilder(256);
        var length = Native.GetClassName(hwnd, sb, sb.Capacity);
        return length <= 0 ? "<unknown>" : sb.ToString();
    }

    public bool ScreenToClient(IntPtr hwnd, ref int x, ref int y)
    {
        var point = new POINT { X = x, Y = y };
        var ok = Native.ScreenToClient(hwnd, ref point);
        x = point.X;
        y = point.Y;
        return ok;
    }

    public IntPtr ChildWindowFromPointEx(IntPtr hwnd, int x, int y, uint flags) =>
        Native.ChildWindowFromPointEx(hwnd, new POINT { X = x, Y = y }, flags);

    public bool IsChild(IntPtr parent, IntPtr child) => Native.IsChild(parent, child);
    public bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) => Native.PostMessage(hwnd, message, wParam, lParam);
    public string LastErrorMessage() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
}

public sealed class WindowsInputService : IInputService
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const int KeyEventUp = 0x0002;
    private const int MouseEventLeftDown = 0x0002;
    private const int MouseEventLeftUp = 0x0004;
    private const int MouseEventRightDown = 0x0008;
    private const int MouseEventRightUp = 0x0010;
    private const int MouseEventMiddleDown = 0x0020;
    private const int MouseEventMiddleUp = 0x0040;
    private const int MouseEventWheel = 0x0800;
    private const uint ChildWindowSkipInvisibleDisabledTransparent = 0x0001 | 0x0002 | 0x0004;
    private const ushort MkLeftButton = 0x0001;
    private const ushort MkRightButton = 0x0002;
    private const ushort MkMiddleButton = 0x0010;

    private readonly IWin32InputBackend backend;

    public WindowsInputService(IWin32InputBackend? backend = null)
    {
        this.backend = backend ?? new NativeWin32InputBackend();
    }

    public ActionResult Click(int x, int y, string button = "left", InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Dispatch(
            "click",
            dispatch,
            () => BackgroundClick(x, y, button, dispatch, windowHandle),
            backgroundError => ForegroundClick(x, y, button, dispatch, windowHandle, backgroundError));

    public ActionResult Move(int x, int y, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null)
    {
        if (dispatch == InputDispatchMode.Auto)
        {
            return ForegroundMove(x, y, dispatch, windowHandle, null);
        }

        return Dispatch(
            "move",
            dispatch,
            () => BackgroundMove(x, y, dispatch, windowHandle),
            backgroundError => ForegroundMove(x, y, dispatch, windowHandle, backgroundError));
    }

    public ActionResult TypeText(string text, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Dispatch(
            "type",
            dispatch,
            () => BackgroundTypeText(text, dispatch, windowHandle),
            backgroundError => ForegroundTypeText(text, dispatch, windowHandle, backgroundError));

    public ActionResult Press(string key, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Dispatch(
            "press",
            dispatch,
            () => BackgroundPress(key, dispatch, windowHandle),
            backgroundError => ForegroundPress(key, dispatch, windowHandle, backgroundError));

    public ActionResult Hotkey(IReadOnlyList<string> keys, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Dispatch(
            "hotkey",
            dispatch,
            () => BackgroundHotkey(keys, dispatch, windowHandle),
            backgroundError => ForegroundHotkey(keys, dispatch, windowHandle, backgroundError));

    public ActionResult Scroll(int delta, int? x = null, int? y = null, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Dispatch(
            "scroll",
            dispatch,
            () => BackgroundScroll(delta, x, y, dispatch, windowHandle),
            backgroundError => ForegroundScroll(delta, x, y, dispatch, windowHandle, backgroundError));

    public ActionResult Drag(int fromX, int fromY, int toX, int toY, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null)
    {
        if (dispatch == InputDispatchMode.Auto)
        {
            return ForegroundDrag(fromX, fromY, toX, toY, dispatch, windowHandle, null);
        }

        return Dispatch(
            "drag",
            dispatch,
            () => BackgroundDrag(fromX, fromY, toX, toY, dispatch, windowHandle),
            backgroundError => ForegroundDrag(fromX, fromY, toX, toY, dispatch, windowHandle, backgroundError));
    }

    private ActionResult Dispatch(
        string action,
        InputDispatchMode dispatch,
        Func<ActionResult> background,
        Func<PbwError?, ActionResult> foreground)
    {
        if (dispatch == InputDispatchMode.Background)
        {
            return background();
        }

        if (dispatch == InputDispatchMode.Foreground)
        {
            return foreground(null);
        }

        try
        {
            return background();
        }
        catch (PbwException ex) when (ex.Error.Code == "background_unavailable")
        {
            return foreground(ex.Error);
        }
    }

    private ActionResult BackgroundClick(int x, int y, string button, InputDispatchMode dispatch, int? windowHandle)
    {
        var target = ResolveMouseTarget("click", InputEventKind.MouseClick, windowHandle, x, y);
        var (down, up, mk) = MouseMessages(button);
        PostOrThrow("click", InputEventKind.MouseClick, target.TargetHwnd, WmMouseMove, IntPtr.Zero, MakeLParam(target.ClientX, target.ClientY), target);
        PostOrThrow("click", InputEventKind.MouseClick, target.TargetHwnd, down, new IntPtr(mk), MakeLParam(target.ClientX, target.ClientY), target);
        Thread.Sleep(20);
        PostOrThrow("click", InputEventKind.MouseClick, target.TargetHwnd, up, IntPtr.Zero, MakeLParam(target.ClientX, target.ClientY), target);
        return new("click", true, "Win32.PostMessage", FormatHwnd(target.TargetHwnd), Details: BackgroundDetails(dispatch, InputEventKind.MouseClick, target, new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["button"] = button }));
    }

    private ActionResult BackgroundMove(int x, int y, InputDispatchMode dispatch, int? windowHandle)
    {
        var target = ResolveMouseTarget("move", InputEventKind.MouseMove, windowHandle, x, y);
        PostOrThrow("move", InputEventKind.MouseMove, target.TargetHwnd, WmMouseMove, IntPtr.Zero, MakeLParam(target.ClientX, target.ClientY), target);
        return new("move", true, "Win32.PostMessage", FormatHwnd(target.TargetHwnd), Details: BackgroundDetails(dispatch, InputEventKind.MouseMove, target, new Dictionary<string, object?> { ["x"] = x, ["y"] = y }));
    }

    private ActionResult BackgroundTypeText(string text, InputDispatchMode dispatch, int? windowHandle)
    {
        var target = ResolveWindowTarget("type", InputEventKind.TextInput, windowHandle);
        foreach (var c in text)
        {
            if (c is '\r' or '\n')
            {
                PostKey("type", InputEventKind.TextInput, target, KeyToVirtualKey("enter"));
            }
            else
            {
                PostOrThrow("type", InputEventKind.TextInput, target.TargetHwnd, WmChar, new IntPtr(c), new IntPtr(1), target);
            }
        }

        return new("type", true, "Win32.PostMessage", FormatHwnd(target.TargetHwnd), Details: BackgroundDetails(dispatch, InputEventKind.TextInput, target, new Dictionary<string, object?> { ["length"] = text.Length }));
    }

    private ActionResult BackgroundPress(string key, InputDispatchMode dispatch, int? windowHandle)
    {
        var target = ResolveWindowTarget("press", InputEventKind.Keystroke, windowHandle);
        PostKey("press", InputEventKind.Keystroke, target, KeyToVirtualKey(key));
        return new("press", true, "Win32.PostMessage", FormatHwnd(target.TargetHwnd), Details: BackgroundDetails(dispatch, InputEventKind.Keystroke, target, new Dictionary<string, object?> { ["key"] = key }));
    }

    private ActionResult BackgroundHotkey(IReadOnlyList<string> keys, InputDispatchMode dispatch, int? windowHandle)
    {
        var target = ResolveWindowTarget("hotkey", InputEventKind.KeyCombo, windowHandle);
        var virtualKeys = keys.Select(KeyToVirtualKey).ToArray();
        foreach (var key in virtualKeys)
        {
            PostKeyDown("hotkey", InputEventKind.KeyCombo, target, key);
        }

        Thread.Sleep(4);
        foreach (var key in virtualKeys.Reverse())
        {
            PostKeyUp("hotkey", InputEventKind.KeyCombo, target, key);
        }

        return new("hotkey", true, "Win32.PostMessage", FormatHwnd(target.TargetHwnd), Details: BackgroundDetails(dispatch, InputEventKind.KeyCombo, target, new Dictionary<string, object?> { ["keys"] = keys }));
    }

    private ActionResult BackgroundScroll(int delta, int? x, int? y, InputDispatchMode dispatch, int? windowHandle)
    {
        var target = x is not null && y is not null
            ? ResolveMouseTarget("scroll", InputEventKind.MouseScroll, windowHandle, x.Value, y.Value)
            : ResolveWindowTarget("scroll", InputEventKind.MouseScroll, windowHandle);
        var lParam = x is not null && y is not null ? MakeLParam(x.Value, y.Value) : IntPtr.Zero;
        PostOrThrow("scroll", InputEventKind.MouseScroll, target.TargetHwnd, WmMouseWheel, MakeWheelWParam(delta), lParam, target);
        return new("scroll", true, "Win32.PostMessage", FormatHwnd(target.TargetHwnd), Details: BackgroundDetails(dispatch, InputEventKind.MouseScroll, target, new Dictionary<string, object?> { ["delta"] = delta, ["x"] = x, ["y"] = y }));
    }

    private ActionResult BackgroundDrag(int fromX, int fromY, int toX, int toY, InputDispatchMode dispatch, int? windowHandle)
    {
        var target = ResolveMouseTarget("drag", InputEventKind.MouseDrag, windowHandle, fromX, fromY);
        var (down, up, mk) = MouseMessages("left");
        var start = ToClient(target.TargetHwnd, fromX, fromY);
        var end = ToClient(target.TargetHwnd, toX, toY);
        PostOrThrow("drag", InputEventKind.MouseDrag, target.TargetHwnd, down, new IntPtr(mk), MakeLParam(start.ClientX, start.ClientY), target);
        for (var step = 1; step <= 8; step++)
        {
            var x = start.ClientX + (int)Math.Round((end.ClientX - start.ClientX) * (step / 8d));
            var y = start.ClientY + (int)Math.Round((end.ClientY - start.ClientY) * (step / 8d));
            PostOrThrow("drag", InputEventKind.MouseDrag, target.TargetHwnd, WmMouseMove, new IntPtr(mk), MakeLParam(x, y), target);
        }

        PostOrThrow("drag", InputEventKind.MouseDrag, target.TargetHwnd, up, IntPtr.Zero, MakeLParam(end.ClientX, end.ClientY), target);
        return new("drag", true, "Win32.PostMessage", FormatHwnd(target.TargetHwnd), Details: BackgroundDetails(dispatch, InputEventKind.MouseDrag, target, new Dictionary<string, object?> { ["fromX"] = fromX, ["fromY"] = fromY, ["toX"] = toX, ["toY"] = toY }));
    }

    private ActionResult ForegroundClick(int x, int y, string button, InputDispatchMode dispatch, int? windowHandle, PbwError? backgroundError) =>
        RunForeground("click", InputEventKind.MouseClick, dispatch, ResolveForegroundTarget(windowHandle, x, y), backgroundError, false, details =>
        {
            backend.SetCursorPos(x, y);
            var (down, up) = MouseEventFlags(button);
            backend.MouseEvent(down, 0, 0, 0, UIntPtr.Zero);
            backend.MouseEvent(up, 0, 0, 0, UIntPtr.Zero);
            details["x"] = x;
            details["y"] = y;
            details["button"] = button;
        });

    private ActionResult ForegroundMove(int x, int y, InputDispatchMode dispatch, int? windowHandle, PbwError? backgroundError)
    {
        var ok = backend.SetCursorPos(x, y);
        var details = ForegroundBaseDetails(dispatch, InputEventKind.MouseMove, ResolveForegroundTarget(windowHandle, x, y), backgroundError);
        details["x"] = x;
        details["y"] = y;
        details["foregroundChanged"] = false;
        details["foregroundRestored"] = false;
        return new("move", ok, "SetCursorPos.foreground", Details: details);
    }

    private ActionResult ForegroundTypeText(string text, InputDispatchMode dispatch, int? windowHandle, PbwError? backgroundError) =>
        RunForeground("type", InputEventKind.TextInput, dispatch, ResolveForegroundTarget(windowHandle), backgroundError, windowHandle is not null, details =>
        {
            foreach (var c in text)
            {
                var vk = backend.VkKeyScan(c);
                if (vk == -1) continue;
                var needsShift = (vk & 0x0100) != 0;
                if (needsShift) backend.KeybdEvent(0x10, 0, 0, UIntPtr.Zero);
                PressVirtualKey((byte)(vk & 0xff));
                if (needsShift) backend.KeybdEvent(0x10, 0, KeyEventUp, UIntPtr.Zero);
            }

            details["length"] = text.Length;
        });

    private ActionResult ForegroundPress(string key, InputDispatchMode dispatch, int? windowHandle, PbwError? backgroundError) =>
        RunForeground("press", InputEventKind.Keystroke, dispatch, ResolveForegroundTarget(windowHandle), backgroundError, windowHandle is not null, details =>
        {
            PressVirtualKey(KeyToVirtualKey(key));
            details["key"] = key;
        });

    private ActionResult ForegroundHotkey(IReadOnlyList<string> keys, InputDispatchMode dispatch, int? windowHandle, PbwError? backgroundError) =>
        RunForeground("hotkey", InputEventKind.KeyCombo, dispatch, ResolveForegroundTarget(windowHandle), backgroundError, windowHandle is not null, details =>
        {
            var virtualKeys = keys.Select(KeyToVirtualKey).ToArray();
            foreach (var key in virtualKeys) backend.KeybdEvent(key, 0, 0, UIntPtr.Zero);
            foreach (var key in virtualKeys.Reverse()) backend.KeybdEvent(key, 0, KeyEventUp, UIntPtr.Zero);
            details["keys"] = keys;
        });

    private ActionResult ForegroundScroll(int delta, int? x, int? y, InputDispatchMode dispatch, int? windowHandle, PbwError? backgroundError) =>
        RunForeground("scroll", InputEventKind.MouseScroll, dispatch, ResolveForegroundTarget(windowHandle, x, y), backgroundError, false, details =>
        {
            if (x is not null && y is not null) backend.SetCursorPos(x.Value, y.Value);
            backend.MouseEvent(MouseEventWheel, 0, 0, delta, UIntPtr.Zero);
            details["delta"] = delta;
            details["x"] = x;
            details["y"] = y;
        });

    private ActionResult ForegroundDrag(int fromX, int fromY, int toX, int toY, InputDispatchMode dispatch, int? windowHandle, PbwError? backgroundError) =>
        RunForeground("drag", InputEventKind.MouseDrag, dispatch, ResolveForegroundTarget(windowHandle, fromX, fromY), backgroundError, false, details =>
        {
            backend.SetCursorPos(fromX, fromY);
            backend.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            backend.SetCursorPos(toX, toY);
            backend.MouseEvent(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            details["fromX"] = fromX;
            details["fromY"] = fromY;
            details["toX"] = toX;
            details["toY"] = toY;
        });

    private ActionResult RunForeground(
        string action,
        InputEventKind kind,
        InputDispatchMode dispatch,
        IntPtr target,
        PbwError? backgroundError,
        bool requireForeground,
        Action<Dictionary<string, object?>> send)
    {
        var details = ForegroundBaseDetails(dispatch, kind, target, backgroundError);
        var previous = backend.GetForegroundWindow();
        details["previousForegroundHwnd"] = previous == IntPtr.Zero ? null : FormatHwnd(previous);
        var setAttempted = target != IntPtr.Zero && previous != target;
        details["setForegroundAttempted"] = setAttempted;
        var setOk = true;
        if (setAttempted)
        {
            setOk = backend.SetForegroundWindow(target);
            Thread.Sleep(8);
        }

        var afterSet = backend.GetForegroundWindow();
        var targetIsForeground = target == IntPtr.Zero || afterSet == target;
        details["setForegroundSucceeded"] = setOk && targetIsForeground;
        details["foregroundAfterSetHwnd"] = afterSet == IntPtr.Zero ? null : FormatHwnd(afterSet);
        details["foregroundChanged"] = previous != afterSet;

        if (requireForeground && !targetIsForeground)
        {
            return new ActionResult(action, false, "Win32Input.foreground", target == IntPtr.Zero ? null : FormatHwnd(target), "Foreground dispatch was requested, but Windows did not make the target foreground. Input was not sent.", details);
        }

        send(details);
        Thread.Sleep(20);
        var afterInput = backend.GetForegroundWindow();
        details["foregroundAfterInputHwnd"] = afterInput == IntPtr.Zero ? null : FormatHwnd(afterInput);

        var restoreAttempted = target != IntPtr.Zero && previous != IntPtr.Zero && previous != target;
        var restored = false;
        if (restoreAttempted)
        {
            backend.SetForegroundWindow(previous);
            Thread.Sleep(8);
            restored = backend.GetForegroundWindow() == previous;
        }

        details["restoreForegroundAttempted"] = restoreAttempted;
        details["foregroundRestored"] = restored;
        return new ActionResult(action, true, "Win32Input.foreground", target == IntPtr.Zero ? null : FormatHwnd(target), Details: details);
    }

    private MessageTarget ResolveMouseTarget(string action, InputEventKind kind, int? windowHandle, int screenX, int screenY)
    {
        var root = ResolveRootWindow(windowHandle, screenX, screenY);
        if (root == IntPtr.Zero)
        {
            throw BackgroundUnavailable(action, kind, IntPtr.Zero, "<unknown>", "A target HWND could not be resolved for background mouse dispatch.");
        }

        var (target, clientX, clientY) = DeepestChildFromScreenPoint(root, screenX, screenY);
        var className = backend.GetClassName(root);
        var messageTarget = new MessageTarget(root, target, className, clientX, clientY);
        EnsureBackgroundAvailable(action, kind, messageTarget);
        return messageTarget;
    }

    private MessageTarget ResolveWindowTarget(string action, InputEventKind kind, int? windowHandle)
    {
        if (windowHandle is null)
        {
            throw BackgroundUnavailable(action, kind, IntPtr.Zero, "<unknown>", "Background dispatch requires --hwnd for this command.");
        }

        var hwnd = new IntPtr(windowHandle.Value);
        if (hwnd == IntPtr.Zero || !backend.IsWindow(hwnd))
        {
            throw BackgroundUnavailable(action, kind, hwnd, "<unknown>", "The requested HWND is not a valid window.");
        }

        var root = backend.GetRootWindow(hwnd);
        var target = new MessageTarget(root, hwnd, backend.GetClassName(root), 0, 0);
        EnsureBackgroundAvailable(action, kind, target);
        return target;
    }

    private IntPtr ResolveForegroundTarget(int? windowHandle, int? screenX = null, int? screenY = null)
    {
        if (windowHandle is not null)
        {
            var hwnd = new IntPtr(windowHandle.Value);
            return hwnd == IntPtr.Zero ? IntPtr.Zero : backend.GetRootWindow(hwnd);
        }

        if (screenX is not null && screenY is not null)
        {
            var hit = backend.WindowFromPoint(screenX.Value, screenY.Value);
            return hit == IntPtr.Zero ? IntPtr.Zero : backend.GetRootWindow(hit);
        }

        return IntPtr.Zero;
    }

    private IntPtr ResolveRootWindow(int? windowHandle, int screenX, int screenY)
    {
        if (windowHandle is not null)
        {
            var hwnd = new IntPtr(windowHandle.Value);
            return hwnd != IntPtr.Zero && backend.IsWindow(hwnd) ? backend.GetRootWindow(hwnd) : IntPtr.Zero;
        }

        var hit = backend.WindowFromPoint(screenX, screenY);
        return hit == IntPtr.Zero ? IntPtr.Zero : backend.GetRootWindow(hit);
    }

    private (IntPtr Hwnd, int ClientX, int ClientY) DeepestChildFromScreenPoint(IntPtr root, int screenX, int screenY)
    {
        var current = root;
        for (var depth = 0; depth < 16; depth++)
        {
            var x = screenX;
            var y = screenY;
            backend.ScreenToClient(current, ref x, ref y);
            var child = backend.ChildWindowFromPointEx(current, x, y, ChildWindowSkipInvisibleDisabledTransparent);
            if (child == IntPtr.Zero || child == current)
            {
                break;
            }

            if (child != root && !backend.IsChild(root, child))
            {
                break;
            }

            current = child;
        }

        var client = ToClient(current, screenX, screenY);
        return (current, client.ClientX, client.ClientY);
    }

    private (int ClientX, int ClientY) ToClient(IntPtr hwnd, int screenX, int screenY)
    {
        var x = screenX;
        var y = screenY;
        backend.ScreenToClient(hwnd, ref x, ref y);
        return (x, y);
    }

    private void EnsureBackgroundAvailable(string action, InputEventKind kind, MessageTarget target)
    {
        if (WouldDropBackground(target.ClassName, kind, out var reason))
        {
            throw BackgroundUnavailable(action, kind, target.RootHwnd, target.ClassName, reason);
        }
    }

    private static bool WouldDropBackground(string className, InputEventKind kind, out string reason)
    {
        if (className.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("CefBrowser", StringComparison.OrdinalIgnoreCase))
        {
            if (kind is InputEventKind.MouseClick or InputEventKind.MouseMove or InputEventKind.MouseScroll or InputEventKind.KeyCombo)
            {
                reason = "Chromium/Electron windows are known to ignore these posted input messages.";
                return true;
            }
        }

        if (className.StartsWith("gdkWindow", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("gdkSurface", StringComparison.OrdinalIgnoreCase))
        {
            if (kind == InputEventKind.MouseClick)
            {
                reason = "GTK toplevel windows are known to ignore posted button clicks for many widgets.";
                return true;
            }
        }

        if (className.StartsWith("SAL", StringComparison.OrdinalIgnoreCase))
        {
            if (kind is InputEventKind.Keystroke or InputEventKind.KeyCombo)
            {
                reason = "VCL/SAL windows route accelerators through key state that PostMessage does not update.";
                return true;
            }
        }

        if (className is "ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow" or "WinUIDesktopWin32WindowClass" or "Microsoft.UI.Content.DesktopChildSiteBridge")
        {
            if (kind is InputEventKind.TextInput or InputEventKind.Keystroke or InputEventKind.KeyCombo)
            {
                reason = "XAML/UWP input dispatchers consume system input queue events and commonly ignore posted keyboard messages.";
                return true;
            }
        }

        if (className.StartsWith("HwndWrapper", StringComparison.OrdinalIgnoreCase) && kind == InputEventKind.MouseDrag)
        {
            reason = "WPF drag handlers commonly poll button state that PostMessage does not update.";
            return true;
        }

        reason = "";
        return false;
    }

    private PbwException BackgroundUnavailable(string action, InputEventKind kind, IntPtr hwnd, string targetClass, string reason)
    {
        var details = new Dictionary<string, object?>
        {
            ["dispatch"] = "background",
            ["eventKind"] = InputDispatchPolicy.ToWireString(kind),
            ["targetHwnd"] = hwnd == IntPtr.Zero ? null : FormatHwnd(hwnd),
            ["targetClass"] = targetClass,
            ["reason"] = reason,
            ["suggestion"] = "Retry with --dispatch foreground, or target a semantic UIA action when available."
        };
        return new PbwException(new PbwError(
            "background_unavailable",
            $"Background dispatch is not available for {action}: {reason}",
            action,
            details,
            "Retry with --dispatch foreground if foreground input is acceptable."));
    }

    private void PostOrThrow(string action, InputEventKind kind, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, MessageTarget target)
    {
        if (!backend.PostMessage(hwnd, message, wParam, lParam))
        {
            throw BackgroundUnavailable(action, kind, target.RootHwnd, target.ClassName, "PostMessage failed: " + backend.LastErrorMessage());
        }
    }

    private void PostKey(string action, InputEventKind kind, MessageTarget target, byte virtualKey)
    {
        PostKeyDown(action, kind, target, virtualKey);
        Thread.Sleep(4);
        PostKeyUp(action, kind, target, virtualKey);
    }

    private void PostKeyDown(string action, InputEventKind kind, MessageTarget target, byte virtualKey)
    {
        var lParam = KeyLParam(virtualKey, keyUp: false);
        PostOrThrow(action, kind, target.TargetHwnd, WmKeyDown, new IntPtr(virtualKey), lParam, target);
    }

    private void PostKeyUp(string action, InputEventKind kind, MessageTarget target, byte virtualKey)
    {
        var lParam = KeyLParam(virtualKey, keyUp: true);
        PostOrThrow(action, kind, target.TargetHwnd, WmKeyUp, new IntPtr(virtualKey), lParam, target);
    }

    private IntPtr KeyLParam(byte virtualKey, bool keyUp)
    {
        var scan = backend.MapVirtualKey(virtualKey, 0);
        var value = 1u | (scan << 16);
        if (keyUp)
        {
            value |= 1u << 30;
            value |= 1u << 31;
        }

        return new IntPtr(unchecked((int)value));
    }

    private static Dictionary<string, object?> BackgroundDetails(InputDispatchMode dispatch, InputEventKind kind, MessageTarget target, Dictionary<string, object?> extra)
    {
        var details = new Dictionary<string, object?>
        {
            ["dispatch"] = InputDispatchPolicy.ToWireString(dispatch),
            ["actualDispatch"] = "background",
            ["eventKind"] = InputDispatchPolicy.ToWireString(kind),
            ["targetHwnd"] = FormatHwnd(target.TargetHwnd),
            ["rootHwnd"] = FormatHwnd(target.RootHwnd),
            ["targetClass"] = target.ClassName,
            ["clientX"] = target.ClientX,
            ["clientY"] = target.ClientY,
            ["foregroundChanged"] = false,
            ["foregroundRestored"] = false
        };
        foreach (var (key, value) in extra)
        {
            details[key] = value;
        }

        return details;
    }

    private static Dictionary<string, object?> ForegroundBaseDetails(InputDispatchMode dispatch, InputEventKind kind, IntPtr target, PbwError? backgroundError)
    {
        var details = new Dictionary<string, object?>
        {
            ["dispatch"] = InputDispatchPolicy.ToWireString(dispatch),
            ["actualDispatch"] = "foreground",
            ["eventKind"] = InputDispatchPolicy.ToWireString(kind),
            ["targetHwnd"] = target == IntPtr.Zero ? null : FormatHwnd(target)
        };
        if (backgroundError is not null)
        {
            details["backgroundFallback"] = new Dictionary<string, object?>
            {
                ["code"] = backgroundError.Code,
                ["message"] = backgroundError.Message,
                ["details"] = backgroundError.Details
            };
        }

        return details;
    }

    private void PressVirtualKey(byte key)
    {
        backend.KeybdEvent(key, 0, 0, UIntPtr.Zero);
        backend.KeybdEvent(key, 0, KeyEventUp, UIntPtr.Zero);
    }

    private byte KeyToVirtualKey(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11,
            "shift" => 0x10,
            "alt" or "menu" => 0x12,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "tab" => 0x09,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "space" => 0x20,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "f1" => 0x70,
            "f2" => 0x71,
            "f3" => 0x72,
            "f4" => 0x73,
            "f5" => 0x74,
            "f6" => 0x75,
            "f7" => 0x76,
            "f8" => 0x77,
            "f9" => 0x78,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            _ when key.Length == 1 => ScanSingleCharacterKey(key[0]),
            _ => throw new ArgumentException($"Unsupported key '{key}'.")
        };
    }

    private byte ScanSingleCharacterKey(char character)
    {
        var scan = backend.VkKeyScan(character);
        if (scan == -1)
        {
            throw new ArgumentException($"Unsupported key '{character}'.");
        }

        return (byte)(scan & 0xff);
    }

    private static (uint Down, uint Up, ushort Mk) MouseMessages(string button) => button.ToLowerInvariant() switch
    {
        "right" => (WmRButtonDown, WmRButtonUp, MkRightButton),
        "middle" => (WmMButtonDown, WmMButtonUp, MkMiddleButton),
        _ => (WmLButtonDown, WmLButtonUp, MkLeftButton)
    };

    private static (int Down, int Up) MouseEventFlags(string button) => button.ToLowerInvariant() switch
    {
        "right" => (MouseEventRightDown, MouseEventRightUp),
        "middle" => (MouseEventMiddleDown, MouseEventMiddleUp),
        _ => (MouseEventLeftDown, MouseEventLeftUp)
    };

    private static IntPtr MakeLParam(int x, int y) =>
        new(unchecked((int)(((uint)(ushort)y << 16) | (ushort)x)));

    private static IntPtr MakeWheelWParam(int delta) =>
        new(unchecked((int)((uint)(ushort)delta << 16)));

    private static string FormatHwnd(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("x");

    private sealed record MessageTarget(IntPtr RootHwnd, IntPtr TargetHwnd, string ClassName, int ClientX, int ClientY);
}

public sealed class WindowsElementAutomationService : IElementAutomationService
{
    private const int MaxDepth = 5;
    private const int MaxChildrenPerNode = 80;
    private const int MaxTotalElements = 1500;
    private static readonly TimeSpan DefaultTreeReadTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultMsaaFallbackTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeSpan treeReadTimeout;
    private readonly TimeSpan msaaFallbackTimeout;
    private readonly Func<IReadOnlyList<ElementSnapshot>> readTreeCore;
    private readonly Func<TargetSpec, AutomationElement?> findElementCore;
    private readonly Func<string, AutomationElement?, ActionResult>? semanticClickCore;
    private readonly IWindowsMsaaAutomationAdapter? msaa;

    public WindowsElementAutomationService()
        : this(DefaultTreeReadTimeout, null, new WindowsMsaaAutomationAdapter(), DefaultMsaaFallbackTimeout)
    {
    }

    internal WindowsElementAutomationService(
        TimeSpan treeReadTimeout,
        Func<IReadOnlyList<ElementSnapshot>>? readTreeCore,
        IWindowsMsaaAutomationAdapter? msaa = null,
        TimeSpan? msaaFallbackTimeout = null,
        Func<TargetSpec, AutomationElement?>? findElementCore = null,
        Func<string, AutomationElement?, ActionResult>? semanticClickCore = null)
    {
        this.treeReadTimeout = treeReadTimeout <= TimeSpan.Zero ? DefaultTreeReadTimeout : treeReadTimeout;
        this.msaaFallbackTimeout = msaaFallbackTimeout is null || msaaFallbackTimeout <= TimeSpan.Zero
            ? DefaultMsaaFallbackTimeout
            : msaaFallbackTimeout.Value;
        this.readTreeCore = readTreeCore ?? ReadTreeCore;
        this.findElementCore = findElementCore ?? FindElement;
        this.semanticClickCore = semanticClickCore;
        this.msaa = msaa;
    }

    public IReadOnlyList<ElementSnapshot> ReadTree()
    {
        try
        {
            var task = Task.Run(readTreeCore);
            if (task.Wait(treeReadTimeout))
            {
                return WithMsaaFallback(task.GetAwaiter().GetResult(), null, "uia_tree_degraded");
            }

            return WithMsaaFallback(new[]
            {
                DegradedElement(
                    "uia-timeout",
                    $"UI Automation tree read exceeded {treeReadTimeout.TotalMilliseconds:0} ms.",
                    "timeout",
                    new Dictionary<string, object?> { ["timeoutMs"] = (int)treeReadTimeout.TotalMilliseconds })
            }, null, "uia_timeout");
        }
        catch (AggregateException ex)
        {
            var inner = ex.Flatten().InnerException ?? ex;
            return WithMsaaFallback(new[] { DegradedElement("uia-error", inner.Message, "exception", ExceptionDetails(inner)) }, null, "uia_exception");
        }
        catch (Exception ex)
        {
            return WithMsaaFallback(new[] { DegradedElement("uia-error", ex.Message, "exception", ExceptionDetails(ex)) }, null, "uia_exception");
        }
    }

    public ActionResult Click(TargetSpec target)
    {
        var element = findElementCore(target);
        var uia = element is null ? NotFound("click", target) : TrySemanticClick("click", element);
        return uia.Performed ? uia : WithMsaaActionFallback(target, uia, adapter => adapter.Click(target));
    }

    public ActionResult SetValue(TargetSpec target, string value)
    {
        var element = findElementCore(target);
        if (element is null) return NotFound("set-value", target);
        if (TryGetCurrentPattern<RangeValuePattern>(element, RangeValuePattern.Pattern, out var rangePattern))
        {
            return SetRangeValue(element, rangePattern, value);
        }

        if (TryGetCurrentPattern<ValuePattern>(element, ValuePattern.Pattern, out var valuePattern))
        {
            var details = SemanticDetails(element, "ValuePattern", "UIAutomation.ValuePattern");
            details["valueLength"] = value.Length;
            try
            {
                if (valuePattern.Current.IsReadOnly)
                {
                    details["errorCode"] = "read_only";
                    return new ActionResult("set-value", false, "UIAutomation.ValuePattern", ElementId(element), "Value target is read-only.", details);
                }

                valuePattern.SetValue(value);
                return new ActionResult("set-value", true, "UIAutomation.ValuePattern", ElementId(element), Details: details);
            }
            catch (Exception ex)
            {
                details["errorCode"] = "uia_provider_error";
                details["exceptionType"] = ex.GetType().Name;
                return new ActionResult("set-value", false, "UIAutomation.ValuePattern", ElementId(element), ex.Message, details);
            }
        }

        return SemanticUnavailable("set-value", element, "Target does not support ValuePattern or RangeValuePattern.", "value_pattern_unavailable");
    }

    public ActionResult PerformAction(TargetSpec target, string action)
    {
        var element = findElementCore(target);
        var normalized = action.ToLowerInvariant();
        if (element is null && !(normalized == "click" && semanticClickCore is not null))
        {
            var notFound = NotFound("perform-action", target);
            return WithMsaaActionFallback(target, notFound, adapter => adapter.PerformAction(target, action));
        }

        var uiaElement = element!;
        try
        {
            switch (normalized)
            {
                case "invoke":
                    if (TryGetCurrentPattern<InvokePattern>(uiaElement, InvokePattern.Pattern, out var invokePattern))
                    {
                        invokePattern.Invoke();
                        return SemanticSuccess("perform-action", uiaElement, "InvokePattern");
                    }
                    break;
                case "click":
                    var clickResult = semanticClickCore is null
                        ? TrySemanticClick("perform-action", uiaElement)
                        : semanticClickCore("perform-action", element);
                    return clickResult.Performed
                        ? clickResult
                        : WithMsaaActionFallback(target, clickResult, adapter => adapter.PerformAction(target, action));
                case "toggle":
                    if (TryGetCurrentPattern<TogglePattern>(uiaElement, TogglePattern.Pattern, out var togglePattern))
                    {
                        togglePattern.Toggle();
                        return SemanticSuccess("perform-action", uiaElement, "TogglePattern");
                    }
                    break;
                case "select":
                    if (TryGetCurrentPattern<SelectionItemPattern>(uiaElement, SelectionItemPattern.Pattern, out var selectionPattern))
                    {
                        selectionPattern.Select();
                        return SemanticSuccess("perform-action", uiaElement, "SelectionItemPattern");
                    }
                    break;
                case "expand":
                case "collapse":
                    if (TryGetCurrentPattern<ExpandCollapsePattern>(uiaElement, ExpandCollapsePattern.Pattern, out var expandPattern))
                    {
                        if (normalized == "expand") expandPattern.Expand(); else expandPattern.Collapse();
                        return SemanticSuccess(
                            "perform-action",
                            uiaElement,
                            "ExpandCollapsePattern",
                            new Dictionary<string, object?> { ["expandCollapseAction"] = normalized });
                    }
                    break;
                case "scroll-into-view":
                case "scrollintoview":
                    if (TryGetCurrentPattern<ScrollItemPattern>(uiaElement, ScrollItemPattern.Pattern, out var scrollPattern))
                    {
                        scrollPattern.ScrollIntoView();
                        return SemanticSuccess("perform-action", uiaElement, "ScrollItemPattern");
                    }
                    break;
                case "focus":
                case "set-focus":
                    uiaElement.SetFocus();
                    return SemanticSuccess("perform-action", uiaElement, "SetFocus");
            }
        }
        catch (Exception ex)
        {
            var providerError = element is null
                ? new ActionResult(
                    "perform-action",
                    false,
                    "UIAutomation",
                    target.ToString(),
                    ex.Message,
                    MergeDetails(ExceptionDetails(ex), new Dictionary<string, object?> { ["fallbackReason"] = "uia_provider_error" }))
                : SemanticProviderError("perform-action", element, ex);
            return WithMsaaActionFallback(target, providerError, adapter => adapter.PerformAction(target, action));
        }

        var unavailable = SemanticUnavailable("perform-action", uiaElement, $"Target does not support action '{action}'.", "semantic_action_unavailable");
        return WithMsaaActionFallback(target, unavailable, adapter => adapter.PerformAction(target, action));
    }

    private IReadOnlyList<ElementSnapshot> WithMsaaFallback(
        IReadOnlyList<ElementSnapshot> uiaElements,
        int? windowHandle,
        string reason)
    {
        if (msaa is null)
        {
            return uiaElements;
        }

        if (ShouldAttemptMsaaTreeFallback(uiaElements))
        {
            var msaaElements = RunMsaaTreeRead(reason, adapter => adapter.ReadTree(windowHandle));
            return MsaaReadSucceeded(msaaElements)
                ? msaaElements
                : uiaElements.Concat(msaaElements).ToArray();
        }

        if (windowHandle is null && msaa.HasKnownLegacyWindows())
        {
            var legacyElements = RunMsaaTreeRead("known_legacy_window", adapter => adapter.ReadLegacyWindowTrees());
            return MsaaReadSucceeded(legacyElements)
                ? uiaElements.Concat(legacyElements).ToArray()
                : uiaElements;
        }

        return uiaElements;
    }

    internal static bool ShouldAttemptMsaaTreeFallback(IReadOnlyList<ElementSnapshot> uiaElements) =>
        uiaElements.Count == 0 || uiaElements.All(IsDegradedElement) || IsWrapperOnlyTree(uiaElements);

    private IReadOnlyList<ElementSnapshot> RunMsaaTreeRead(
        string reason,
        Func<IWindowsMsaaAutomationAdapter, IReadOnlyList<ElementSnapshot>> read)
    {
        if (msaa is null)
        {
            return Array.Empty<ElementSnapshot>();
        }

        try
        {
            var task = Task.Run(() => read(msaa));
            if (!task.Wait(msaaFallbackTimeout))
            {
                return new[]
                {
                    DegradedElement(
                        "msaa-timeout",
                        $"MSAA fallback tree read exceeded {msaaFallbackTimeout.TotalMilliseconds:0} ms.",
                        "msaa_timeout",
                        new Dictionary<string, object?>
                        {
                            ["source"] = "msaa",
                            ["fallbackFrom"] = reason,
                            ["timeoutMs"] = (int)msaaFallbackTimeout.TotalMilliseconds
                        })
                };
            }

            var elements = task.GetAwaiter().GetResult();
            return elements.Count == 0
                ? new[]
                {
                    DegradedElement(
                        "msaa-empty",
                        "MSAA fallback returned no elements.",
                        "msaa_empty",
                        new Dictionary<string, object?>
                        {
                            ["source"] = "msaa",
                            ["fallbackFrom"] = reason
                        })
                }
                : elements;
        }
        catch (AggregateException ex)
        {
            var inner = ex.Flatten().InnerException ?? ex;
            return new[]
            {
                DegradedElement(
                    "msaa-error",
                    inner.Message,
                    "msaa_exception",
                    MergeDetails(ExceptionDetails(inner), new Dictionary<string, object?> { ["source"] = "msaa", ["fallbackFrom"] = reason }))
            };
        }
        catch (Exception ex)
        {
            return new[]
            {
                DegradedElement(
                    "msaa-error",
                    ex.Message,
                    "msaa_exception",
                    MergeDetails(ExceptionDetails(ex), new Dictionary<string, object?> { ["source"] = "msaa", ["fallbackFrom"] = reason }))
            };
        }
    }

    private ActionResult WithMsaaActionFallback(
        TargetSpec target,
        ActionResult uiaResult,
        Func<IWindowsMsaaAutomationAdapter, ActionResult> msaaAction)
    {
        if (msaa is null)
        {
            return uiaResult;
        }

        ActionResult msaaResult;
        try
        {
            var task = Task.Run(() => msaaAction(msaa));
            if (!task.Wait(msaaFallbackTimeout))
            {
                msaaResult = new ActionResult(
                    uiaResult.Action,
                    false,
                    "MSAA",
                    target.ToString(),
                    $"MSAA fallback action exceeded {msaaFallbackTimeout.TotalMilliseconds:0} ms.",
                    new Dictionary<string, object?>
                    {
                        ["elementSource"] = "msaa",
                        ["fallbackReason"] = "msaa_timeout",
                        ["timeoutMs"] = (int)msaaFallbackTimeout.TotalMilliseconds
                    });
            }
            else
            {
                msaaResult = task.GetAwaiter().GetResult();
            }
        }
        catch (AggregateException ex)
        {
            var inner = ex.Flatten().InnerException ?? ex;
            msaaResult = new ActionResult(
                uiaResult.Action,
                false,
                "MSAA",
                target.ToString(),
                inner.Message,
                MergeDetails(ExceptionDetails(inner), new Dictionary<string, object?> { ["elementSource"] = "msaa", ["fallbackReason"] = "msaa_exception" }));
        }
        catch (Exception ex)
        {
            msaaResult = new ActionResult(
                uiaResult.Action,
                false,
                "MSAA",
                target.ToString(),
                ex.Message,
                MergeDetails(ExceptionDetails(ex), new Dictionary<string, object?> { ["elementSource"] = "msaa", ["fallbackReason"] = "msaa_exception" }));
        }

        return msaaResult.Performed ? WithUiaFallbackDetails(msaaResult, uiaResult) : WithMsaaFallbackDetails(uiaResult, msaaResult);
    }

    private static bool MsaaReadSucceeded(IReadOnlyList<ElementSnapshot> elements) =>
        elements.Count > 0 && elements.Any(e => !IsDegradedElement(e));

    private static bool IsDegradedElement(ElementSnapshot element) =>
        element.Metadata is not null &&
        element.Metadata.TryGetValue("degraded", out var degraded) &&
        degraded is bool b &&
        b;

    private static bool IsWrapperOnlyTree(IReadOnlyList<ElementSnapshot> elements)
    {
        if (elements.Count != 1)
        {
            return false;
        }

        var element = elements[0];
        var childCount = element.Children?.Count ?? 0;
        var patternCount = element.Patterns?.Count ?? 0;
        return childCount == 0 &&
            patternCount == 0 &&
            string.IsNullOrWhiteSpace(element.Name) &&
            element.Role is "Window" or "Pane" or "Custom" or "unknown";
    }

    private static ActionResult WithUiaFallbackDetails(ActionResult result, ActionResult uiaAttempt)
    {
        var details = result.Details is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(result.Details);
        details["uiaAttempted"] = true;
        details["uiaPerformed"] = uiaAttempt.Performed;
        details["uiaMethod"] = uiaAttempt.Method;
        details["uiaFallbackReason"] = ActionFallbackReason(uiaAttempt, "uia_unavailable");
        details["finalMethod"] = result.Method;
        return result with { Details = details };
    }

    private static ActionResult WithMsaaFallbackDetails(ActionResult result, ActionResult msaaAttempt)
    {
        var details = result.Details is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(result.Details);
        details["msaaAttempted"] = true;
        details["msaaPerformed"] = msaaAttempt.Performed;
        details["msaaMethod"] = msaaAttempt.Method;
        details["msaaFallbackReason"] = ActionFallbackReason(msaaAttempt, "msaa_unavailable");
        details["finalMethod"] = result.Method;
        return result with { Details = details };
    }

    private static object? ActionFallbackReason(ActionResult attempt, string defaultReason)
    {
        if (attempt.Details is not null && attempt.Details.TryGetValue("fallbackReason", out var reason))
        {
            return reason;
        }

        return string.IsNullOrWhiteSpace(attempt.Message) ? defaultReason : attempt.Message;
    }

    public IReadOnlyList<MenuItemInfo> ListMenus(TargetSpec target)
    {
        var root = FindElement(target) ?? RootElement();
        return EnumerateElements(root, includeRoot: false)
            .Where(e => ControlTypeEquals(e, ControlType.MenuItem))
            .Take(200)
            .Select(e => new MenuItemInfo(SafeName(e), SafeBool(e, AutomationElement.IsEnabledProperty), ElementId(e)))
            .ToArray();
    }

    public ActionResult ClickMenu(TargetSpec target, string menu)
    {
        var item = EnumerateElements(RootElement(), includeRoot: false)
            .Where(e => ControlTypeEquals(e, ControlType.MenuItem))
            .FirstOrDefault(e => SafeName(e).Contains(menu, StringComparison.OrdinalIgnoreCase));
        if (item is null) return new ActionResult("menu.click", false, "UIAutomation", menu, "Menu item was not found.");
        return InvokeElement("menu.click", item);
    }

    public IReadOnlyList<DialogInfo> ListDialogs()
    {
        var diagnostics = new UiaTraversalDiagnostics();
        return FindChildren(RootElement(), diagnostics)
            .Cast<AutomationElement>()
            .Where(e => ControlTypeEquals(e, ControlType.Window) && SafeName(e).Length > 0)
            .Select(e => new DialogInfo(ElementId(e), SafeName(e), Children(e, 0, new UiaTraversalState(MaxTotalElements), diagnostics)))
            .ToArray();
    }

    public ActionResult ClickDialog(TargetSpec target, string button)
    {
        var dialog = FindElement(target) ?? RootElement();
        var buttonElement = EnumerateElements(dialog, includeRoot: false)
            .Where(e => ControlTypeEquals(e, ControlType.Button))
            .FirstOrDefault(e => SafeName(e).Contains(button, StringComparison.OrdinalIgnoreCase));
        if (buttonElement is null) return new ActionResult("dialog.click", false, "UIAutomation", button, "Dialog button was not found.");
        return InvokeElement("dialog.click", buttonElement);
    }

    public ActionResult InputDialog(TargetSpec target, string value)
    {
        var dialog = FindElement(target) ?? RootElement();
        var edit = EnumerateElements(dialog, includeRoot: false).FirstOrDefault(e => ControlTypeEquals(e, ControlType.Edit));
        if (edit is null) return new ActionResult("dialog.input", false, "UIAutomation", null, "Dialog input target was not found.");
        if (TryGetCurrentPattern<ValuePattern>(edit, ValuePattern.Pattern, out var valuePattern))
        {
            valuePattern.SetValue(value);
            return new ActionResult("dialog.input", true, "UIAutomation.ValuePattern", ElementId(edit));
        }
        return new ActionResult("dialog.input", false, "UIAutomation", ElementId(edit), "Dialog input does not support ValuePattern.");
    }

    public ActionResult DismissDialog(TargetSpec target)
    {
        var dialog = FindElement(target);
        if (dialog is null) return new ActionResult("dialog.dismiss", false, "UIAutomation", null, "Dialog target was not found.");
        var close = EnumerateElements(dialog, includeRoot: false)
            .Where(e => ControlTypeEquals(e, ControlType.Button))
            .FirstOrDefault(e => SafeName(e).Equals("Cancel", StringComparison.OrdinalIgnoreCase) || SafeName(e).Equals("Close", StringComparison.OrdinalIgnoreCase));
        return close is null ? PerformAction(target, "focus") : InvokeElement("dialog.dismiss", close);
    }

    private static ActionResult InvokeElement(string action, AutomationElement element)
    {
        if (TryGetCurrentPattern<InvokePattern>(element, InvokePattern.Pattern, out var invoke))
        {
            invoke.Invoke();
            return SemanticSuccess(action, element, "InvokePattern");
        }
        return SemanticUnavailable(action, element, "Target does not support InvokePattern.", "invoke_pattern_unavailable");
    }

    private static ActionResult TrySemanticClick(string action, AutomationElement element)
    {
        var extra = new Dictionary<string, object?>();
        var preActions = new List<string>();
        var providerErrors = new List<IReadOnlyDictionary<string, object?>>();
        TryScrollIntoViewBeforeClick(element, preActions, providerErrors);
        if (preActions.Count > 0)
        {
            extra["preActions"] = preActions;
        }

        if (TryGetCurrentPattern<InvokePattern>(element, InvokePattern.Pattern, out var invokePattern))
        {
            try
            {
                invokePattern.Invoke();
                return SemanticSuccess(action, element, "InvokePattern", extra);
            }
            catch (Exception ex)
            {
                providerErrors.Add(SemanticPatternError("InvokePattern", ex));
            }
        }

        if (TryGetCurrentPattern<TogglePattern>(element, TogglePattern.Pattern, out var togglePattern))
        {
            try
            {
                togglePattern.Toggle();
                return SemanticSuccess(action, element, "TogglePattern", extra);
            }
            catch (Exception ex)
            {
                providerErrors.Add(SemanticPatternError("TogglePattern", ex));
            }
        }

        if (TryGetCurrentPattern<SelectionItemPattern>(element, SelectionItemPattern.Pattern, out var selectionPattern))
        {
            try
            {
                selectionPattern.Select();
                return SemanticSuccess(action, element, "SelectionItemPattern", extra);
            }
            catch (Exception ex)
            {
                providerErrors.Add(SemanticPatternError("SelectionItemPattern", ex));
            }
        }

        if (TryGetCurrentPattern<ExpandCollapsePattern>(element, ExpandCollapsePattern.Pattern, out var expandPattern))
        {
            try
            {
                var expandAction = ToggleExpandCollapse(expandPattern);
                if (expandAction is not null)
                {
                    extra["expandCollapseAction"] = expandAction;
                    return SemanticSuccess(action, element, "ExpandCollapsePattern", extra);
                }
            }
            catch (Exception ex)
            {
                providerErrors.Add(SemanticPatternError("ExpandCollapsePattern", ex));
            }
        }

        if (providerErrors.Count > 0)
        {
            extra["providerErrors"] = providerErrors;
            return SemanticUnavailable(action, element, "Semantic UI Automation click patterns failed; falling back to input dispatch.", "semantic_pattern_failed", extra);
        }

        return SemanticUnavailable(action, element, "Target does not expose a clickable UI Automation pattern.", "semantic_pattern_unavailable", extra);
    }

    private static void TryScrollIntoViewBeforeClick(
        AutomationElement element,
        ICollection<string> preActions,
        ICollection<IReadOnlyDictionary<string, object?>> providerErrors)
    {
        if (!TryGetCurrentPattern<ScrollItemPattern>(element, ScrollItemPattern.Pattern, out var scrollPattern))
        {
            return;
        }

        try
        {
            scrollPattern.ScrollIntoView();
            preActions.Add("ScrollItemPattern");
        }
        catch (Exception ex)
        {
            providerErrors.Add(SemanticPatternError("ScrollItemPattern", ex));
        }
    }

    private static string? ToggleExpandCollapse(ExpandCollapsePattern expandPattern)
    {
        var state = expandPattern.Current.ExpandCollapseState;
        if (state == ExpandCollapseState.LeafNode)
        {
            return null;
        }

        if (state == ExpandCollapseState.Expanded)
        {
            expandPattern.Collapse();
            return "collapse";
        }

        expandPattern.Expand();
        return "expand";
    }

    private static ActionResult SemanticSuccess(
        string action,
        AutomationElement element,
        string semanticPattern,
        IReadOnlyDictionary<string, object?>? extraDetails = null)
    {
        var method = SemanticMethod(semanticPattern);
        return new ActionResult(action, true, method, ElementId(element), Details: SemanticDetails(element, semanticPattern, method, extraDetails));
    }

    private static ActionResult SemanticUnavailable(
        string action,
        AutomationElement element,
        string message,
        string fallbackReason,
        IReadOnlyDictionary<string, object?>? extraDetails = null)
    {
        var details = SemanticDetails(element, null, "UIAutomation", extraDetails);
        details["fallbackReason"] = fallbackReason;
        return new ActionResult(action, false, "UIAutomation", ElementId(element), message, details);
    }

    private static ActionResult SemanticProviderError(string action, AutomationElement element, Exception ex)
    {
        var details = SemanticDetails(element, null, "UIAutomation");
        details["fallbackReason"] = "uia_provider_error";
        details["errorCode"] = "uia_provider_error";
        details["exceptionType"] = ex.GetType().Name;
        return new ActionResult(action, false, "UIAutomation", ElementId(element), ex.Message, details);
    }

    private static Dictionary<string, object?> SemanticDetails(
        AutomationElement element,
        string? semanticPattern,
        string finalMethod,
        IReadOnlyDictionary<string, object?>? extraDetails = null)
    {
        var details = PatternDetails(element).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        details["finalMethod"] = finalMethod;
        if (semanticPattern is not null)
        {
            details["semanticPattern"] = semanticPattern;
        }

        if (extraDetails is not null)
        {
            foreach (var (key, value) in extraDetails)
            {
                details[key] = value;
            }
        }

        return details;
    }

    private static IReadOnlyDictionary<string, object?> SemanticPatternError(string pattern, Exception ex) => new Dictionary<string, object?>
    {
        ["semanticPattern"] = pattern,
        ["message"] = ex.Message,
        ["exceptionType"] = ex.GetType().Name
    };

    private static string SemanticMethod(string semanticPattern) =>
        semanticPattern == "SetFocus" ? "UIAutomation.SetFocus" : "UIAutomation." + semanticPattern;

    private AutomationElement? FindElement(TargetSpec target)
    {
        if (target.X is not null && target.Y is not null)
        {
            var pointElement = ElementFromPoint(target.X.Value, target.Y.Value, target.WindowHandle);
            if (pointElement is not null)
            {
                return pointElement;
            }
        }

        AutomationElement searchRoot = RootElement();
        if (target.WindowHandle is not null)
        {
            var hwnd = new IntPtr(target.WindowHandle.Value);
            var handleRoot = ElementFromHandle(hwnd);
            if (target.AutomationId is null && target.Text is null && target.Role is null && target.X is null && target.Y is null && target.Index is null)
            {
                return handleRoot ?? FindWindowRootFromDesktop(hwnd, searchRoot);
            }

            if (handleRoot is not null)
            {
                var scoped = FindElementInRoot(handleRoot, target);
                if (scoped is not null)
                {
                    return scoped;
                }
            }

            var fallback = FindWindowRootFromDesktop(hwnd, handleRoot ?? searchRoot);
            return fallback is null ? null : FindElementInRoot(fallback, target);
        }

        return FindElementInRoot(searchRoot, target);
    }

    private static ActionResult SetRangeValue(AutomationElement element, RangeValuePattern rangePattern, string value)
    {
        var details = RangeDetails(element, rangePattern, value);
        if (!TryParseFiniteDouble(value, out var numeric))
        {
            details["errorCode"] = "invalid_argument";
            return new ActionResult("set-value", false, "UIAutomation.RangeValuePattern", ElementId(element), "RangeValue target requires a finite numeric value.", details);
        }

        details["requestedValue"] = numeric;
        RangeValuePattern.RangeValuePatternInformation current;
        try
        {
            current = rangePattern.Current;
        }
        catch (Exception ex)
        {
            details["errorCode"] = "uia_provider_error";
            details["exceptionType"] = ex.GetType().Name;
            return new ActionResult("set-value", false, "UIAutomation.RangeValuePattern", ElementId(element), ex.Message, details);
        }

        if (current.IsReadOnly)
        {
            details["errorCode"] = "read_only";
            return new ActionResult("set-value", false, "UIAutomation.RangeValuePattern", ElementId(element), "RangeValue target is read-only.", details);
        }

        if (numeric < current.Minimum || numeric > current.Maximum)
        {
            details["errorCode"] = "out_of_range";
            return new ActionResult("set-value", false, "UIAutomation.RangeValuePattern", ElementId(element), $"Value must be between {current.Minimum.ToString(CultureInfo.InvariantCulture)} and {current.Maximum.ToString(CultureInfo.InvariantCulture)}.", details);
        }

        try
        {
            rangePattern.SetValue(numeric);
            details["value"] = numeric;
            return new ActionResult("set-value", true, "UIAutomation.RangeValuePattern", ElementId(element), Details: details);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            details["errorCode"] = "out_of_range";
            return new ActionResult("set-value", false, "UIAutomation.RangeValuePattern", ElementId(element), ex.Message, details);
        }
        catch (Exception ex)
        {
            details["errorCode"] = "uia_provider_error";
            details["exceptionType"] = ex.GetType().Name;
            return new ActionResult("set-value", false, "UIAutomation.RangeValuePattern", ElementId(element), ex.Message, details);
        }
    }

    private static AutomationElement? FindElementInRoot(AutomationElement searchRoot, TargetSpec target)
    {
        var all = EnumerateElements(searchRoot, includeRoot: true);
        if (target.AutomationId is not null)
            return all.FirstOrDefault(e => SafeString(e, AutomationElement.AutomationIdProperty).Equals(target.AutomationId, StringComparison.OrdinalIgnoreCase));
        if (target.Text is not null)
            return all.FirstOrDefault(e => SafeName(e).Contains(target.Text, StringComparison.OrdinalIgnoreCase));
        if (target.Role is not null)
            return all.FirstOrDefault(e => Role(e).Equals(target.Role, StringComparison.OrdinalIgnoreCase));
        if (target.X is not null && target.Y is not null)
            return all.Where(e => Contains(Bounds(e), target.X.Value, target.Y.Value)).OrderBy(e => Bounds(e).Width * Bounds(e).Height).FirstOrDefault();
        if (target.Index is not null)
            return all.Skip(target.Index.Value).FirstOrDefault();
        return null;
    }

    private IReadOnlyList<ElementSnapshot> ReadTreeCore()
    {
        var diagnostics = new UiaTraversalDiagnostics();
        var state = new UiaTraversalState(MaxTotalElements);
        var roots = FindChildren(RootElement(), diagnostics);
        var snapshots = roots.Cast<AutomationElement>()
            .Take(MaxChildrenPerNode)
            .Select((element, index) => ToSnapshot(element, index, 0, state, diagnostics))
            .Where(e => e is not null)
            .Cast<ElementSnapshot>()
            .ToList();

        var limit = state.CreateLimitElement();
        if (limit is not null)
        {
            snapshots.Add(limit);
        }

        return snapshots.Count == 0
            ? new[] { DegradedElement("uia-empty", "UI Automation desktop root returned no elements.", "empty") }
            : snapshots;
    }

    private static ElementSnapshot? ToSnapshot(AutomationElement element, int index, int depth, UiaTraversalState state, UiaTraversalDiagnostics diagnostics)
    {
        if (!state.TryEnter())
        {
            return null;
        }

        try
        {
            var id = ElementId(element);
            var children = depth >= MaxDepth ? Array.Empty<ElementSnapshot>() : Children(element, depth + 1, state, diagnostics);
            return new ElementSnapshot(
                id,
                SafeName(element),
                Role(element),
                Bounds(element),
                SafeString(element, AutomationElement.AutomationIdProperty),
                SafeBool(element, AutomationElement.IsEnabledProperty),
                SafeBool(element, AutomationElement.HasKeyboardFocusProperty),
                Patterns(element),
                children);
        }
        catch (Exception ex)
        {
            return DegradedElement($"uia-degraded-{depth}-{index}", ex.Message, "element_serialization", ExceptionDetails(ex));
        }
    }

    private static ElementSnapshot[] Children(AutomationElement element, int depth, UiaTraversalState state, UiaTraversalDiagnostics diagnostics)
    {
        try
        {
            var children = FindChildren(element, diagnostics);
            var snapshots = children
                .Cast<AutomationElement>()
                .Take(MaxChildrenPerNode)
                .Select((child, index) => ToSnapshot(child, index, depth, state, diagnostics))
                .Where(e => e is not null)
                .Cast<ElementSnapshot>()
                .ToList();
            var limit = state.CreateLimitElement();
            if (limit is not null)
            {
                snapshots.Add(limit);
            }

            return snapshots.ToArray();
        }
        catch (Exception ex)
        {
            return new[] { DegradedElement("uia-children-error", ex.Message, "children_exception", ExceptionDetails(ex)) };
        }
    }

    private static IReadOnlyDictionary<string, object?> PatternDetails(AutomationElement element) => new Dictionary<string, object?> { ["patterns"] = Patterns(element) };
    private static bool Contains(Bounds bounds, int x, int y) => x >= bounds.X && x <= bounds.X + bounds.Width && y >= bounds.Y && y <= bounds.Y + bounds.Height;
    private static string ElementId(AutomationElement element) => "uia-" + SafeInt(element, AutomationElement.NativeWindowHandleProperty) + "-" + SafeString(element, AutomationElement.AutomationIdProperty) + "-" + SafeName(element).GetHashCode(StringComparison.Ordinal);
    private static Bounds Bounds(AutomationElement element)
    {
        var rect = SafeProperty(element, AutomationElement.BoundingRectangleProperty) is System.Windows.Rect r
            ? r
            : System.Windows.Rect.Empty;
        if (rect.IsEmpty) return new Bounds(0, 0, 0, 0);
        return new Bounds((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
    }

    private static string Role(AutomationElement element)
    {
        try
        {
            return SafeProperty(element, AutomationElement.ControlTypeProperty) is ControlType controlType
                ? controlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal)
                : "unknown";
        }
        catch { return "unknown"; }
    }

    private static IReadOnlyList<string> Patterns(AutomationElement element)
    {
        return PatternNames(pattern => HasPattern(element, pattern));
    }

    internal static IReadOnlyList<string> PatternNames(Func<AutomationPattern, bool> hasPattern)
    {
        var patterns = new List<string>();
        AddPattern(hasPattern, InvokePattern.Pattern, "Invoke", patterns);
        AddPattern(hasPattern, ValuePattern.Pattern, "Value", patterns);
        AddPattern(hasPattern, RangeValuePattern.Pattern, "RangeValue", patterns);
        AddPattern(hasPattern, TogglePattern.Pattern, "Toggle", patterns);
        AddPattern(hasPattern, SelectionItemPattern.Pattern, "SelectionItem", patterns);
        AddPattern(hasPattern, ExpandCollapsePattern.Pattern, "ExpandCollapse", patterns);
        AddPattern(hasPattern, ScrollItemPattern.Pattern, "ScrollIntoView", patterns);
        AddPattern(hasPattern, WindowPattern.Pattern, "Window", patterns);
        return patterns;
    }

    internal static ElementSnapshot DegradedElement(string id, string message, string reason, IReadOnlyDictionary<string, object?>? details = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["degraded"] = true,
            ["degradationReason"] = reason,
            ["message"] = message
        };
        if (details is not null)
        {
            metadata["details"] = details;
        }

        return new ElementSnapshot(
            id,
            message,
            "degraded",
            new Bounds(0, 0, 0, 0),
            Enabled: false,
            Patterns: new[] { "Degraded" },
            Metadata: metadata);
    }

    private static void AddPattern(Func<AutomationPattern, bool> hasPattern, AutomationPattern pattern, string name, List<string> patterns)
    {
        try
        {
            if (hasPattern(pattern)) patterns.Add(name);
        }
        catch { }
    }

    private static bool HasPattern(AutomationElement element, AutomationPattern pattern)
    {
        try
        {
            _ = element.GetCachedPattern(pattern);
            return true;
        }
        catch { }

        try
        {
            return element.TryGetCurrentPattern(pattern, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetCurrentPattern<T>(AutomationElement element, AutomationPattern pattern, out T typed)
        where T : class
    {
        typed = null!;
        try
        {
            if (element.TryGetCurrentPattern(pattern, out var value) && value is T matched)
            {
                typed = matched;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static string SafeName(AutomationElement element) => SafeString(element, AutomationElement.NameProperty);
    private static string SafeString(AutomationElement element, AutomationProperty property)
    {
        return SafeProperty(element, property) as string ?? "";
    }

    private static bool SafeBool(AutomationElement element, AutomationProperty property)
    {
        return SafeProperty(element, property) is bool b && b;
    }

    private static int SafeInt(AutomationElement element, AutomationProperty property)
    {
        return SafeProperty(element, property) is int i ? i : 0;
    }

    private static object? SafeProperty(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var cached = element.GetCachedPropertyValue(property, true);
            if (cached != AutomationElement.NotSupported)
            {
                return cached;
            }
        }
        catch { }

        try
        {
            var current = element.GetCurrentPropertyValue(property, true);
            return current == AutomationElement.NotSupported ? null : current;
        }
        catch
        {
            return null;
        }
    }

    private static bool ControlTypeEquals(AutomationElement element, ControlType controlType)
    {
        try
        {
            return SafeProperty(element, AutomationElement.ControlTypeProperty) is ControlType current && current == controlType;
        }
        catch
        {
            return false;
        }
    }

    private static AutomationElement RootElement() => AutomationElement.RootElement;

    private static AutomationElement? ElementFromHandle(IntPtr hwnd)
    {
        try
        {
            return hwnd == IntPtr.Zero ? null : AutomationElement.FromHandle(hwnd);
        }
        catch
        {
            return null;
        }
    }

    private static AutomationElement? ElementFromPoint(int x, int y, int? windowHandle)
    {
        try
        {
            var candidate = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (candidate is null || windowHandle is null)
            {
                return candidate;
            }

            var hwnd = new IntPtr(windowHandle.Value);
            return ElementBelongsToWindowAtPoint(candidate, hwnd, x, y) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ElementBelongsToWindowAtPoint(AutomationElement candidate, IntPtr requestedHwnd, int x, int y)
    {
        if (requestedHwnd == IntPtr.Zero || !Native.IsWindow(requestedHwnd))
        {
            return false;
        }

        var rootHwnd = Native.GetAncestor(requestedHwnd, Native.GaRoot);
        if (rootHwnd == IntPtr.Zero)
        {
            rootHwnd = requestedHwnd;
        }

        Native.GetWindowThreadProcessId(rootHwnd, out var processId);
        if (MatchesWindowRelationship(candidate, requestedHwnd, rootHwnd, processId))
        {
            return true;
        }

        var candidateProcessId = SafeInt(candidate, AutomationElement.ProcessIdProperty);
        return processId != 0 &&
            candidateProcessId == processId &&
            Native.GetWindowRect(rootHwnd, out var rect) &&
            x >= rect.Left &&
            x <= rect.Right &&
            y >= rect.Top &&
            y <= rect.Bottom;
    }

    private static AutomationElement? FindWindowRootFromDesktop(IntPtr requestedHwnd, AutomationElement currentRoot)
    {
        if (requestedHwnd == IntPtr.Zero || !Native.IsWindow(requestedHwnd))
        {
            return null;
        }

        var rootHwnd = Native.GetAncestor(requestedHwnd, Native.GaRoot);
        if (rootHwnd == IntPtr.Zero)
        {
            rootHwnd = requestedHwnd;
        }

        Native.GetWindowThreadProcessId(rootHwnd, out var processId);
        var currentId = ElementId(currentRoot);
        var diagnostics = new UiaTraversalDiagnostics();
        foreach (var candidate in FindChildren(RootElement(), diagnostics).Cast<AutomationElement>().Take(MaxChildrenPerNode))
        {
            if (ElementId(candidate).Equals(currentId, StringComparison.Ordinal))
            {
                continue;
            }

            if (MatchesWindowRelationship(candidate, requestedHwnd, rootHwnd, processId))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool MatchesWindowRelationship(AutomationElement candidate, IntPtr requestedHwnd, IntPtr rootHwnd, int processId)
    {
        var candidateHwnd = new IntPtr(SafeInt(candidate, AutomationElement.NativeWindowHandleProperty));
        var candidateProcessId = SafeInt(candidate, AutomationElement.ProcessIdProperty);
        if (candidateHwnd == requestedHwnd || candidateHwnd == rootHwnd)
        {
            return true;
        }

        if (candidateHwnd == IntPtr.Zero || processId == 0 || candidateProcessId != processId)
        {
            return false;
        }

        var candidateRoot = Native.GetAncestor(candidateHwnd, Native.GaRoot);
        if (candidateRoot == IntPtr.Zero)
        {
            candidateRoot = candidateHwnd;
        }

        return candidateRoot == rootHwnd ||
            Native.IsChild(rootHwnd, candidateHwnd) ||
            Native.IsChild(candidateHwnd, rootHwnd);
    }

    private static IEnumerable<AutomationElement> EnumerateElements(AutomationElement root, bool includeRoot)
    {
        var state = new UiaTraversalState(MaxTotalElements);
        if (includeRoot && state.TryEnter())
        {
            yield return root;
        }

        var diagnostics = new UiaTraversalDiagnostics();
        foreach (var element in EnumerateChildren(root, 0, state, diagnostics))
        {
            yield return element;
        }
    }

    private static IEnumerable<AutomationElement> EnumerateChildren(AutomationElement root, int depth, UiaTraversalState state, UiaTraversalDiagnostics diagnostics)
    {
        if (depth >= MaxDepth || state.LimitReached)
        {
            yield break;
        }

        AutomationElementCollection children;
        try
        {
            children = FindChildren(root, diagnostics);
        }
        catch
        {
            yield break;
        }

        foreach (var child in children.Cast<AutomationElement>().Take(MaxChildrenPerNode))
        {
            if (!state.TryEnter())
            {
                yield break;
            }

            yield return child;
            foreach (var descendant in EnumerateChildren(child, depth + 1, state, diagnostics))
            {
                yield return descendant;
            }
        }
    }

    private static AutomationElementCollection FindChildren(AutomationElement element, UiaTraversalDiagnostics diagnostics)
    {
        try
        {
            var cache = CreateCacheRequest();
            using (cache.Activate())
            {
                return element.FindAll(TreeScope.Children, Condition.TrueCondition);
            }
        }
        catch (Exception ex)
        {
            diagnostics.CacheFallbacks++;
            diagnostics.LastCacheError = ex.Message;
            return element.FindAll(TreeScope.Children, Condition.TrueCondition);
        }
    }

    private static CacheRequest CreateCacheRequest()
    {
        var cache = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.Full,
            TreeFilter = Automation.RawViewCondition,
            TreeScope = TreeScope.Element | TreeScope.Children
        };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.HasKeyboardFocusProperty);
        cache.Add(AutomationElement.NativeWindowHandleProperty);
        cache.Add(AutomationElement.ProcessIdProperty);
        cache.Add(InvokePattern.Pattern);
        cache.Add(ValuePattern.Pattern);
        cache.Add(RangeValuePattern.Pattern);
        cache.Add(TogglePattern.Pattern);
        cache.Add(SelectionItemPattern.Pattern);
        cache.Add(ExpandCollapsePattern.Pattern);
        cache.Add(ScrollItemPattern.Pattern);
        cache.Add(WindowPattern.Pattern);
        return cache;
    }

    private static Dictionary<string, object?> RangeDetails(AutomationElement element, RangeValuePattern rangePattern, string rawValue)
    {
        var details = PatternDetails(element).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        details["rawValue"] = rawValue;
        details["semanticPattern"] = "RangeValuePattern";
        details["finalMethod"] = "UIAutomation.RangeValuePattern";

        try
        {
            var current = rangePattern.Current;
            details["rangeValue"] = current.Value;
            details["rangeMinimum"] = current.Minimum;
            details["rangeMaximum"] = current.Maximum;
            details["rangeSmallChange"] = current.SmallChange;
            details["rangeLargeChange"] = current.LargeChange;
            details["rangeIsReadOnly"] = current.IsReadOnly;
        }
        catch (Exception ex)
        {
            details["rangeError"] = ex.Message;
            details["exceptionType"] = ex.GetType().Name;
        }

        return details;
    }

    private static bool TryParseFiniteDouble(string value, out double result)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            !double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
        {
            return false;
        }

        return !double.IsNaN(result) && !double.IsInfinity(result);
    }

    private static IReadOnlyDictionary<string, object?> ExceptionDetails(Exception ex) => new Dictionary<string, object?>
    {
        ["exceptionType"] = ex.GetType().Name,
        ["message"] = ex.Message
    };

    private static IReadOnlyDictionary<string, object?> MergeDetails(
        IReadOnlyDictionary<string, object?> first,
        IReadOnlyDictionary<string, object?> second)
    {
        var result = new Dictionary<string, object?>(first);
        foreach (var (key, value) in second)
        {
            result[key] = value;
        }

        return result;
    }

    private static ActionResult NotFound(string action, TargetSpec target) => new(action, false, "UIAutomation", target.ToString(), "Target was not found.");

    private sealed class UiaTraversalState(int remaining)
    {
        private int remaining = remaining;
        private bool limitReported;

        public bool LimitReached { get; private set; }

        public bool TryEnter()
        {
            if (remaining <= 0)
            {
                LimitReached = true;
                return false;
            }

            remaining--;
            return true;
        }

        public ElementSnapshot? CreateLimitElement()
        {
            if (!LimitReached || limitReported)
            {
                return null;
            }

            limitReported = true;
            return DegradedElement(
                "uia-limit",
                $"UI Automation traversal stopped after {MaxTotalElements} elements.",
                "element_limit",
                new Dictionary<string, object?> { ["maxTotalElements"] = MaxTotalElements });
        }
    }

    private sealed class UiaTraversalDiagnostics
    {
        public int CacheFallbacks { get; set; }
        public string? LastCacheError { get; set; }
    }
}

internal sealed class WindowsMsaaAutomationAdapter : IWindowsMsaaAutomationAdapter
{
    private const int ChildIdSelf = 0;
    private const int MaxDepth = 5;
    private const int MaxChildrenPerNode = 80;
    private const int MaxTotalElements = 1500;
    private const int MaxRootWindows = 24;
    private const uint ObjidClient = 0xFFFFFFFC;

    public bool HasKnownLegacyWindows() => EnumerateCandidateWindows(legacyOnly: true).Any();

    public IReadOnlyList<ElementSnapshot> ReadTree(int? windowHandle = null) =>
        ReadTrees(ResolveCandidateWindows(windowHandle, legacyOnly: false), "msaa_fallback");

    public IReadOnlyList<ElementSnapshot> ReadLegacyWindowTrees() =>
        ReadTrees(EnumerateCandidateWindows(legacyOnly: true), "known_legacy_window");

    public ActionResult Click(TargetSpec target) => InvokeDefaultAction("click", target, "invoke");

    public ActionResult PerformAction(TargetSpec target, string action)
    {
        var normalized = action.Trim().ToLowerInvariant();
        if (normalized is "click" or "invoke")
        {
            return InvokeDefaultAction("perform-action", target, normalized);
        }

        return new ActionResult(
            "perform-action",
            false,
            "MSAA",
            target.ToString(),
            $"MSAA fallback does not support action '{action}'.",
            new Dictionary<string, object?>
            {
                ["elementSource"] = "msaa",
                ["fallbackReason"] = "msaa_action_unavailable",
                ["requestedAction"] = action
            });
    }

    private static IReadOnlyList<ElementSnapshot> ReadTrees(IEnumerable<IntPtr> handles, string reason)
    {
        var snapshots = new List<ElementSnapshot>();
        var remaining = MaxTotalElements;
        foreach (var hwnd in handles.Take(MaxRootWindows))
        {
            var accessible = AccessibleFromWindow(hwnd);
            if (accessible is null)
            {
                continue;
            }

            try
            {
                var state = new MsaaTraversalState(remaining);
                var snapshot = BuildSnapshot(accessible, ChildIdSelf, hwnd, "0", 0, state, reason);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }

                remaining = state.Remaining;
                if (remaining <= 0)
                {
                    snapshots.Add(WindowsElementAutomationService.DegradedElement(
                        "msaa-limit",
                        $"MSAA traversal stopped after {MaxTotalElements} elements.",
                        "msaa_element_limit",
                        new Dictionary<string, object?> { ["source"] = "msaa", ["maxTotalElements"] = MaxTotalElements }));
                    break;
                }
            }
            finally
            {
                ReleaseComObject(accessible);
            }
        }

        return snapshots;
    }

    private static ActionResult InvokeDefaultAction(string action, TargetSpec target, string requestedAction)
    {
        foreach (var hwnd in ResolveActionWindows(target).Take(MaxRootWindows))
        {
            var accessible = AccessibleFromWindow(hwnd);
            if (accessible is null)
            {
                continue;
            }

            try
            {
                var state = new MsaaTraversalState(MaxTotalElements);
                var result = TryInvokeDefaultAction(accessible, ChildIdSelf, hwnd, "0", 0, state, target, action, requestedAction);
                if (result is not null)
                {
                    return result;
                }
            }
            finally
            {
                ReleaseComObject(accessible);
            }
        }

        return new ActionResult(
            action,
            false,
            "MSAA",
            target.ToString(),
            "Target was not found in the bounded MSAA fallback tree.",
            new Dictionary<string, object?>
            {
                ["elementSource"] = "msaa",
                ["fallbackReason"] = "msaa_target_not_found",
                ["maxDepth"] = MaxDepth,
                ["maxTotalElements"] = MaxTotalElements
            });
    }

    private static ActionResult? TryInvokeDefaultAction(
        MsaaAccessible accessible,
        int childId,
        IntPtr hwnd,
        string path,
        int depth,
        MsaaTraversalState state,
        TargetSpec target,
        string action,
        string requestedAction)
    {
        if (!state.TryEnter(out var traversalIndex))
        {
            return null;
        }

        var info = ReadElementInfo(accessible, childId, hwnd, path, "msaa_action", traversalIndex);
        if (info is not null && Matches(info, target))
        {
            if (!MsaaElementMapper.SupportsDefaultAction(info.Role, info.DefaultAction))
            {
                return new ActionResult(
                    action,
                    false,
                    "MSAA",
                    info.Id,
                    "MSAA target does not expose a safe default action.",
                    ActionDetails(info, "msaa_default_action_unavailable", requestedAction));
            }

            try
            {
                accessible.accDoDefaultAction(childId);
                return new ActionResult(
                    action,
                    true,
                    "MSAA.accDoDefaultAction",
                    info.Id,
                    Details: ActionDetails(info, null, requestedAction, "MSAA.accDoDefaultAction"));
            }
            catch (Exception ex)
            {
                var details = ActionDetails(info, "msaa_provider_error", requestedAction);
                details["exceptionType"] = ex.GetType().Name;
                return new ActionResult(action, false, "MSAA.accDoDefaultAction", info.Id, ex.Message, details);
            }
        }

        if (depth >= MaxDepth || childId != ChildIdSelf)
        {
            return null;
        }

        foreach (var child in EnumerateChildren(accessible).Take(MaxChildrenPerNode))
        {
            if (child.Accessible is not null)
            {
                try
                {
                    var result = TryInvokeDefaultAction(child.Accessible, ChildIdSelf, hwnd, $"{path}.{child.ChildId}", depth + 1, state, target, action, requestedAction);
                    if (result is not null)
                    {
                        return result;
                    }
                }
                finally
                {
                    if (!ReferenceEquals(child.Accessible, accessible))
                    {
                        ReleaseComObject(child.Accessible);
                    }
                }
            }
            else
            {
                var result = TryInvokeDefaultAction(accessible, child.ChildId, hwnd, $"{path}.{child.ChildId}", depth + 1, state, target, action, requestedAction);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static ElementSnapshot? BuildSnapshot(
        MsaaAccessible accessible,
        int childId,
        IntPtr hwnd,
        string path,
        int depth,
        MsaaTraversalState state,
        string reason)
    {
        if (!state.TryEnter(out var traversalIndex))
        {
            return null;
        }

        var info = ReadElementInfo(accessible, childId, hwnd, path, reason, traversalIndex);
        if (info is null)
        {
            return null;
        }

        var children = new List<ElementSnapshot>();
        if (depth < MaxDepth && childId == ChildIdSelf)
        {
            foreach (var child in EnumerateChildren(accessible).Take(MaxChildrenPerNode))
            {
                ElementSnapshot? childSnapshot;
                if (child.Accessible is not null)
                {
                    try
                    {
                        childSnapshot = BuildSnapshot(child.Accessible, ChildIdSelf, hwnd, $"{path}.{child.ChildId}", depth + 1, state, reason);
                    }
                    finally
                    {
                        if (!ReferenceEquals(child.Accessible, accessible))
                        {
                            ReleaseComObject(child.Accessible);
                        }
                    }
                }
                else
                {
                    childSnapshot = BuildSnapshot(accessible, child.ChildId, hwnd, $"{path}.{child.ChildId}", depth + 1, state, reason);
                }

                if (childSnapshot is not null)
                {
                    children.Add(childSnapshot);
                }

                if (state.Remaining <= 0)
                {
                    break;
                }
            }
        }

        return MsaaElementMapper.ToSnapshot(info with { Children = children });
    }

    private static MsaaElementInfo? ReadElementInfo(
        MsaaAccessible accessible,
        int childId,
        IntPtr hwnd,
        string path,
        string reason,
        int traversalIndex)
    {
        try
        {
            var name = SafeString(() => accessible.get_accName(childId));
            var role = SafeInt(() => accessible.get_accRole(childId));
            var state = SafeInt(() => accessible.get_accState(childId));
            var defaultAction = SafeString(() => accessible.get_accDefaultAction(childId));
            var bounds = SafeLocation(accessible, childId);
            var value = SafeString(() => accessible.get_accValue(childId));
            var id = "msaa-" + hwnd.ToInt64().ToString("x") + "-" + path;
            return new MsaaElementInfo(
                id,
                string.IsNullOrWhiteSpace(name) ? null : name,
                role,
                state,
                bounds,
                string.IsNullOrWhiteSpace(defaultAction) ? null : defaultAction,
                string.IsNullOrWhiteSpace(value) ? null : value,
                hwnd,
                childId,
                path,
                traversalIndex,
                reason,
                Array.Empty<ElementSnapshot>());
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<MsaaChild> EnumerateChildren(MsaaAccessible accessible)
    {
        int count;
        try
        {
            count = accessible.accChildCount;
        }
        catch
        {
            yield break;
        }

        for (var childId = 1; childId <= Math.Min(count, MaxChildrenPerNode); childId++)
        {
            MsaaAccessible? childAccessible = null;
            try
            {
                if (accessible.get_accChild(childId) is MsaaAccessible matched)
                {
                    childAccessible = matched;
                }
            }
            catch
            {
            }

            yield return new MsaaChild(childId, childAccessible);
        }
    }

    private static bool Matches(MsaaElementInfo info, TargetSpec target)
    {
        if (target.Id is not null)
        {
            return info.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase);
        }

        if (target.AutomationId is not null)
        {
            return false;
        }

        if (target.Text is not null)
        {
            return info.Name is not null && info.Name.Contains(target.Text, StringComparison.OrdinalIgnoreCase);
        }

        if (target.Role is not null)
        {
            return MsaaElementMapper.RoleName(info.Role).Equals(target.Role, StringComparison.OrdinalIgnoreCase);
        }

        if (target.X is not null && target.Y is not null)
        {
            return info.Bounds.Width > 0 &&
                info.Bounds.Height > 0 &&
                target.X.Value >= info.Bounds.X &&
                target.X.Value <= info.Bounds.X + info.Bounds.Width &&
                target.Y.Value >= info.Bounds.Y &&
                target.Y.Value <= info.Bounds.Y + info.Bounds.Height;
        }

        if (target.Index is not null)
        {
            return info.TraversalIndex == target.Index.Value;
        }

        return false;
    }

    private static Dictionary<string, object?> ActionDetails(
        MsaaElementInfo info,
        string? fallbackReason,
        string requestedAction,
        string finalMethod = "MSAA")
    {
        var details = MsaaElementMapper.Metadata(info).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        details["semanticPattern"] = "MSAA.DefaultAction";
        details["requestedAction"] = requestedAction;
        details["finalMethod"] = finalMethod;
        if (fallbackReason is not null)
        {
            details["fallbackReason"] = fallbackReason;
        }

        return details;
    }

    private static IEnumerable<IntPtr> ResolveCandidateWindows(int? windowHandle, bool legacyOnly)
    {
        if (windowHandle is not null)
        {
            var hwnd = new IntPtr(windowHandle.Value);
            if (hwnd != IntPtr.Zero && Native.IsWindow(hwnd))
            {
                yield return RootWindow(hwnd);
            }

            yield break;
        }

        foreach (var hwnd in EnumerateCandidateWindows(legacyOnly))
        {
            yield return hwnd;
        }
    }

    private static IEnumerable<IntPtr> ResolveActionWindows(TargetSpec target)
    {
        if (target.WindowHandle is not null)
        {
            foreach (var hwnd in ResolveCandidateWindows(target.WindowHandle, legacyOnly: false))
            {
                yield return hwnd;
            }

            yield break;
        }

        if (target.X is not null && target.Y is not null)
        {
            var hit = Native.WindowFromPoint(new POINT { X = target.X.Value, Y = target.Y.Value });
            if (hit != IntPtr.Zero)
            {
                yield return RootWindow(hit);
            }

            yield break;
        }

        foreach (var hwnd in EnumerateCandidateWindows(legacyOnly: true))
        {
            yield return hwnd;
        }
    }

    private static IEnumerable<IntPtr> EnumerateCandidateWindows(bool legacyOnly)
    {
        var handles = new List<IntPtr>();
        try
        {
            Native.EnumWindows((hwnd, _) =>
            {
                if (handles.Count >= MaxRootWindows)
                {
                    return false;
                }

                if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
                {
                    return true;
                }

                var root = RootWindow(hwnd);
                if (root == IntPtr.Zero || handles.Contains(root))
                {
                    return true;
                }

                if (legacyOnly && !IsKnownLegacyClass(ClassName(root)))
                {
                    return true;
                }

                Native.GetWindowRect(root, out var rect);
                if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                {
                    return true;
                }

                handles.Add(root);
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
        }

        return handles;
    }

    private static bool IsKnownLegacyClass(string className) =>
        className.StartsWith("SAL", StringComparison.OrdinalIgnoreCase) ||
        className.StartsWith("VCL", StringComparison.OrdinalIgnoreCase) ||
        className.StartsWith("Thunder", StringComparison.OrdinalIgnoreCase) ||
        className.StartsWith("Afx:", StringComparison.OrdinalIgnoreCase) ||
        className.StartsWith("ATL:", StringComparison.OrdinalIgnoreCase);

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var length = Native.GetClassName(hwnd, sb, sb.Capacity);
        return length <= 0 ? "" : sb.ToString();
    }

    private static IntPtr RootWindow(IntPtr hwnd)
    {
        var root = Native.GetAncestor(hwnd, Native.GaRoot);
        return root == IntPtr.Zero ? hwnd : root;
    }

    private static MsaaAccessible? AccessibleFromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        var hr = Native.AccessibleObjectFromWindow(hwnd, ObjidClient, ref iid, out var accessible);
        return hr < 0 || accessible is not MsaaAccessible typed ? null : typed;
    }

    private static Bounds SafeLocation(MsaaAccessible accessible, int childId)
    {
        try
        {
            accessible.accLocation(out var left, out var top, out var width, out var height, childId);
            return width > 0 && height > 0 ? new Bounds(left, top, width, height) : new Bounds(0, 0, 0, 0);
        }
        catch
        {
            return new Bounds(0, 0, 0, 0);
        }
    }

    private static string? SafeString(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static int SafeInt(Func<object?> read)
    {
        try
        {
            var value = read();
            return value switch
            {
                int i => i,
                short s => s,
                uint u when u <= int.MaxValue => (int)u,
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }

    private sealed record MsaaChild(int ChildId, MsaaAccessible? Accessible);

    private sealed class MsaaTraversalState(int remaining)
    {
        public int Remaining { get; private set; } = remaining;
        private int visited;

        public bool TryEnter(out int traversalIndex)
        {
            traversalIndex = -1;
            if (Remaining <= 0)
            {
                return false;
            }

            traversalIndex = visited;
            visited++;
            Remaining--;
            return true;
        }
    }
}

internal sealed record MsaaElementInfo(
    string Id,
    string? Name,
    int Role,
    int State,
    Bounds Bounds,
    string? DefaultAction,
    string? Value,
    IntPtr WindowHandle,
    int ChildId,
    string Path,
    int TraversalIndex,
    string FallbackReason,
    IReadOnlyList<ElementSnapshot> Children);

internal static class MsaaElementMapper
{
    internal const int RoleSystemTitleBar = 0x01;
    internal const int RoleSystemMenuBar = 0x02;
    internal const int RoleSystemScrollBar = 0x03;
    internal const int RoleSystemWindow = 0x09;
    internal const int RoleSystemClient = 0x0A;
    internal const int RoleSystemMenuPopup = 0x0B;
    internal const int RoleSystemMenuItem = 0x0C;
    internal const int RoleSystemToolTip = 0x0D;
    internal const int RoleSystemDialog = 0x12;
    internal const int RoleSystemGrouping = 0x14;
    internal const int RoleSystemToolBar = 0x16;
    internal const int RoleSystemStatusBar = 0x17;
    internal const int RoleSystemLink = 0x1E;
    internal const int RoleSystemList = 0x21;
    internal const int RoleSystemListItem = 0x22;
    internal const int RoleSystemPageTab = 0x25;
    internal const int RoleSystemGraphic = 0x28;
    internal const int RoleSystemStaticText = 0x29;
    internal const int RoleSystemText = 0x2A;
    internal const int RoleSystemPushButton = 0x2B;
    internal const int RoleSystemCheckButton = 0x2C;
    internal const int RoleSystemRadioButton = 0x2D;
    internal const int RoleSystemComboBox = 0x2E;
    internal const int RoleSystemProgressBar = 0x30;
    internal const int RoleSystemSlider = 0x33;
    internal const int RoleSystemButtonDropDown = 0x38;
    internal const int RoleSystemButtonMenu = 0x39;
    internal const int RoleSystemButtonDropDownGrid = 0x3A;
    internal const int RoleSystemPageTabList = 0x3C;
    internal const int RoleSystemSplitButton = 0x3E;

    private const int StateSystemUnavailable = 0x00000001;
    private const int StateSystemFocused = 0x00000004;
    private const int StateSystemChecked = 0x00000010;
    private const int StateSystemReadOnly = 0x00000040;
    private const int StateSystemExpanded = 0x00000200;
    private const int StateSystemCollapsed = 0x00000400;
    private const int StateSystemInvisible = 0x00008000;

    public static ElementSnapshot ToSnapshot(MsaaElementInfo info) => new(
        info.Id,
        info.Name,
        RoleName(info.Role),
        info.Bounds,
        AutomationId: null,
        Enabled: (info.State & StateSystemUnavailable) == 0,
        Focused: (info.State & StateSystemFocused) != 0,
        Patterns: Patterns(info.Role, info.DefaultAction),
        Children: info.Children,
        Metadata: Metadata(info));

    public static IReadOnlyDictionary<string, object?> Metadata(MsaaElementInfo info)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["source"] = "msaa",
            ["elementSource"] = "msaa",
            ["msaaRole"] = info.Role,
            ["msaaRoleName"] = RoleName(info.Role),
            ["msaaState"] = info.State,
            ["msaaStateNames"] = StateNames(info.State),
            ["msaaDefaultAction"] = info.DefaultAction,
            ["msaaValue"] = info.Value,
            ["msaaChildId"] = info.ChildId,
            ["msaaPath"] = info.Path,
            ["msaaTraversalIndex"] = info.TraversalIndex,
            ["windowHandle"] = FormatHwnd(info.WindowHandle),
            ["fallbackReason"] = info.FallbackReason
        };
        return metadata;
    }

    public static string RoleName(int role) => role switch
    {
        RoleSystemTitleBar => "TitleBar",
        RoleSystemMenuBar => "MenuBar",
        RoleSystemScrollBar => "ScrollBar",
        RoleSystemWindow => "Window",
        RoleSystemClient => "Pane",
        RoleSystemMenuPopup => "Menu",
        RoleSystemMenuItem => "MenuItem",
        RoleSystemToolTip => "ToolTip",
        RoleSystemDialog => "Window",
        RoleSystemGrouping => "Group",
        RoleSystemToolBar => "ToolBar",
        RoleSystemStatusBar => "StatusBar",
        RoleSystemLink => "Hyperlink",
        RoleSystemList => "List",
        RoleSystemListItem => "ListItem",
        RoleSystemPageTab => "TabItem",
        RoleSystemGraphic => "Image",
        RoleSystemStaticText => "Text",
        RoleSystemText => "Edit",
        RoleSystemPushButton => "Button",
        RoleSystemCheckButton => "CheckBox",
        RoleSystemRadioButton => "RadioButton",
        RoleSystemComboBox => "ComboBox",
        RoleSystemProgressBar => "ProgressBar",
        RoleSystemSlider => "Slider",
        RoleSystemButtonDropDown or RoleSystemButtonMenu or RoleSystemButtonDropDownGrid or RoleSystemSplitButton => "SplitButton",
        RoleSystemPageTabList => "Tab",
        0 => "Unknown",
        _ => "Role_0x" + role.ToString("X", CultureInfo.InvariantCulture)
    };

    public static IReadOnlyList<string> StateNames(int state)
    {
        var names = new List<string>();
        AddState(state, StateSystemUnavailable, "Unavailable", names);
        AddState(state, StateSystemFocused, "Focused", names);
        AddState(state, StateSystemChecked, "Checked", names);
        AddState(state, StateSystemReadOnly, "ReadOnly", names);
        AddState(state, StateSystemExpanded, "Expanded", names);
        AddState(state, StateSystemCollapsed, "Collapsed", names);
        AddState(state, StateSystemInvisible, "Invisible", names);
        return names;
    }

    public static IReadOnlyList<string> Patterns(int role, string? defaultAction)
    {
        var patterns = new List<string>();
        if (SupportsDefaultAction(role, defaultAction))
        {
            patterns.Add("Invoke");
        }

        if (IsDropdown(role))
        {
            patterns.Add("ExpandCollapse");
        }

        return patterns;
    }

    public static bool SupportsDefaultAction(int role, string? defaultAction) =>
        !string.IsNullOrWhiteSpace(defaultAction) ||
        role is RoleSystemPushButton or
            RoleSystemCheckButton or
            RoleSystemRadioButton or
            RoleSystemLink or
            RoleSystemMenuItem or
            RoleSystemListItem or
            RoleSystemPageTab or
            RoleSystemComboBox or
            RoleSystemButtonDropDown or
            RoleSystemButtonMenu or
            RoleSystemButtonDropDownGrid or
            RoleSystemSplitButton;

    private static bool IsDropdown(int role) =>
        role is RoleSystemButtonDropDown or RoleSystemButtonMenu or RoleSystemButtonDropDownGrid or RoleSystemSplitButton;

    private static void AddState(int state, int flag, string name, ICollection<string> names)
    {
        if ((state & flag) != 0)
        {
            names.Add(name);
        }
    }

    private static string FormatHwnd(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("x");
}

public sealed class WindowsOcrService : IWindowsOcrService
{
    public IReadOnlyList<OcrTextSnapshot> Recognize(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return Array.Empty<OcrTextSnapshot>();
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return Array.Empty<OcrTextSnapshot>();

            var file = StorageFile.GetFileFromPathAsync(imagePath).AsTask().GetAwaiter().GetResult();
            using var stream = file.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            using var bitmap = decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            return result.Lines.SelectMany(line => line.Words)
                .Select(word => new OcrTextSnapshot(
                    word.Text,
                    new Bounds(
                        (int)Math.Round(word.BoundingRect.X),
                        (int)Math.Round(word.BoundingRect.Y),
                        (int)Math.Round(word.BoundingRect.Width),
                        (int)Math.Round(word.BoundingRect.Height)),
                    0))
                .ToArray();
        }
        catch
        {
            return Array.Empty<OcrTextSnapshot>();
        }
    }
}

public sealed class WindowsCaptureService : IWindowsCaptureService
{
    public CaptureResult CaptureDesktop(string imagePath, IReadOnlyList<WindowSnapshot> windows, IReadOnlyList<ElementSnapshot> elements)
    {
        var bounds = new Bounds(0, 0, Native.GetSystemMetrics(0), Native.GetSystemMetrics(1));
        var attempts = new List<CaptureAttempt>();
        var commonDetails = new Dictionary<string, object?>
        {
            ["captureBounds"] = bounds,
            ["boundsSource"] = "primaryMonitor"
        };
        var annotationBounds = windows.Select(w => w.Bounds).Concat(Flatten(elements).Select(e => e.Bounds)).ToArray();

        var graphicsResult = AssessBmp(TryCapturePrimaryMonitorWithGraphicsCapture(imagePath));
        attempts.Add(ToAttempt(graphicsResult));
        if (IsUsable(graphicsResult))
        {
            AnnotateBmp(imagePath, annotationBounds, bounds);
            return FinalizeResult(graphicsResult, attempts, commonDetails);
        }

        var result = AssessBmp(CaptureRegion(IntPtr.Zero, bounds, imagePath, "BitBlt.desktop"));
        attempts.Add(ToAttempt(result));
        if (result.Success)
        {
            AnnotateBmp(imagePath, annotationBounds, bounds);
        }

        return FinalizeResult(result, attempts, commonDetails);
    }

    public CaptureResult CaptureWindow(int handle, string imagePath, IReadOnlyList<ElementSnapshot> elements)
    {
        var hwnd = new IntPtr(handle);
        var attempts = new List<CaptureAttempt>();
        if (!TryGetWindowCaptureBounds(hwnd, out var windowBounds, out var boundsMessage))
        {
            var failure = new CaptureResult(
                false,
                "window.bounds",
                null,
                boundsMessage,
                CaptureQualityStatus.Unavailable);
            return FinalizeResult(failure, attempts, new Dictionary<string, object?>());
        }

        var commonDetails = WindowBoundsDetails(windowBounds);
        if (boundsMessage is not null)
        {
            commonDetails["boundsMessage"] = boundsMessage;
        }

        if (Native.IsIconic(hwnd))
        {
            var minimized = new CaptureResult(
                false,
                "none",
                null,
                "Window is minimized; no rendered pixels are available for image capture.",
                CaptureQualityStatus.Unavailable,
                MergeDetails(commonDetails, new Dictionary<string, object?>
                {
                    ["minimized"] = true,
                    ["noPixels"] = true
                }));
            attempts.Add(ToAttempt(minimized));
            return FinalizeResult(minimized, attempts, commonDetails);
        }

        var annotationBounds = Flatten(elements).Select(e => e.Bounds).ToArray();
        var graphicsResult = AssessBmp(TryCaptureWindowWithGraphicsCapture(hwnd, imagePath));
        attempts.Add(ToAttempt(graphicsResult));
        if (IsUsable(graphicsResult))
        {
            AnnotateBmp(imagePath, annotationBounds, windowBounds.CaptureBounds);
            return FinalizeResult(graphicsResult, attempts, commonDetails);
        }

        var printWindow = AssessBmp(CaptureRegion(hwnd, windowBounds.Win32Bounds, imagePath, "PrintWindow", windowBounds.CaptureBounds));
        attempts.Add(ToAttempt(printWindow));
        if (IsUsable(printWindow))
        {
            AnnotateBmp(imagePath, annotationBounds, windowBounds.CaptureBounds);
            return FinalizeResult(printWindow, attempts, commonDetails);
        }

        var occlusion = TryGetOcclusion(hwnd, windowBounds.CaptureBounds);
        var desktopCropDetails = BuildDesktopCropOcclusionDetails(occlusion);

        var desktopCropRaw = CaptureRegion(IntPtr.Zero, windowBounds.CaptureBounds, imagePath, "BitBlt.desktop-crop");
        var desktopCrop = desktopCropRaw with
        {
            Status = desktopCropRaw.Success && occlusion.Occluded == true ? CaptureQualityStatus.Degraded : desktopCropRaw.Status,
            Details = desktopCropDetails,
            Message = desktopCropRaw.Success && occlusion.Occluded == true
                ? "Desktop crop may show another window because the target is occluded."
                : desktopCropRaw.Message
        };
        desktopCrop = AssessBmp(desktopCrop);
        attempts.Add(ToAttempt(desktopCrop));
        if (desktopCrop.Success)
        {
            AnnotateBmp(imagePath, annotationBounds, windowBounds.CaptureBounds);
            return FinalizeResult(desktopCrop, attempts, commonDetails);
        }

        return FinalizeResult(desktopCrop, attempts, commonDetails);
    }

    private static bool IsUsable(CaptureResult result) => result.Success && result.Status == CaptureQualityStatus.Ok;

    private static CaptureResult AssessBmp(CaptureResult result)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.ImagePath))
        {
            return result;
        }

        var quality = BmpCaptureDiagnostics.Analyze(result.ImagePath);
        var details = MergeDetails(result.Details, new Dictionary<string, object?> { ["quality"] = quality });
        if (quality.Status == CaptureQualityStatus.Ok)
        {
            return result with { Status = result.Status, Details = details };
        }

        return result with
        {
            Status = CaptureQualityStatus.Degraded,
            Message = CombineMessages(result.Message, quality.Message),
            Details = details
        };
    }

    private static CaptureAttempt ToAttempt(CaptureResult result) => new(result.Method, result.Status, result.Message, result.Details);

    private static CaptureResult FinalizeResult(
        CaptureResult result,
        IReadOnlyList<CaptureAttempt> attempts,
        IReadOnlyDictionary<string, object?> commonDetails)
    {
        var details = MergeDetails(commonDetails, result.Details);
        details["attempts"] = attempts;
        details["qualityStatus"] = result.Status;

        var fallbackMessages = attempts
            .Where(a => !string.Equals(a.Method, result.Method, StringComparison.Ordinal) && a.Status != CaptureQualityStatus.Ok && !string.IsNullOrWhiteSpace(a.Message))
            .Select(a => $"{a.Method}: {a.Message}");
        return result with
        {
            Details = details,
            Message = CombineMessages(result.Message, string.Join("; ", fallbackMessages))
        };
    }

    private static Dictionary<string, object?> WindowBoundsDetails(WindowCaptureBounds bounds)
    {
        var details = new Dictionary<string, object?>
        {
            ["win32Bounds"] = bounds.Win32Bounds,
            ["captureBounds"] = bounds.CaptureBounds,
            ["boundsSource"] = bounds.Source
        };
        if (bounds.DwmExtendedFrameBounds is not null)
        {
            details["dwmExtendedFrameBounds"] = bounds.DwmExtendedFrameBounds;
        }

        return details;
    }

    private static Dictionary<string, object?> MergeDetails(
        IReadOnlyDictionary<string, object?>? first,
        IReadOnlyDictionary<string, object?>? second)
    {
        var result = first is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(first);
        if (second is null)
        {
            return result;
        }

        foreach (var (key, value) in second)
        {
            result[key] = value;
        }

        return result;
    }

    private static string? CombineMessages(params string?[] messages)
    {
        var nonEmpty = messages.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).ToArray();
        return nonEmpty.Length == 0 ? null : string.Join("; ", nonEmpty);
    }

    private static bool TryGetWindowCaptureBounds(IntPtr hwnd, out WindowCaptureBounds bounds, out string? message)
    {
        bounds = default!;
        if (!Native.GetWindowRect(hwnd, out var rect))
        {
            message = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        var win32 = RectToBounds(rect);
        if (TryGetDwmExtendedFrameBounds(hwnd, out var dwm, out message))
        {
            bounds = new WindowCaptureBounds(win32, dwm, dwm, "dwmExtendedFrame");
            return true;
        }

        bounds = new WindowCaptureBounds(win32, win32, null, "win32WindowRect");
        return true;
    }

    private static bool TryGetDwmExtendedFrameBounds(IntPtr hwnd, out Bounds bounds, out string? message)
    {
        bounds = default!;
        message = null;
        try
        {
            var hr = Native.DwmGetWindowAttribute(hwnd, Native.DwmwaExtendedFrameBounds, out var rect, Marshal.SizeOf<RECT>());
            if (hr < 0)
            {
                message = "DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS) failed with HRESULT 0x" + hr.ToString("X8");
                return false;
            }

            var candidate = RectToBounds(rect);
            if (candidate.Width <= 0 || candidate.Height <= 0)
            {
                message = "DWM extended frame bounds were empty.";
                return false;
            }

            bounds = candidate;
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    internal static IReadOnlyDictionary<string, object?> BuildDesktopCropOcclusionDetails(IntPtr hwnd, Bounds bounds) =>
        BuildDesktopCropOcclusionDetails(TryGetOcclusion(hwnd, bounds));

    private static Dictionary<string, object?> BuildDesktopCropOcclusionDetails(OcclusionResult occlusion)
    {
        var details = new Dictionary<string, object?>
        {
            ["occluded"] = occlusion.Occluded,
            ["occlusionCheck"] = occlusion.Status
        };
        if (occlusion.Message is not null)
        {
            details["occlusionMessage"] = occlusion.Message;
        }

        return details;
    }

    private static OcclusionResult TryGetOcclusion(IntPtr hwnd, Bounds bounds)
    {
        if (hwnd == IntPtr.Zero)
        {
            return new OcclusionResult(null, "unavailable", "Target HWND was unavailable for occlusion sampling.");
        }

        if (bounds.Width <= 4 || bounds.Height <= 4)
        {
            return new OcclusionResult(null, "unavailable", "Target bounds were too small for occlusion sampling.");
        }

        try
        {
            var targetRoot = Native.GetAncestor(hwnd, Native.GaRoot);
            if (targetRoot == IntPtr.Zero)
            {
                targetRoot = hwnd;
            }

            var points = new[]
            {
                new POINT { X = bounds.X + 2, Y = bounds.Y + 2 },
                new POINT { X = bounds.X + bounds.Width - 3, Y = bounds.Y + 2 },
                new POINT { X = bounds.X + 2, Y = bounds.Y + bounds.Height - 3 },
                new POINT { X = bounds.X + bounds.Width - 3, Y = bounds.Y + bounds.Height - 3 },
                new POINT { X = bounds.CenterX, Y = bounds.CenterY }
            };
            var covered = 0;
            foreach (var point in points)
            {
                var owner = Native.WindowFromPoint(point);
                if (owner == IntPtr.Zero)
                {
                    continue;
                }

                var ownerRoot = Native.GetAncestor(owner, Native.GaRoot);
                if (ownerRoot == IntPtr.Zero)
                {
                    ownerRoot = owner;
                }

                if (ownerRoot != targetRoot)
                {
                    covered++;
                }
            }

            return new OcclusionResult(covered >= 2, "windowFromPoint", $"{covered} of {points.Length} sample points were covered by another root window.");
        }
        catch (Exception ex)
        {
            return new OcclusionResult(null, "unavailable", ex.Message);
        }
    }

    private static Bounds RectToBounds(RECT rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static CaptureResult TryCaptureWindowWithGraphicsCapture(IntPtr hwnd, string path)
    {
        try
        {
            if (Native.IsIconic(hwnd))
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "WGC cannot capture a minimized window because no rendered pixels are available.", CaptureQualityStatus.Unavailable);

            if (!GraphicsCaptureSession.IsSupported())
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "GraphicsCaptureSession is not supported.", CaptureQualityStatus.Unavailable);

            using var d3d = CreateDirect3DDevice();
            var item = CreateCaptureItemForWindow(hwnd);
            return CaptureGraphicsItem(item, d3d.Device, path);
        }
        catch (Exception ex)
        {
            return new CaptureResult(false, "Windows.Graphics.Capture", null, ex.Message, CaptureQualityStatus.Unavailable);
        }
    }

    private static CaptureResult TryCapturePrimaryMonitorWithGraphicsCapture(string path)
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "GraphicsCaptureSession is not supported.", CaptureQualityStatus.Unavailable);

            var monitor = Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
            if (monitor == IntPtr.Zero)
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "Primary monitor handle was not found.", CaptureQualityStatus.Unavailable);

            using var d3d = CreateDirect3DDevice();
            var item = CreateCaptureItemForMonitor(monitor);
            return CaptureGraphicsItem(item, d3d.Device, path);
        }
        catch (Exception ex)
        {
            return new CaptureResult(false, "Windows.Graphics.Capture", null, ex.Message, CaptureQualityStatus.Unavailable);
        }
    }

    private static CaptureResult CaptureGraphicsItem(GraphicsCaptureItem? item, IDirect3DDevice device, string path)
    {
        try
        {
            if (item is null || item.Size.Width <= 0 || item.Size.Height <= 0)
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "Could not create a valid GraphicsCaptureItem.", CaptureQualityStatus.Unavailable);

            using var frameReady = new AutoResetEvent(false);
            Direct3D11CaptureFrame? captured = null;
            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                item.Size);
            var session = framePool.CreateCaptureSession(item);
            framePool.FrameArrived += (sender, _) =>
            {
                try
                {
                    captured?.Dispose();
                    captured = sender.TryGetNextFrame();
                    frameReady.Set();
                }
                catch
                {
                    frameReady.Set();
                }
            };
            session.StartCapture();
            if (!frameReady.WaitOne(TimeSpan.FromSeconds(2)) || captured is null)
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "No capture frame arrived before timeout.", CaptureQualityStatus.Unavailable);

            SaveSurfaceAsBmp(captured.Surface, path);
            return new CaptureResult(true, "Windows.Graphics.Capture", path);
        }
        catch (Exception ex)
        {
            return new CaptureResult(false, "Windows.Graphics.Capture", null, ex.Message, CaptureQualityStatus.Unavailable);
        }
    }

    private static void SaveSurfaceAsBmp(IDirect3DSurface surface, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(surface).AsTask().GetAwaiter().GetResult();
        if (!File.Exists(path)) File.WriteAllBytes(path, Array.Empty<byte>());
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var stream = file.OpenAsync(FileAccessMode.ReadWrite).AsTask().GetAwaiter().GetResult();
        try
        {
            stream.Size = 0;
            var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream).AsTask().GetAwaiter().GetResult();
            encoder.SetSoftwareBitmap(bitmap);
            encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static GraphicsCaptureItem? CreateCaptureItemForWindow(IntPtr hwnd)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hstring = IntPtr.Zero;
        var factoryPtr = IntPtr.Zero;
        try
        {
            var hrString = Native.WindowsCreateString(className, className.Length, out hstring);
            if (hrString < 0) Marshal.ThrowExceptionForHR(hrString);
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            var hrFactory = Native.RoGetActivationFactory(hstring, ref interopIid, out factoryPtr);
            if (hrFactory < 0) Marshal.ThrowExceptionForHR(hrFactory);
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var itemPtr = interop.CreateForWindow(hwnd, ref itemIid);
            if (itemPtr == IntPtr.Zero) return null;
            try
            {
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            if (hstring != IntPtr.Zero) Native.WindowsDeleteString(hstring);
        }
    }

    private static GraphicsCaptureItem? CreateCaptureItemForMonitor(IntPtr monitor)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hstring = IntPtr.Zero;
        var factoryPtr = IntPtr.Zero;
        try
        {
            var hrString = Native.WindowsCreateString(className, className.Length, out hstring);
            if (hrString < 0) Marshal.ThrowExceptionForHR(hrString);
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            var hrFactory = Native.RoGetActivationFactory(hstring, ref interopIid, out factoryPtr);
            if (hrFactory < 0) Marshal.ThrowExceptionForHR(hrFactory);
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var itemPtr = interop.CreateForMonitor(monitor, ref itemIid);
            if (itemPtr == IntPtr.Zero) return null;
            try
            {
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            if (hstring != IntPtr.Zero) Native.WindowsDeleteString(hstring);
        }
    }

    private static Direct3DDeviceHandle CreateDirect3DDevice()
    {
        var hr = Native.D3D11CreateDevice(
            IntPtr.Zero,
            1,
            IntPtr.Zero,
            0x20,
            null,
            0,
            7,
            out var d3dDevice,
            out _,
            out var d3dContext);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        var hr2 = Native.CreateDirect3D11DeviceFromDXGIDevice(d3dDevice, out var winRtDevice);
        if (hr2 < 0) Marshal.ThrowExceptionForHR(hr2);
        var device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winRtDevice);
        Marshal.Release(winRtDevice);
        return new Direct3DDeviceHandle(device, d3dDevice, d3dContext);
    }

    private static CaptureResult CaptureRegion(IntPtr hwnd, Bounds bounds, string path, string method, Bounds? cropBounds = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return new CaptureResult(false, method, null, "Capture bounds were empty.", CaptureQualityStatus.Unavailable);

        var screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return new CaptureResult(false, method, null, "GetDC returned null.", CaptureQualityStatus.Unavailable);

        var memoryDc = Native.CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return new CaptureResult(false, method, null, "CreateCompatibleDC returned null.", CaptureQualityStatus.Unavailable);
        }

        var bitmap = Native.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
        if (bitmap == IntPtr.Zero)
        {
            Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return new CaptureResult(false, method, null, "CreateCompatibleBitmap returned null.", CaptureQualityStatus.Unavailable);
        }

        var old = Native.SelectObject(memoryDc, bitmap);
        try
        {
            var ok = hwnd != IntPtr.Zero && method == "PrintWindow"
                ? Native.PrintWindow(hwnd, memoryDc, 0)
                : Native.BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, 0x00CC0020);
            if (!ok) return new CaptureResult(false, method, null, new Win32Exception(Marshal.GetLastWin32Error()).Message, CaptureQualityStatus.Unavailable);
            var pixels = ReadPixels(memoryDc, bitmap, bounds.Width, bounds.Height);
            var outputBounds = bounds;
            if (cropBounds is not null && TryCropPixels(pixels, bounds, cropBounds, out var croppedPixels, out var croppedBounds))
            {
                pixels = croppedPixels;
                outputBounds = croppedBounds;
            }

            SaveBmp(path, outputBounds.Width, outputBounds.Height, pixels);
            return new CaptureResult(true, method, path, Details: new Dictionary<string, object?>
            {
                ["sourceBounds"] = bounds,
                ["outputBounds"] = outputBounds,
                ["croppedToDwmBounds"] = cropBounds is not null && outputBounds != bounds
            });
        }
        catch (Exception ex)
        {
            return new CaptureResult(false, method, null, ex.Message, CaptureQualityStatus.Unavailable);
        }
        finally
        {
            Native.SelectObject(memoryDc, old);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static bool TryCropPixels(byte[] pixels, Bounds sourceBounds, Bounds cropBounds, out byte[] croppedPixels, out Bounds croppedBounds)
    {
        croppedPixels = Array.Empty<byte>();
        croppedBounds = sourceBounds;
        var offsetX = cropBounds.X - sourceBounds.X;
        var offsetY = cropBounds.Y - sourceBounds.Y;
        if (offsetX < 0 || offsetY < 0 || cropBounds.Width <= 0 || cropBounds.Height <= 0 ||
            offsetX + cropBounds.Width > sourceBounds.Width || offsetY + cropBounds.Height > sourceBounds.Height)
        {
            return false;
        }

        var sourceStride = sourceBounds.Width * 4;
        var cropStride = cropBounds.Width * 4;
        croppedPixels = new byte[cropStride * cropBounds.Height];
        for (var row = 0; row < cropBounds.Height; row++)
        {
            var sourceIndex = ((offsetY + row) * sourceStride) + (offsetX * 4);
            var destinationIndex = row * cropStride;
            Array.Copy(pixels, sourceIndex, croppedPixels, destinationIndex, cropStride);
        }

        croppedBounds = cropBounds;
        return true;
    }

    private static byte[] ReadPixels(IntPtr dc, IntPtr bitmap, int width, int height)
    {
        var info = BitmapInfo.For32BppTopDown(width, height);
        var pixels = new byte[width * height * 4];
        var read = Native.GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0);
        if (read == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return pixels;
    }

    private static void SaveBmp(string path, int width, int height, byte[] pixels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        var pixelSize = pixels.Length;
        var fileSize = 14 + 40 + pixelSize;
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write(0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(pixelSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        writer.Write(pixels);
    }

    private static void AnnotateBmp(string path, IEnumerable<Bounds> rectangles, Bounds origin)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54) return;
        var width = BitConverter.ToInt32(bytes, 18);
        var height = Math.Abs(BitConverter.ToInt32(bytes, 22));
        var offset = BitConverter.ToInt32(bytes, 10);
        var bitCount = BitConverter.ToInt16(bytes, 28);
        if (bitCount != 32) return;
        foreach (var rect in rectangles.Take(120))
        {
            DrawRectangle(bytes, offset, width, height, new Bounds(rect.X - origin.X, rect.Y - origin.Y, rect.Width, rect.Height));
        }
        File.WriteAllBytes(path, bytes);
    }

    private static void DrawRectangle(byte[] bytes, int offset, int width, int height, Bounds rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var x1 = Math.Clamp(rect.X, 0, width - 1);
        var y1 = Math.Clamp(rect.Y, 0, height - 1);
        var x2 = Math.Clamp(rect.X + rect.Width, 0, width - 1);
        var y2 = Math.Clamp(rect.Y + rect.Height, 0, height - 1);
        for (var x = x1; x <= x2; x++)
        {
            SetPixel(bytes, offset, width, height, x, y1);
            SetPixel(bytes, offset, width, height, x, y2);
        }
        for (var y = y1; y <= y2; y++)
        {
            SetPixel(bytes, offset, width, height, x1, y);
            SetPixel(bytes, offset, width, height, x2, y);
        }
    }

    private static void SetPixel(byte[] bytes, int offset, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return;
        var index = offset + ((y * width) + x) * 4;
        if (index + 3 >= bytes.Length) return;
        bytes[index] = 0;
        bytes[index + 1] = 0;
        bytes[index + 2] = 255;
        bytes[index + 3] = 255;
    }

    private static IEnumerable<ElementSnapshot> Flatten(IEnumerable<ElementSnapshot> elements)
    {
        foreach (var element in elements)
        {
            yield return element;
            if (element.Children is null) continue;
            foreach (var child in Flatten(element.Children)) yield return child;
        }
    }

    private sealed record WindowCaptureBounds(
        Bounds Win32Bounds,
        Bounds CaptureBounds,
        Bounds? DwmExtendedFrameBounds,
        string Source);

    private sealed record OcclusionResult(bool? Occluded, string Status, string? Message);
}

internal sealed class Direct3DDeviceHandle(IDirect3DDevice device, IntPtr d3dDevice, IntPtr d3dContext) : IDisposable
{
    public IDirect3DDevice Device { get; } = device;

    public void Dispose()
    {
        if (d3dContext != IntPtr.Zero) Marshal.Release(d3dContext);
        if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
    }
}

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow(IntPtr window, ref Guid iid);
    IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
}

internal interface IWindowsDoctorDiagnosticProvider
{
    WindowsDoctorDiagnostics Collect();
}

internal sealed record WindowsDoctorDiagnostics(
    bool IsWindows,
    string OsDescription,
    WindowsSessionDiagnostics Session,
    WindowsDesktopDiagnostics Desktop,
    WindowsForegroundDiagnostics Foreground,
    WindowsIntegrityDiagnostics Integrity,
    WindowsUiaDiagnostics Uia,
    WindowsCaptureDiagnostics Capture,
    WindowsOcrDiagnostics Ocr,
    WindowsDpiDiagnostics Dpi);

internal sealed record WindowsSessionDiagnostics(
    bool Available,
    int ProcessId,
    int? SessionId,
    uint? ActiveConsoleSessionId,
    string? Error);

internal sealed record WindowsDesktopDiagnostics(
    bool UserInteractive,
    bool WindowStationOpen,
    bool WindowStationQueried,
    string? WindowStationName,
    int? WindowStationLastError,
    string? WindowStationError,
    bool DefaultDesktopOpen,
    bool DefaultDesktopQueried,
    string? DefaultDesktopName,
    int? DefaultDesktopLastError,
    string? DefaultDesktopError);

internal sealed record WindowsForegroundDiagnostics(
    bool Available,
    IntPtr Hwnd,
    int? ProcessId,
    string? ClassName,
    string? Error);

internal sealed record WindowsIntegrityDiagnostics(
    bool Available,
    int? Rid,
    string? Label,
    string? Error);

internal sealed record WindowsUiaDiagnostics(
    bool Available,
    bool TimedOut,
    int TimeoutMs,
    string? Error);

internal sealed record WindowsCaptureDiagnostics(
    bool WindowsGraphicsCaptureSupported,
    bool PrintWindowFallbackAvailable,
    bool BitBltFallbackAvailable,
    string? Error);

internal sealed record WindowsOcrDiagnostics(bool Available, string? Error);

internal sealed record WindowsDpiDiagnostics(int SystemMetricsWidth, int SystemMetricsHeight);

public sealed class WindowsDoctorCheckService : IDoctorCheckService
{
    private readonly IWindowsDoctorDiagnosticProvider provider;

    public WindowsDoctorCheckService()
        : this(new NativeWindowsDoctorDiagnosticProvider())
    {
    }

    internal WindowsDoctorCheckService(IWindowsDoctorDiagnosticProvider provider)
    {
        this.provider = provider;
    }

    public IReadOnlyList<DoctorCheck> RunChecks(PbwConfig config)
    {
        var diagnostics = CollectDiagnostics();
        var checks = new List<DoctorCheck>
        {
            OsCheck(diagnostics),
            SessionCheck(diagnostics),
            InteractiveDesktopCheck(diagnostics),
            ForegroundCheck(diagnostics),
            IntegrityCheck(diagnostics),
            UiaCheck(diagnostics),
            CaptureCheck(diagnostics),
            OcrCheck(diagnostics),
            DpiCheck(diagnostics),
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

    private WindowsDoctorDiagnostics CollectDiagnostics()
    {
        try
        {
            return provider.Collect();
        }
        catch (Exception ex)
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            return new WindowsDoctorDiagnostics(
                isWindows,
                RuntimeInformation.OSDescription,
                new WindowsSessionDiagnostics(false, Environment.ProcessId, null, null, ex.Message),
                new WindowsDesktopDiagnostics(Environment.UserInteractive, false, false, null, null, ex.Message, false, false, null, null, ex.Message),
                new WindowsForegroundDiagnostics(false, IntPtr.Zero, null, null, ex.Message),
                new WindowsIntegrityDiagnostics(false, null, null, ex.Message),
                new WindowsUiaDiagnostics(false, false, 0, ex.Message),
                new WindowsCaptureDiagnostics(false, isWindows, isWindows, ex.Message),
                new WindowsOcrDiagnostics(false, ex.Message),
                new WindowsDpiDiagnostics(0, 0));
        }
    }

    private static DoctorCheck OsCheck(WindowsDoctorDiagnostics diagnostics) => new(
        "os",
        diagnostics.IsWindows ? "ok" : "warning",
        diagnostics.OsDescription,
        new Dictionary<string, object?> { ["isWindows"] = diagnostics.IsWindows });

    private static DoctorCheck SessionCheck(WindowsDoctorDiagnostics diagnostics)
    {
        var session = diagnostics.Session;
        var isSession0 = session.SessionId == 0;
        var status = !diagnostics.IsWindows || !session.Available
            ? "warning"
            : isSession0 ? "error" : "ok";
        var message = status switch
        {
            "ok" => $"Current process is running in session {session.SessionId}.",
            "error" => "Current process is running in Session 0; interactive desktop automation will not work here.",
            _ => session.Error ?? "Current process session id could not be determined."
        };

        return new DoctorCheck("session", status, message, new Dictionary<string, object?>
        {
            ["processId"] = session.ProcessId,
            ["sessionId"] = session.SessionId,
            ["isSession0"] = isSession0,
            ["activeConsoleSessionId"] = session.ActiveConsoleSessionId,
            ["error"] = session.Error
        });
    }

    private static DoctorCheck InteractiveDesktopCheck(WindowsDoctorDiagnostics diagnostics)
    {
        var desktop = diagnostics.Desktop;
        var open = desktop.WindowStationOpen && desktop.DefaultDesktopOpen;
        var queried = desktop.WindowStationQueried && desktop.DefaultDesktopQueried;
        var status = !diagnostics.IsWindows
            ? "warning"
            : !open ? "error" : !queried || !desktop.UserInteractive ? "warning" : "ok";
        var message = status switch
        {
            "ok" => "WinSta0 and the Default desktop are openable and queryable.",
            "error" => "WinSta0 or the Default desktop could not be opened; GUI automation is unavailable from this process.",
            _ when open && !desktop.UserInteractive => "The interactive desktop is openable, but the process is not marked user-interactive.",
            _ => "WinSta0 or the Default desktop opened, but query details were not fully available."
        };

        return new DoctorCheck("interactiveDesktop", status, message, new Dictionary<string, object?>
        {
            ["userInteractive"] = desktop.UserInteractive,
            ["windowStation"] = new Dictionary<string, object?>
            {
                ["name"] = desktop.WindowStationName,
                ["open"] = desktop.WindowStationOpen,
                ["queried"] = desktop.WindowStationQueried,
                ["lastError"] = desktop.WindowStationLastError,
                ["error"] = desktop.WindowStationError
            },
            ["desktop"] = new Dictionary<string, object?>
            {
                ["name"] = desktop.DefaultDesktopName,
                ["open"] = desktop.DefaultDesktopOpen,
                ["queried"] = desktop.DefaultDesktopQueried,
                ["lastError"] = desktop.DefaultDesktopLastError,
                ["error"] = desktop.DefaultDesktopError
            }
        });
    }

    private static DoctorCheck ForegroundCheck(WindowsDoctorDiagnostics diagnostics)
    {
        var foreground = diagnostics.Foreground;
        var status = diagnostics.IsWindows && foreground.Available ? "ok" : "warning";
        var message = foreground.Available
            ? "A foreground window is available on the current desktop."
            : foreground.Error ?? "No foreground window is available; the desktop may be locked, headless, or non-interactive.";
        return new DoctorCheck("foreground", status, message, new Dictionary<string, object?>
        {
            ["available"] = foreground.Available,
            ["hwnd"] = foreground.Hwnd == IntPtr.Zero ? null : FormatHwnd(foreground.Hwnd),
            ["processId"] = foreground.ProcessId,
            ["className"] = foreground.ClassName,
            ["error"] = foreground.Error
        });
    }

    private static DoctorCheck IntegrityCheck(WindowsDoctorDiagnostics diagnostics)
    {
        var integrity = diagnostics.Integrity;
        var lowIntegrity = integrity.Rid is not null && integrity.Rid < 0x00002000;
        var status = !diagnostics.IsWindows || !integrity.Available || lowIntegrity ? "warning" : "ok";
        var message = integrity.Available
            ? $"Current process integrity level is {integrity.Label ?? "unknown"}."
            : integrity.Error ?? "Current process integrity level could not be determined.";
        return new DoctorCheck("integrity", status, message, new Dictionary<string, object?>
        {
            ["available"] = integrity.Available,
            ["rid"] = integrity.Rid,
            ["label"] = integrity.Label,
            ["error"] = integrity.Error
        });
    }

    private static DoctorCheck UiaCheck(WindowsDoctorDiagnostics diagnostics)
    {
        var uia = diagnostics.Uia;
        var status = diagnostics.IsWindows && uia.Available ? "ok" : "warning";
        var message = uia.Available
            ? "UI Automation root element smoke check succeeded."
            : uia.TimedOut
                ? "UI Automation smoke check timed out."
                : uia.Error ?? "UI Automation smoke check did not complete successfully.";
        return new DoctorCheck("uia", status, message, new Dictionary<string, object?>
        {
            ["available"] = uia.Available,
            ["timedOut"] = uia.TimedOut,
            ["timeoutMs"] = uia.TimeoutMs,
            ["error"] = uia.Error
        });
    }

    private static DoctorCheck CaptureCheck(WindowsDoctorDiagnostics diagnostics)
    {
        var capture = diagnostics.Capture;
        var hasFallback = capture.PrintWindowFallbackAvailable && capture.BitBltFallbackAvailable;
        var status = !diagnostics.IsWindows
            ? "warning"
            : capture.WindowsGraphicsCaptureSupported ? "ok" : hasFallback ? "warning" : "error";
        var message = capture.WindowsGraphicsCaptureSupported
            ? "Windows.Graphics.Capture is available; PrintWindow and BitBlt fallbacks are available."
            : hasFallback
                ? "Windows.Graphics.Capture is unavailable; PrintWindow and BitBlt fallbacks are available."
                : capture.Error ?? "No usable capture backend was detected.";
        return new DoctorCheck("capture", status, message, new Dictionary<string, object?>
        {
            ["windowsGraphicsCaptureSupported"] = capture.WindowsGraphicsCaptureSupported,
            ["printWindowFallbackAvailable"] = capture.PrintWindowFallbackAvailable,
            ["bitBltFallbackAvailable"] = capture.BitBltFallbackAvailable,
            ["error"] = capture.Error
        });
    }

    private static DoctorCheck OcrCheck(WindowsDoctorDiagnostics diagnostics)
    {
        var ocr = diagnostics.Ocr;
        return new DoctorCheck(
            "ocr",
            ocr.Available ? "ok" : "warning",
            ocr.Available ? "Windows OCR engine is available." : ocr.Error ?? "Windows OCR engine is unavailable for the current user languages.",
            new Dictionary<string, object?> { ["available"] = ocr.Available, ["error"] = ocr.Error });
    }

    private static DoctorCheck DpiCheck(WindowsDoctorDiagnostics diagnostics)
    {
        var dpi = diagnostics.Dpi;
        var valid = dpi.SystemMetricsWidth > 0 && dpi.SystemMetricsHeight > 0;
        return new DoctorCheck(
            "dpi",
            valid ? "ok" : "warning",
            valid ? "Coordinate converter uses display scale metadata." : "Display system metrics are unavailable.",
            new Dictionary<string, object?> { ["systemMetricsWidth"] = dpi.SystemMetricsWidth, ["systemMetricsHeight"] = dpi.SystemMetricsHeight });
    }

    private static string FormatHwnd(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("x");
}

internal sealed class NativeWindowsDoctorDiagnosticProvider : IWindowsDoctorDiagnosticProvider
{
    private const uint NoActiveConsoleSession = 0xFFFFFFFF;
    private const uint WindowStationReadAttributes = 0x0002;
    private const uint WindowStationEnumerateDesktops = 0x0001;
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopEnumerate = 0x0040;
    private const int UoiName = 2;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int UiaTimeoutMs = 2000;

    public WindowsDoctorDiagnostics Collect()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        return new WindowsDoctorDiagnostics(
            isWindows,
            RuntimeInformation.OSDescription,
            Session(isWindows),
            Desktop(isWindows),
            Foreground(isWindows),
            Integrity(isWindows),
            Uia(isWindows),
            Capture(isWindows),
            Ocr(isWindows),
            Dpi(isWindows));
    }

    private static WindowsSessionDiagnostics Session(bool isWindows)
    {
        if (!isWindows)
        {
            return new WindowsSessionDiagnostics(false, Environment.ProcessId, null, null, "not_windows");
        }

        try
        {
            var ok = Native.ProcessIdToSessionId(Environment.ProcessId, out var sessionId);
            var active = Native.WTSGetActiveConsoleSessionId();
            return ok
                ? new WindowsSessionDiagnostics(true, Environment.ProcessId, sessionId, active == NoActiveConsoleSession ? null : active, null)
                : new WindowsSessionDiagnostics(false, Environment.ProcessId, null, active == NoActiveConsoleSession ? null : active, LastWin32Error());
        }
        catch (Exception ex)
        {
            return new WindowsSessionDiagnostics(false, Environment.ProcessId, null, null, ex.Message);
        }
    }

    private static WindowsDesktopDiagnostics Desktop(bool isWindows)
    {
        if (!isWindows)
        {
            return new WindowsDesktopDiagnostics(Environment.UserInteractive, false, false, null, null, "not_windows", false, false, null, null, "not_windows");
        }

        var station = ProbeWindowStation();
        var desktop = ProbeDefaultDesktop();
        return new WindowsDesktopDiagnostics(
            Environment.UserInteractive,
            station.Open,
            station.Queried,
            station.Name,
            station.LastError,
            station.Error,
            desktop.Open,
            desktop.Queried,
            desktop.Name,
            desktop.LastError,
            desktop.Error);
    }

    private static WindowsForegroundDiagnostics Foreground(bool isWindows)
    {
        if (!isWindows)
        {
            return new WindowsForegroundDiagnostics(false, IntPtr.Zero, null, null, "not_windows");
        }

        try
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return new WindowsForegroundDiagnostics(false, IntPtr.Zero, null, null, "GetForegroundWindow returned null.");
            }

            Native.GetWindowThreadProcessId(hwnd, out var processId);
            return new WindowsForegroundDiagnostics(true, hwnd, processId, ClassName(hwnd), null);
        }
        catch (Exception ex)
        {
            return new WindowsForegroundDiagnostics(false, IntPtr.Zero, null, null, ex.Message);
        }
    }

    private static WindowsIntegrityDiagnostics Integrity(bool isWindows)
    {
        if (!isWindows)
        {
            return new WindowsIntegrityDiagnostics(false, null, null, "not_windows");
        }

        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), TokenQuery, out token))
            {
                return new WindowsIntegrityDiagnostics(false, null, null, LastWin32Error());
            }

            Native.GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                return new WindowsIntegrityDiagnostics(false, null, null, LastWin32Error());
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!Native.GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
            {
                return new WindowsIntegrityDiagnostics(false, null, null, LastWin32Error());
            }

            var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
            var countPtr = Native.GetSidSubAuthorityCount(label.Label.Sid);
            if (countPtr == IntPtr.Zero)
            {
                return new WindowsIntegrityDiagnostics(false, null, null, "GetSidSubAuthorityCount returned null.");
            }

            var count = Marshal.ReadByte(countPtr);
            if (count == 0)
            {
                return new WindowsIntegrityDiagnostics(false, null, null, "Integrity SID has no sub-authorities.");
            }

            var ridPtr = Native.GetSidSubAuthority(label.Label.Sid, count - 1);
            if (ridPtr == IntPtr.Zero)
            {
                return new WindowsIntegrityDiagnostics(false, null, null, "GetSidSubAuthority returned null.");
            }

            var rid = Marshal.ReadInt32(ridPtr);
            return new WindowsIntegrityDiagnostics(true, rid, IntegrityLabel(rid), null);
        }
        catch (Exception ex)
        {
            return new WindowsIntegrityDiagnostics(false, null, null, ex.Message);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                Native.CloseHandle(token);
            }
        }
    }

    private static WindowsUiaDiagnostics Uia(bool isWindows)
    {
        if (!isWindows)
        {
            return new WindowsUiaDiagnostics(false, false, UiaTimeoutMs, "not_windows");
        }

        try
        {
            var task = Task.Run(() =>
            {
                var root = AutomationElement.RootElement;
                _ = root.Current.ControlType;
            });
            if (!task.Wait(TimeSpan.FromMilliseconds(UiaTimeoutMs)))
            {
                return new WindowsUiaDiagnostics(false, true, UiaTimeoutMs, "UI Automation smoke check timed out.");
            }

            task.GetAwaiter().GetResult();
            return new WindowsUiaDiagnostics(true, false, UiaTimeoutMs, null);
        }
        catch (AggregateException ex)
        {
            return new WindowsUiaDiagnostics(false, false, UiaTimeoutMs, (ex.Flatten().InnerException ?? ex).Message);
        }
        catch (Exception ex)
        {
            return new WindowsUiaDiagnostics(false, false, UiaTimeoutMs, ex.Message);
        }
    }

    private static WindowsCaptureDiagnostics Capture(bool isWindows)
    {
        if (!isWindows)
        {
            return new WindowsCaptureDiagnostics(false, false, false, "not_windows");
        }

        try
        {
            return new WindowsCaptureDiagnostics(GraphicsCaptureSession.IsSupported(), true, true, null);
        }
        catch (Exception ex)
        {
            return new WindowsCaptureDiagnostics(false, true, true, ex.Message);
        }
    }

    private static WindowsOcrDiagnostics Ocr(bool isWindows)
    {
        if (!isWindows)
        {
            return new WindowsOcrDiagnostics(false, "not_windows");
        }

        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages() is null
                ? new WindowsOcrDiagnostics(false, "Windows OCR engine is unavailable for the current user languages.")
                : new WindowsOcrDiagnostics(true, null);
        }
        catch (Exception ex)
        {
            return new WindowsOcrDiagnostics(false, ex.Message);
        }
    }

    private static WindowsDpiDiagnostics Dpi(bool isWindows)
    {
        if (!isWindows)
        {
            return new WindowsDpiDiagnostics(0, 0);
        }

        try
        {
            return new WindowsDpiDiagnostics(Native.GetSystemMetrics(0), Native.GetSystemMetrics(1));
        }
        catch
        {
            return new WindowsDpiDiagnostics(0, 0);
        }
    }

    private static ObjectProbe ProbeWindowStation()
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = Native.OpenWindowStation("WinSta0", false, WindowStationReadAttributes | WindowStationEnumerateDesktops);
            if (handle == IntPtr.Zero)
            {
                return new ObjectProbe(false, false, null, Marshal.GetLastWin32Error(), LastWin32Error());
            }

            var query = QueryUserObjectName(handle);
            return new ObjectProbe(true, query.Success, query.Name, query.LastError, query.Error);
        }
        catch (Exception ex)
        {
            return new ObjectProbe(false, false, null, null, ex.Message);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                Native.CloseWindowStation(handle);
            }
        }
    }

    private static ObjectProbe ProbeDefaultDesktop()
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = Native.OpenDesktop("Default", 0, false, DesktopReadObjects | DesktopEnumerate);
            if (handle == IntPtr.Zero)
            {
                return new ObjectProbe(false, false, null, Marshal.GetLastWin32Error(), LastWin32Error());
            }

            var query = QueryUserObjectName(handle);
            return new ObjectProbe(true, query.Success, query.Name, query.LastError, query.Error);
        }
        catch (Exception ex)
        {
            return new ObjectProbe(false, false, null, null, ex.Message);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                Native.CloseDesktop(handle);
            }
        }
    }

    private static ObjectNameProbe QueryUserObjectName(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        var ok = Native.GetUserObjectInformation(handle, UoiName, buffer, buffer.Capacity * 2, out _);
        if (ok)
        {
            return new ObjectNameProbe(true, buffer.ToString(), null, null);
        }

        var error = Marshal.GetLastWin32Error();
        return new ObjectNameProbe(false, null, error, new Win32Exception(error).Message);
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var length = Native.GetClassName(hwnd, sb, sb.Capacity);
        return length <= 0 ? "" : sb.ToString();
    }

    private static string LastWin32Error()
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error).Message;
    }

    private static string IntegrityLabel(int rid) => rid switch
    {
        < 0x00001000 => "untrusted",
        < 0x00002000 => "low",
        < 0x00003000 => "medium",
        < 0x00004000 => "high",
        < 0x00005000 => "system",
        _ => "protected"
    };

    private sealed record ObjectProbe(bool Open, bool Queried, string? Name, int? LastError, string? Error);
    private sealed record ObjectNameProbe(bool Success, string? Name, int? LastError, string? Error);
}

internal static partial class Native
{
    internal const uint GaRoot = 2;
    internal const int DwmwaExtendedFrameBounds = 9;

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] internal static extern IntPtr ChildWindowFromPointEx(IntPtr hwndParent, POINT pt, uint flags);
    [DllImport("user32.dll")] internal static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll")] internal static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] internal static extern void keybd_event(byte bVk, byte bScan, int dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern short VkKeyScan(char ch);
    [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BitmapInfo lpbmi, uint usage);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("d3d11.dll", SetLastError = true)]
    internal static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        int[]? featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", SetLastError = true)] internal static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll", SetLastError = true)] internal static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);
    [DllImport("combase.dll", SetLastError = true)] internal static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll", SetLastError = true)] internal static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ProcessIdToSessionId(int dwProcessId, out int pSessionId);
    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr hObject);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseWindowStation(IntPtr hWinSta);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern IntPtr GetSidSubAuthority(IntPtr sid, int subAuthority);
    [DllImport("oleacc.dll", PreserveSig = true)]
    internal static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        uint dwId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppvObject);
}

internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_MANDATORY_LABEL
{
    public SID_AND_ATTRIBUTES Label;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SID_AND_ATTRIBUTES
{
    public IntPtr Sid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfo
{
    public BitmapInfoHeader Header;
    public uint Colors;

    public static BitmapInfo For32BppTopDown(int width, int height) => new()
    {
        Header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
            SizeImage = (uint)(width * height * 4),
            XPelsPerMeter = 2835,
            YPelsPerMeter = 2835,
            ClrUsed = 0,
            ClrImportant = 0
        },
        Colors = 0
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ClrUsed;
    public uint ClrImportant;
}
