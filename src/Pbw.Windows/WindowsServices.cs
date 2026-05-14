using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Windows.Automation;
using Pbw.Core;

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

public sealed record CaptureResult(bool Success, string Method, string? ImagePath, string? Message = null);

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
                ["captureStatus"] = captureResult.Success ? "ok" : "degraded",
                ["captureMessage"] = captureResult.Message,
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
    private const int MaxDepth = 5;
    private const int MaxChildrenPerNode = 80;

    public IReadOnlyList<ElementSnapshot> ReadTree()
    {
        try
        {
            var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            return roots.Cast<AutomationElement>()
                .Take(MaxChildrenPerNode)
                .Select((element, index) => ToSnapshot(element, "uia", index, 0))
                .Where(e => e is not null)
                .Cast<ElementSnapshot>()
                .ToArray();
        }
        catch (Exception ex)
        {
            return new[] { new ElementSnapshot("uia-error", ex.Message, "error", new Bounds(0, 0, 0, 0), Enabled: false) };
        }
    }

    public ActionResult SetValue(TargetSpec target, string value)
    {
        var element = FindElement(target);
        if (element is null) return NotFound("set-value", target);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
        {
            valuePattern.SetValue(value);
            return new ActionResult("set-value", true, "UIAutomation.ValuePattern", ElementId(element));
        }

        return new ActionResult("set-value", false, "UIAutomation", ElementId(element), "Target does not support ValuePattern.", PatternDetails(element));
    }

    public ActionResult PerformAction(TargetSpec target, string action)
    {
        var element = FindElement(target);
        if (element is null) return NotFound("perform-action", target);
        var normalized = action.ToLowerInvariant();
        try
        {
            switch (normalized)
            {
                case "invoke":
                case "click":
                    if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke) && invoke is InvokePattern invokePattern)
                    {
                        invokePattern.Invoke();
                        return new ActionResult("perform-action", true, "UIAutomation.InvokePattern", ElementId(element));
                    }
                    break;
                case "toggle":
                    if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle) && toggle is TogglePattern togglePattern)
                    {
                        togglePattern.Toggle();
                        return new ActionResult("perform-action", true, "UIAutomation.TogglePattern", ElementId(element));
                    }
                    break;
                case "select":
                    if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select) && select is SelectionItemPattern selectionPattern)
                    {
                        selectionPattern.Select();
                        return new ActionResult("perform-action", true, "UIAutomation.SelectionItemPattern", ElementId(element));
                    }
                    break;
                case "expand":
                case "collapse":
                    if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) && expand is ExpandCollapsePattern expandPattern)
                    {
                        if (normalized == "expand") expandPattern.Expand(); else expandPattern.Collapse();
                        return new ActionResult("perform-action", true, "UIAutomation.ExpandCollapsePattern", ElementId(element));
                    }
                    break;
                case "scroll-into-view":
                    if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll) && scroll is ScrollItemPattern scrollPattern)
                    {
                        scrollPattern.ScrollIntoView();
                        return new ActionResult("perform-action", true, "UIAutomation.ScrollItemPattern", ElementId(element));
                    }
                    break;
                case "focus":
                    element.SetFocus();
                    return new ActionResult("perform-action", true, "UIAutomation.SetFocus", ElementId(element));
            }
        }
        catch (Exception ex)
        {
            return new ActionResult("perform-action", false, "UIAutomation", ElementId(element), ex.Message, PatternDetails(element));
        }

        return new ActionResult("perform-action", false, "UIAutomation", ElementId(element), $"Target does not support action '{action}'.", PatternDetails(element));
    }

    public IReadOnlyList<MenuItemInfo> ListMenus(TargetSpec target)
    {
        var root = FindElement(target) ?? AutomationElement.RootElement;
        return root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
            .Cast<AutomationElement>()
            .Take(200)
            .Select(e => new MenuItemInfo(SafeName(e), SafeBool(e, AutomationElement.IsEnabledProperty), ElementId(e)))
            .ToArray();
    }

    public ActionResult ClickMenu(TargetSpec target, string menu)
    {
        var item = AutomationElement.RootElement
            .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
            .Cast<AutomationElement>()
            .FirstOrDefault(e => SafeName(e).Contains(menu, StringComparison.OrdinalIgnoreCase));
        if (item is null) return new ActionResult("menu.click", false, "UIAutomation", menu, "Menu item was not found.");
        return InvokeElement("menu.click", item);
    }

    public IReadOnlyList<DialogInfo> ListDialogs()
    {
        return AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))
            .Cast<AutomationElement>()
            .Where(e => SafeName(e).Length > 0)
            .Select(e => new DialogInfo(ElementId(e), SafeName(e), Children(e, ElementId(e), 0)))
            .ToArray();
    }

    public ActionResult ClickDialog(TargetSpec target, string button)
    {
        var dialog = FindElement(target) ?? AutomationElement.RootElement;
        var buttonElement = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>()
            .FirstOrDefault(e => SafeName(e).Contains(button, StringComparison.OrdinalIgnoreCase));
        if (buttonElement is null) return new ActionResult("dialog.click", false, "UIAutomation", button, "Dialog button was not found.");
        return InvokeElement("dialog.click", buttonElement);
    }

    public ActionResult InputDialog(TargetSpec target, string value)
    {
        var dialog = FindElement(target) ?? AutomationElement.RootElement;
        var edit = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
            .Cast<AutomationElement>()
            .FirstOrDefault();
        if (edit is null) return new ActionResult("dialog.input", false, "UIAutomation", null, "Dialog input target was not found.");
        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
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
        var close = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>()
            .FirstOrDefault(e => SafeName(e).Equals("Cancel", StringComparison.OrdinalIgnoreCase) || SafeName(e).Equals("Close", StringComparison.OrdinalIgnoreCase));
        return close is null ? PerformAction(target, "focus") : InvokeElement("dialog.dismiss", close);
    }

    private static ActionResult InvokeElement(string action, AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) && pattern is InvokePattern invoke)
        {
            invoke.Invoke();
            return new ActionResult(action, true, "UIAutomation.InvokePattern", ElementId(element));
        }
        return new ActionResult(action, false, "UIAutomation", ElementId(element), "Target does not support InvokePattern.", PatternDetails(element));
    }

    private AutomationElement? FindElement(TargetSpec target)
    {
        AutomationElement searchRoot = AutomationElement.RootElement;
        if (target.WindowHandle is not null)
        {
            try
            {
                searchRoot = AutomationElement.FromHandle(new IntPtr(target.WindowHandle.Value));
                if (target.AutomationId is null && target.Text is null && target.Role is null && target.X is null && target.Y is null && target.Index is null)
                {
                    return searchRoot;
                }
            }
            catch { }
        }

        var all = searchRoot.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>();
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

    private static ElementSnapshot? ToSnapshot(AutomationElement element, string prefix, int index, int depth)
    {
        try
        {
            var id = ElementId(element);
            var children = depth >= MaxDepth ? Array.Empty<ElementSnapshot>() : Children(element, id, depth + 1);
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
        catch
        {
            return null;
        }
    }

    private static ElementSnapshot[] Children(AutomationElement element, string prefix, int depth)
    {
        try
        {
            return element.FindAll(TreeScope.Children, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Take(MaxChildrenPerNode)
                .Select((child, index) => ToSnapshot(child, prefix, index, depth))
                .Where(e => e is not null)
                .Cast<ElementSnapshot>()
                .ToArray();
        }
        catch
        {
            return Array.Empty<ElementSnapshot>();
        }
    }

    private static IReadOnlyDictionary<string, object?> PatternDetails(AutomationElement element) => new Dictionary<string, object?> { ["patterns"] = Patterns(element) };
    private static bool Contains(Bounds bounds, int x, int y) => x >= bounds.X && x <= bounds.X + bounds.Width && y >= bounds.Y && y <= bounds.Y + bounds.Height;
    private static string ElementId(AutomationElement element) => "uia-" + SafeInt(element, AutomationElement.NativeWindowHandleProperty) + "-" + SafeString(element, AutomationElement.AutomationIdProperty) + "-" + SafeName(element).GetHashCode(StringComparison.Ordinal);
    private static Bounds Bounds(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty) return new Bounds(0, 0, 0, 0);
        return new Bounds((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
    }

    private static string Role(AutomationElement element)
    {
        try { return element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal); }
        catch { return "unknown"; }
    }

    private static IReadOnlyList<string> Patterns(AutomationElement element)
    {
        var patterns = new List<string>();
        AddPattern(element, InvokePattern.Pattern, "Invoke", patterns);
        AddPattern(element, ValuePattern.Pattern, "Value", patterns);
        AddPattern(element, TogglePattern.Pattern, "Toggle", patterns);
        AddPattern(element, SelectionItemPattern.Pattern, "SelectionItem", patterns);
        AddPattern(element, ExpandCollapsePattern.Pattern, "ExpandCollapse", patterns);
        AddPattern(element, ScrollItemPattern.Pattern, "ScrollIntoView", patterns);
        AddPattern(element, WindowPattern.Pattern, "Window", patterns);
        return patterns;
    }

    private static void AddPattern(AutomationElement element, AutomationPattern pattern, string name, List<string> patterns)
    {
        try
        {
            if (element.TryGetCurrentPattern(pattern, out _)) patterns.Add(name);
        }
        catch { }
    }

    private static string SafeName(AutomationElement element) => SafeString(element, AutomationElement.NameProperty);
    private static string SafeString(AutomationElement element, AutomationProperty property)
    {
        try { return element.GetCurrentPropertyValue(property, true) as string ?? ""; }
        catch { return ""; }
    }

    private static bool SafeBool(AutomationElement element, AutomationProperty property)
    {
        try { return element.GetCurrentPropertyValue(property, true) is bool b && b; }
        catch { return false; }
    }

    private static int SafeInt(AutomationElement element, AutomationProperty property)
    {
        try { return element.GetCurrentPropertyValue(property, true) is int i ? i : 0; }
        catch { return 0; }
    }

    private static ActionResult NotFound(string action, TargetSpec target) => new(action, false, "UIAutomation", target.ToString(), "Target was not found.");
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
        var graphicsResult = TryCapturePrimaryMonitorWithGraphicsCapture(imagePath);
        if (graphicsResult.Success)
        {
            AnnotateBmp(imagePath, windows.Select(w => w.Bounds).Concat(Flatten(elements).Select(e => e.Bounds)), bounds);
            return graphicsResult;
        }

        var result = CaptureRegion(IntPtr.Zero, bounds, imagePath, "BitBlt.desktop");
        if (result.Success) AnnotateBmp(imagePath, windows.Select(w => w.Bounds).Concat(Flatten(elements).Select(e => e.Bounds)), bounds);
        return result with { Message = graphicsResult.Message is null ? result.Message : "Windows.Graphics.Capture monitor failed: " + graphicsResult.Message };
    }

    public CaptureResult CaptureWindow(int handle, string imagePath, IReadOnlyList<ElementSnapshot> elements)
    {
        if (!Native.GetWindowRect(new IntPtr(handle), out var rect))
            return new CaptureResult(false, "PrintWindow", null, new Win32Exception(Marshal.GetLastWin32Error()).Message);

        var bounds = new Bounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var graphicsResult = TryCaptureWindowWithGraphicsCapture(new IntPtr(handle), imagePath);
        if (graphicsResult.Success)
        {
            AnnotateBmp(imagePath, Flatten(elements).Select(e => e.Bounds), bounds);
            return graphicsResult;
        }

        var result = CaptureRegion(new IntPtr(handle), bounds, imagePath, "PrintWindow");
        if (!result.Success) result = CaptureRegion(IntPtr.Zero, bounds, imagePath, "BitBlt.desktop-crop");
        if (result.Success) AnnotateBmp(imagePath, Flatten(elements).Select(e => e.Bounds), bounds);
        return result with { Message = graphicsResult.Message is null ? result.Message : "Windows.Graphics.Capture failed: " + graphicsResult.Message };
    }

    private static CaptureResult TryCaptureWindowWithGraphicsCapture(IntPtr hwnd, string path)
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "GraphicsCaptureSession is not supported.");

            using var d3d = CreateDirect3DDevice();
            var item = CreateCaptureItemForWindow(hwnd);
            return CaptureGraphicsItem(item, d3d.Device, path);
        }
        catch (Exception ex)
        {
            return new CaptureResult(false, "Windows.Graphics.Capture", null, ex.Message);
        }
    }

    private static CaptureResult TryCapturePrimaryMonitorWithGraphicsCapture(string path)
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "GraphicsCaptureSession is not supported.");

            var monitor = Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
            if (monitor == IntPtr.Zero)
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "Primary monitor handle was not found.");

            using var d3d = CreateDirect3DDevice();
            var item = CreateCaptureItemForMonitor(monitor);
            return CaptureGraphicsItem(item, d3d.Device, path);
        }
        catch (Exception ex)
        {
            return new CaptureResult(false, "Windows.Graphics.Capture", null, ex.Message);
        }
    }

    private static CaptureResult CaptureGraphicsItem(GraphicsCaptureItem? item, IDirect3DDevice device, string path)
    {
        try
        {
            if (item is null || item.Size.Width <= 0 || item.Size.Height <= 0)
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "Could not create a valid GraphicsCaptureItem.");

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
                return new CaptureResult(false, "Windows.Graphics.Capture", null, "No capture frame arrived before timeout.");

            SaveSurfaceAsBmp(captured.Surface, path);
            return new CaptureResult(true, "Windows.Graphics.Capture", path);
        }
        catch (Exception ex)
        {
            return new CaptureResult(false, "Windows.Graphics.Capture", null, ex.Message);
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

    private static CaptureResult CaptureRegion(IntPtr hwnd, Bounds bounds, string path, string method)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return new CaptureResult(false, method, null, "Capture bounds were empty.");
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memoryDc = Native.CreateCompatibleDC(screenDc);
        var bitmap = Native.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
        var old = Native.SelectObject(memoryDc, bitmap);
        try
        {
            var ok = hwnd != IntPtr.Zero && method == "PrintWindow"
                ? Native.PrintWindow(hwnd, memoryDc, 0)
                : Native.BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, 0x00CC0020);
            if (!ok) return new CaptureResult(false, method, null, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var pixels = ReadPixels(memoryDc, bitmap, bounds.Width, bounds.Height);
            SaveBmp(path, bounds.Width, bounds.Height, pixels);
            return new CaptureResult(true, method, path);
        }
        finally
        {
            Native.SelectObject(memoryDc, old);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
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

public sealed class WindowsDoctorCheckService : IDoctorCheckService
{
    public IReadOnlyList<DoctorCheck> RunChecks(PbwConfig config)
    {
        var checks = new List<DoctorCheck>
        {
            new("os", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ok" : "warning", RuntimeInformation.OSDescription),
            new("uia", "ok", "UI Automation tree reading and common patterns are available."),
            new("capture", "ok", GraphicsCaptureSession.IsSupported()
                ? "Windows.Graphics.Capture window capture is available; PrintWindow and BitBlt fallbacks are available."
                : "Windows.Graphics.Capture is unavailable; PrintWindow and BitBlt fallbacks are available."),
            new("ocr", OcrEngine.TryCreateFromUserProfileLanguages() is null ? "warning" : "ok", OcrEngine.TryCreateFromUserProfileLanguages() is null ? "Windows OCR engine is unavailable for the current user languages." : "Windows OCR engine is available."),
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
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
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
    [DllImport("d3d11.dll", SetLastError = true)] internal static extern int D3D11CreateDevice(
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
