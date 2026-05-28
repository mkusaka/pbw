using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pbw.Cli;
using Pbw.Core;
using Pbw.Mcp;
using Pbw.Windows;
using Windows.Graphics.Capture;
using Windows.Media.Ocr;

namespace Pbw.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ElementMatcher_Finds_By_Text_And_Coordinates()
    {
        var elements = new[]
        {
            new ElementSnapshot("root", "Root", "window", new Bounds(0, 0, 500, 500), Children: new[]
            {
                new ElementSnapshot("button-1", "Save now", "button", new Bounds(10, 20, 100, 30), Patterns: new[] { "Invoke" })
            })
        };
        var matcher = new ElementMatcher();

        Assert.Equal("button-1", matcher.Find(elements, new TargetSpec(Text: "save"))?.Id);
        Assert.Equal("button-1", matcher.Find(elements, new TargetSpec(X: 25, Y: 25))?.Id);
    }

    [Fact]
    public async Task TargetResolver_Uses_Snapshot_Source()
    {
        var resolver = new TargetResolver(new FakeSnapshotSource());
        var element = await resolver.ResolveAsync(new TargetSpec(Id: "target"));
        Assert.NotNull(element);
        Assert.Equal("target", element!.Id);
    }

    [Fact]
    public async Task ActionRouter_Uses_Invoke_When_Available()
    {
        var automation = new FakeAutomation();
        var router = new ActionRouter(new FakeInput(), automation, new FakeSnapshotSource());

        var result = await router.ClickAsync(new TargetSpec(Id: "target"), InputDispatchMode.Background);

        Assert.True(result.Performed);
        Assert.Equal("UIAutomation", result.Method);
        Assert.Equal("invoke", automation.LastAction);
        Assert.NotNull(result.Details);
        Assert.Equal("background", result.Details!["dispatch"]);
        Assert.Equal("semantic", result.Details["actualDispatch"]);
        Assert.True((bool)result.Details["semantic"]!);
    }

    [Fact]
    public async Task ActionRouter_Passes_Dispatch_To_Input_Fallback()
    {
        var input = new FakeInput();
        var router = new ActionRouter(input, new FakeAutomation(), new FakeSnapshotSource());

        var result = await router.ClickAsync(new TargetSpec(X: 11, Y: 22, WindowHandle: 1234), InputDispatchMode.Foreground);

        Assert.True(result.Performed);
        Assert.Equal(InputDispatchMode.Foreground, input.LastDispatch);
        Assert.Equal(1234, input.LastWindowHandle);
    }

    [Theory]
    [InlineData(null, InputDispatchMode.Auto)]
    [InlineData("", InputDispatchMode.Auto)]
    [InlineData("auto", InputDispatchMode.Auto)]
    [InlineData("background", InputDispatchMode.Background)]
    [InlineData("foreground", InputDispatchMode.Foreground)]
    public void InputDispatchPolicy_Parses_And_Defaults(string? value, InputDispatchMode expected)
    {
        Assert.Equal(expected, InputDispatchPolicy.Parse(value));
    }

    [Fact]
    public void InputDispatchPolicy_Rejects_Unknown_Mode()
    {
        var ex = Assert.Throws<ArgumentException>(() => InputDispatchPolicy.Parse("silent"));
        Assert.Contains("Unsupported dispatch mode", ex.Message);
    }

    [Fact]
    public void CoordinateConverter_RoundTrips_With_Scale()
    {
        var converter = new CoordinateConverter();
        var display = new DisplayContext("primary", new Bounds(100, 50, 800, 600), 1.5);

        var physical = converter.ToPhysical(200, 150, display);
        var logical = converter.ToLogical(physical.PhysicalX, physical.PhysicalY, display);

        Assert.Equal((200, 150), logical);
    }

    [Fact]
    public void ConfigLoader_Validates_Defaults_And_Rejects_Remote_Listener()
    {
        var loader = new ConfigLoader(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json"));
        var defaults = PbwConfig.Defaults(Path.GetTempPath());
        Assert.True(loader.Validate(defaults).Valid);

        var invalid = defaults with { Mcp = defaults.Mcp with { RemoteListenerEnabled = true } };
        var result = loader.Validate(invalid);
        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("remoteListenerEnabled"));
    }

    [Fact]
    public void ErrorMapper_Maps_Common_Exceptions_To_Structured_Codes()
    {
        Assert.Equal("invalid_argument", ErrorMapper.Map(new ArgumentException("bad")).Code);
        Assert.Equal("access_denied", ErrorMapper.Map(new UnauthorizedAccessException("no")).Code);
    }

    [Fact]
    public void SnapshotStore_Saves_Lists_Shows_Inspects_And_Cleans()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pbw-tests", Guid.NewGuid().ToString("N"));
        var store = new SnapshotStore(dir);
        var snapshot = Snapshot.Empty("s1") with
        {
            Elements = new[] { new ElementSnapshot("e1", "OK", "button", new Bounds(1, 2, 3, 4)) }
        };

        store.Save(snapshot);

        Assert.Single(store.List());
        Assert.Equal("s1", store.Show("s1").Id);
        Assert.Equal("e1", store.Inspect("s1", new TargetSpec(Text: "OK")).Element?.Id);
        Assert.Equal(1, store.Clean(0, 0).Deleted);
    }

    [Fact]
    public void Json_Output_Contains_SchemaVersion_And_Stable_CamelCase()
    {
        var json = JsonSerializer.Serialize(PbwEnvelope<object>.Success(Snapshot.Empty("schema")), PbwSchema.Json);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(PbwSchema.Version, doc.RootElement.GetProperty("schemaVersion").GetString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("schema", doc.RootElement.GetProperty("data").GetProperty("id").GetString());
    }

    [Fact]
    public void SnapshotRedactor_Redacts_Configured_Text_In_Elements_And_Ocr()
    {
        var snapshot = Snapshot.Empty("redact") with
        {
            Elements = new[] { new ElementSnapshot("e1", "password: secret", "edit", new Bounds(0, 0, 1, 1)) },
            OcrText = new[] { new OcrTextSnapshot("token value", new Bounds(0, 0, 1, 1)) }
        };

        var redacted = new SnapshotRedactor(new RedactionConfig(true, new[] { "password", "token" })).Redact(snapshot);

        Assert.Equal("[redacted]", redacted.Elements[0].Name);
        Assert.Equal("[redacted]", redacted.OcrText[0].Text);
    }
}

public sealed class CaptureDiagnosticsTests
{
    [Fact]
    public void BmpCaptureDiagnostics_Detects_All_Black_Bmp()
    {
        var path = Path.Combine(Path.GetTempPath(), "pbw-black-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            WriteBmp(path, 16, 16, (_, _) => ((byte)0, (byte)0, (byte)0));

            var quality = BmpCaptureDiagnostics.Analyze(path);

            Assert.True(quality.IsBmp);
            Assert.True(quality.IsMostlyBlack);
            Assert.Equal(CaptureQualityStatus.Degraded, quality.Status);
            Assert.Equal(256, quality.BlackPixels);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void BmpCaptureDiagnostics_Detects_Mostly_Black_Bmp()
    {
        var path = Path.Combine(Path.GetTempPath(), "pbw-mostly-black-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            WriteBmp(path, 100, 100, (x, y) => x < 2 && y < 10 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));

            var quality = BmpCaptureDiagnostics.Analyze(path);

            Assert.True(quality.IsMostlyBlack);
            Assert.Equal(CaptureQualityStatus.Degraded, quality.Status);
            Assert.True(quality.BlackRatio >= 0.995d);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void BmpCaptureDiagnostics_Does_Not_Mark_Dark_Nonblack_Bmp_As_Black()
    {
        var path = Path.Combine(Path.GetTempPath(), "pbw-dark-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            WriteBmp(path, 24, 24, (_, _) => ((byte)1, (byte)1, (byte)1));

            var quality = BmpCaptureDiagnostics.Analyze(path);

            Assert.False(quality.IsMostlyBlack);
            Assert.Equal(CaptureQualityStatus.Ok, quality.Status);
            Assert.Equal(0, quality.BlackPixels);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void BmpCaptureDiagnostics_Rejects_Implausibly_Large_Malformed_Bmp_Quickly()
    {
        var path = Path.Combine(Path.GetTempPath(), "pbw-malformed-large-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            WriteBmpHeaderOnly(path, int.MaxValue, int.MaxValue);

            BmpQualityInfo? quality = null;
            var stopwatch = Stopwatch.StartNew();
            var exception = Record.Exception(() => quality = BmpCaptureDiagnostics.Analyze(path));
            stopwatch.Stop();

            Assert.Null(exception);
            Assert.NotNull(quality);
            Assert.Equal(CaptureQualityStatus.Unavailable, quality!.Status);
            Assert.Contains("too large", quality.Message);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Analyze took {stopwatch.Elapsed}.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WindowsSnapshotSource_Propagates_Capture_Metadata_Details()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbw-tests", Guid.NewGuid().ToString("N"));
        var attempts = new[]
        {
            new CaptureAttempt("Windows.Graphics.Capture", CaptureQualityStatus.Unavailable, "unsupported"),
            new CaptureAttempt("BitBlt.desktop", CaptureQualityStatus.Degraded, "mostly black")
        };
        var capture = new MetadataCaptureService(new CaptureResult(
            true,
            "BitBlt.desktop",
            Path.Combine(root, "capture.bmp"),
            "fallback used",
            CaptureQualityStatus.Degraded,
            new Dictionary<string, object?>
            {
                ["attempts"] = attempts,
                ["qualityStatus"] = CaptureQualityStatus.Degraded,
                ["occluded"] = true,
                ["occlusionCheck"] = "windowFromPoint"
            }));
        var source = new WindowsSnapshotSource(new FakeWindowService(), new FakeAutomation(), capture, new EmptyOcrService(), root);

        var snapshot = await source.CaptureAsync(CancellationToken.None);

        Assert.NotNull(snapshot.Metadata);
        Assert.Equal("BitBlt.desktop", snapshot.Metadata!["captureMethod"]);
        Assert.Equal(CaptureQualityStatus.Degraded, snapshot.Metadata["captureStatus"]);
        Assert.Equal("fallback used", snapshot.Metadata["captureMessage"]);
        var details = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snapshot.Metadata["captureDetails"]);
        Assert.Same(attempts, details["attempts"]);
        Assert.Equal(CaptureQualityStatus.Degraded, details["qualityStatus"]);
        Assert.True((bool)details["occluded"]!);
        Assert.Equal("windowFromPoint", details["occlusionCheck"]);
    }

    [Fact]
    public void WindowsCaptureService_DesktopCropOcclusionDetails_Report_Unavailable_When_Target_Cannot_Be_Sampled()
    {
        var details = WindowsCaptureService.BuildDesktopCropOcclusionDetails(IntPtr.Zero, new Bounds(0, 0, 100, 100));

        Assert.Null(details["occluded"]);
        Assert.Equal("unavailable", details["occlusionCheck"]);
        Assert.Contains("HWND", (string)details["occlusionMessage"]!);
    }

    [Fact]
    public void WindowsCaptureService_Invalid_Window_Bounds_Result_Includes_QualityStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "pbw-invalid-window-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            var result = new WindowsCaptureService().CaptureWindow(0, path, Array.Empty<ElementSnapshot>());

            Assert.False(result.Success);
            Assert.Equal("window.bounds", result.Method);
            Assert.Equal(CaptureQualityStatus.Unavailable, result.Status);
            Assert.Null(result.ImagePath);
            Assert.NotNull(result.Details);
            Assert.Equal(CaptureQualityStatus.Unavailable, result.Details!["qualityStatus"]);
            var attempts = Assert.IsAssignableFrom<IReadOnlyList<CaptureAttempt>>(result.Details["attempts"]);
            Assert.Empty(attempts);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteBmp(string path, int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var pixelSize = width * height * 4;
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(14 + 40 + pixelSize);
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
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                writer.Write(b);
                writer.Write(g);
                writer.Write(r);
                writer.Write((byte)255);
            }
        }
    }

    private static void WriteBmpHeaderOnly(string path, int width, int signedHeight)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(signedHeight);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(0);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
    }
}

public sealed class WindowsInputDispatchTests
{
    [Fact]
    public void WindowsInputService_Background_Click_Posts_Messages_Without_Foreground()
    {
        var backend = new FakeWin32InputBackend
        {
            Foreground = new IntPtr(0x10),
            WindowAtPoint = new IntPtr(0x100),
            RootWindow = new IntPtr(0x100),
            ChildWindow = new IntPtr(0x200),
            ClassName = "Button"
        };
        var service = new WindowsInputService(backend);

        var result = service.Click(50, 60, dispatch: InputDispatchMode.Background);

        Assert.True(result.Performed);
        Assert.Equal("Win32.PostMessage", result.Method);
        Assert.Empty(backend.SetForegroundCalls);
        Assert.Empty(backend.MouseEvents);
        Assert.True(backend.Posts.Count >= 3);
        Assert.NotNull(result.Details);
        Assert.Equal("background", result.Details!["dispatch"]);
        Assert.Equal("background", result.Details["actualDispatch"]);
        Assert.Equal("mouse_click", result.Details["eventKind"]);
        Assert.False((bool)result.Details["foregroundChanged"]!);
    }

    [Fact]
    public void WindowsInputService_Background_Click_Returns_Structured_BackgroundUnavailable_For_Known_Drop()
    {
        var backend = new FakeWin32InputBackend
        {
            WindowAtPoint = new IntPtr(0x100),
            RootWindow = new IntPtr(0x100),
            ClassName = "Chrome_WidgetWin_1"
        };
        var service = new WindowsInputService(backend);

        var ex = Assert.Throws<PbwException>(() => service.Click(10, 20, dispatch: InputDispatchMode.Background));

        Assert.Equal("background_unavailable", ex.Error.Code);
        Assert.NotNull(ex.Error.Details);
        Assert.Equal("Chrome_WidgetWin_1", ex.Error.Details!["targetClass"]);
        Assert.Equal("mouse_click", ex.Error.Details["eventKind"]);
        Assert.Empty(backend.Posts);
        Assert.Empty(backend.MouseEvents);
    }

    [Fact]
    public void WindowsInputService_Auto_Falls_Back_To_Foreground_With_Details_When_Background_Drops()
    {
        var backend = new FakeWin32InputBackend
        {
            Foreground = new IntPtr(0x10),
            WindowAtPoint = new IntPtr(0x100),
            RootWindow = new IntPtr(0x100),
            ClassName = "Chrome_WidgetWin_1"
        };
        var service = new WindowsInputService(backend);

        var result = service.Click(10, 20);

        Assert.True(result.Performed);
        Assert.Equal("Win32Input.foreground", result.Method);
        Assert.NotNull(result.Details);
        Assert.Equal("auto", result.Details!["dispatch"]);
        Assert.Equal("foreground", result.Details["actualDispatch"]);
        Assert.True(result.Details.ContainsKey("backgroundFallback"));
        Assert.Contains(new IntPtr(0x100), backend.SetForegroundCalls);
        Assert.Contains(new IntPtr(0x10), backend.SetForegroundCalls);
        Assert.NotEmpty(backend.MouseEvents);
    }

    [Fact]
    public void WindowsInputService_Background_Type_Posts_WmChar_To_Hwnd()
    {
        var backend = new FakeWin32InputBackend
        {
            RootWindow = new IntPtr(0x100),
            ClassName = "Edit"
        };
        var service = new WindowsInputService(backend);

        var result = service.TypeText("ab", InputDispatchMode.Background, 0x100);

        Assert.True(result.Performed);
        Assert.Equal("Win32.PostMessage", result.Method);
        Assert.Equal(2, backend.Posts.Count(p => p.Message == 0x0102));
        Assert.NotNull(result.Details);
        Assert.Equal("text_input", result.Details!["eventKind"]);
    }

    [Fact]
    public void WindowsInputService_Background_Type_Without_Hwnd_Does_Not_Send_Foreground_Input()
    {
        var backend = new FakeWin32InputBackend();
        var service = new WindowsInputService(backend);

        var ex = Assert.Throws<PbwException>(() => service.TypeText("hi", InputDispatchMode.Background));

        Assert.Equal("background_unavailable", ex.Error.Code);
        Assert.Empty(backend.Posts);
        Assert.Empty(backend.KeyEvents);
        Assert.Equal("background", ex.Error.Details!["dispatch"]);
    }
}

public sealed class WindowsUiaRobustnessTests
{
    [Fact]
    public void WindowsElementAutomationService_PatternNames_Includes_RangeValue()
    {
        var patterns = WindowsElementAutomationService.PatternNames(pattern =>
            pattern == RangeValuePattern.Pattern ||
            pattern == ValuePattern.Pattern);

        Assert.Contains("RangeValue", patterns);
        Assert.Contains("Value", patterns);
    }

    [Fact]
    public void WindowsElementAutomationService_ReadTree_Timeout_Returns_Degraded_Element()
    {
        var service = new WindowsElementAutomationService(
            TimeSpan.FromMilliseconds(10),
            () =>
            {
                Thread.Sleep(250);
                return Array.Empty<ElementSnapshot>();
            });

        var element = Assert.Single(service.ReadTree());

        Assert.Equal("uia-timeout", element.Id);
        Assert.Equal("degraded", element.Role);
        Assert.NotNull(element.Metadata);
        Assert.True((bool)element.Metadata!["degraded"]!);
        Assert.Equal("timeout", element.Metadata["degradationReason"]);
    }

    [Fact]
    public void WindowsElementAutomationService_ReadTree_Exception_Returns_Degraded_Element()
    {
        var service = new WindowsElementAutomationService(
            TimeSpan.FromSeconds(1),
            () => throw new InvalidOperationException("provider failed"));

        var element = Assert.Single(service.ReadTree());

        Assert.Equal("uia-error", element.Id);
        Assert.Equal("degraded", element.Role);
        Assert.NotNull(element.Metadata);
        Assert.Equal("exception", element.Metadata!["degradationReason"]);
        var details = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(element.Metadata["details"]);
        Assert.Equal(nameof(InvalidOperationException), details["exceptionType"]);
    }
}

public sealed class CliTests
{
    [Fact]
    public async Task Cli_Help_Returns_Structured_Json()
    {
        var cli = TestCli();
        var result = await cli.ExecuteAsync(new[] { "--help" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(PbwSchema.Version, doc.RootElement.GetProperty("schemaVersion").GetString());
    }

    [Fact]
    public async Task Cli_Doctor_Returns_Checks()
    {
        var cli = TestCli();
        var result = await cli.ExecuteAsync(new[] { "doctor" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.True(doc.RootElement.GetProperty("data").GetProperty("checks").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Cli_Unknown_Command_Returns_Structured_Error()
    {
        var cli = TestCli();
        var result = await cli.ExecuteAsync(new[] { "missing" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_argument", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cli_Click_Help_Returns_Command_Usage_Without_Input()
    {
        var input = new FakeInput();
        var cli = TestCli(input);
        var result = await cli.ExecuteAsync(new[] { "click", "--help" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("click", doc.RootElement.GetProperty("data").GetProperty("command").GetString());
        Assert.Contains("--dispatch", doc.RootElement.GetProperty("data").GetProperty("usage").GetString());
        Assert.Null(input.LastDispatch);
    }

    [Fact]
    public async Task Cli_Type_Text_Help_Executes_Input_Command()
    {
        var input = new FakeInput();
        var cli = TestCli(input);
        var result = await cli.ExecuteAsync(new[] { "type", "--text", "help" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        Assert.False(data.TryGetProperty("command", out _));
        Assert.Equal("type", data.GetProperty("action").GetString());
        Assert.Equal("fake", data.GetProperty("method").GetString());
        Assert.Equal(4, data.GetProperty("details").GetProperty("length").GetInt32());
        Assert.Equal(InputDispatchMode.Auto, input.LastDispatch);
    }

    [Fact]
    public async Task Cli_Hotkey_Keys_Help_Executes_Input_Command()
    {
        var input = new FakeInput();
        var cli = TestCli(input);
        var result = await cli.ExecuteAsync(new[] { "hotkey", "--keys", "help" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        Assert.False(data.TryGetProperty("command", out _));
        Assert.Equal("hotkey", data.GetProperty("action").GetString());
        Assert.Equal("help", data.GetProperty("details").GetProperty("keys")[0].GetString());
        Assert.Equal(InputDispatchMode.Auto, input.LastDispatch);
    }

    [Fact]
    public async Task Cli_CommandExecutor_Type_Text_Help_Executes_Input_Command()
    {
        var input = new FakeInput();
        var cli = TestCli(input);
        var envelope = await cli.ExecuteAsync(
            "type",
            new Dictionary<string, object?> { ["text"] = "help" },
            CancellationToken.None);
        var json = JsonSerializer.Serialize(envelope.Data, PbwSchema.Json);
        using var doc = JsonDocument.Parse(json);

        Assert.True(envelope.Ok);
        Assert.False(doc.RootElement.TryGetProperty("command", out _));
        Assert.Equal("type", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("details").GetProperty("length").GetInt32());
        Assert.Equal(InputDispatchMode.Auto, input.LastDispatch);
    }

    [Fact]
    public async Task Cli_Input_Dispatch_Option_Is_Returned_In_Json_Details()
    {
        var cli = TestCli();
        var result = await cli.ExecuteAsync(new[] { "type", "--text", "hi", "--hwnd", "42", "--dispatch", "foreground" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(0, result.ExitCode);
        var details = doc.RootElement.GetProperty("data").GetProperty("details");
        Assert.Equal("foreground", details.GetProperty("dispatch").GetString());
        Assert.Equal(42, details.GetProperty("hwnd").GetInt32());
    }

    [Fact]
    public async Task Cli_Invalid_Dispatch_Returns_Structured_Error()
    {
        var cli = TestCli();
        var result = await cli.ExecuteAsync(new[] { "click", "--x", "1", "--y", "2", "--dispatch", "silent" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(2, result.ExitCode);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_argument", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cli_BackgroundUnavailable_Returns_Structured_Error()
    {
        var cli = TestCli(new FakeInput { ThrowBackgroundUnavailable = true });
        var result = await cli.ExecuteAsync(new[] { "type", "--text", "hi", "--dispatch", "background" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(1, result.ExitCode);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("background_unavailable", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("background", doc.RootElement.GetProperty("error").GetProperty("details").GetProperty("dispatch").GetString());
    }

    [Fact]
    public async Task Cli_Destructive_Command_Requires_Confirmation_By_Default()
    {
        var cli = TestCli();
        var result = await cli.ExecuteAsync(new[] { "window", "close", "--hwnd", "1" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.False(doc.RootElement.GetProperty("data").GetProperty("performed").GetBoolean());
        Assert.Equal("safety-policy", doc.RootElement.GetProperty("data").GetProperty("method").GetString());
    }

    [Theory]
    [InlineData("see")]
    [InlineData("image")]
    [InlineData("click --id target")]
    [InlineData("click --x 1 --y 2")]
    [InlineData("type --text hi")]
    [InlineData("press --key enter")]
    [InlineData("hotkey --keys ctrl+s")]
    [InlineData("scroll --delta 120")]
    [InlineData("drag --from-x 1 --from-y 2 --to-x 3 --to-y 4")]
    [InlineData("move --x 5 --y 6")]
    [InlineData("set-value --id target --value abc")]
    [InlineData("perform-action --id target --action invoke")]
    [InlineData("window list")]
    [InlineData("window focus --hwnd 1")]
    [InlineData("window move --hwnd 1 --x 10 --y 20")]
    [InlineData("window resize --hwnd 1 --width 100 --height 80")]
    [InlineData("window set-bounds --hwnd 1 --x 1 --y 2 --width 3 --height 4")]
    [InlineData("window minimize --hwnd 1")]
    [InlineData("window maximize --hwnd 1")]
    [InlineData("window restore --hwnd 1")]
    [InlineData("window close --hwnd 1")]
    [InlineData("app list")]
    [InlineData("app launch --path fake.exe")]
    [InlineData("app focus --name test")]
    [InlineData("app switch --name test")]
    [InlineData("app quit --name test")]
    [InlineData("menu list")]
    [InlineData("menu click --text File")]
    [InlineData("dialog list")]
    [InlineData("dialog click --button OK")]
    [InlineData("dialog input --value hello")]
    [InlineData("dialog dismiss")]
    [InlineData("clipboard get")]
    [InlineData("clipboard set --text hello")]
    [InlineData("clipboard clear")]
    [InlineData("clipboard paste")]
    [InlineData("snapshot list")]
    [InlineData("snapshot show --id missing")]
    [InlineData("snapshot inspect --id missing --text nope")]
    [InlineData("snapshot clean")]
    [InlineData("config init")]
    [InlineData("config show")]
    [InlineData("config validate")]
    [InlineData("config get --key snapshotDirectory")]
    [InlineData("config set --key maxSnapshots --value 5")]
    [InlineData("doctor")]
    public async Task Cli_All_Commands_Return_Structured_Envelope(string commandLine)
    {
        var cli = TestCli();
        var result = await cli.ExecuteAsync(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(PbwSchema.Version, doc.RootElement.GetProperty("schemaVersion").GetString());
        Assert.True(doc.RootElement.TryGetProperty("ok", out _));
        Assert.True(doc.RootElement.TryGetProperty("data", out _) || doc.RootElement.TryGetProperty("error", out _));
    }

    private static PbwCli TestCli(IInputService? input = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pbw-tests", Guid.NewGuid().ToString("N"));
        var config = PbwConfig.Defaults(root);
        var source = new FakeSnapshotSource();
        var automation = new FakeAutomation();
        input ??= new FakeInput();
        return new PbwCli(
            new ConfigLoader(Path.Combine(root, "config.json")),
            config,
            source,
            new SnapshotStore(config.SnapshotDirectory),
            input,
            new ActionRouter(input, automation, source),
            new FakeWindowService(),
            new FakeAppService(),
            new FakeClipboard(),
            automation,
            new FakeDoctor());
    }
}

public sealed class McpTests
{
    [Fact]
    public void Mcp_Lists_Cli_Aligned_Tools()
    {
        var tools = new McpToolRegistry().ListTools();
        Assert.Contains(tools, t => t.Name == "see");
        Assert.Contains(tools, t => t.Name == "window.list");
        Assert.Contains(tools, t => t.Name == "doctor");
    }

    [Fact]
    public void Mcp_Tools_Expose_Command_Specific_Schemas()
    {
        var tools = new McpToolRegistry().ListTools();
        var type = tools.Single(t => t.Name == "type");
        var click = tools.Single(t => t.Name == "click");
        var windowMove = tools.Single(t => t.Name == "window.move");

        var typeProperties = (IReadOnlyDictionary<string, object?>)type.InputSchema["properties"]!;
        var clickProperties = (IReadOnlyDictionary<string, object?>)click.InputSchema["properties"]!;
        Assert.Contains("text", typeProperties.Keys);
        Assert.Contains("dispatch", typeProperties.Keys);
        Assert.Contains("hwnd", typeProperties.Keys);
        Assert.Contains("dispatch", clickProperties.Keys);
        Assert.False((bool)type.InputSchema["additionalProperties"]!);
        Assert.Contains("hwnd", ((IReadOnlyDictionary<string, object?>)windowMove.InputSchema["properties"]!).Keys);
        Assert.Contains("width", ((IReadOnlyDictionary<string, object?>)tools.Single(t => t.Name == "window.resize").InputSchema["properties"]!).Keys);
    }

    [Fact]
    public async Task Mcp_ToolsList_Returns_JsonRpc_Response()
    {
        var server = new McpServer(new McpToolRegistry(), new FakeExecutor());
        var json = await server.HandleJsonRpcAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Mcp_Tool_Call_Returns_Structured_Result()
    {
        var server = new McpServer(new McpToolRegistry(), new FakeExecutor());
        var json = await server.HandleJsonRpcAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"doctor","arguments":{}}}""");
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains(PbwSchema.Version, text);
    }

    [Fact]
    public async Task Mcp_Tool_Call_Forwards_Dispatch_Field()
    {
        var executor = new FakeExecutor();
        var server = new McpServer(new McpToolRegistry(), executor);

        await server.HandleJsonRpcAsync("""{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"type","arguments":{"text":"hi","dispatch":"background","hwnd":42}}}""");

        Assert.Equal("type", executor.LastCommand);
        Assert.NotNull(executor.LastArguments);
        Assert.Equal("background", executor.LastArguments!["dispatch"]?.ToString());
        var hwnd = Assert.IsType<JsonElement>(executor.LastArguments["hwnd"]);
        Assert.Equal(42, hwnd.GetInt32());
    }

    [Fact]
    public async Task Mcp_Tool_Call_Returns_BackgroundUnavailable_Error()
    {
        var error = new PbwError(
            "background_unavailable",
            "Background dispatch unavailable in fake executor.",
            "type",
            new Dictionary<string, object?> { ["dispatch"] = "background" });
        var server = new McpServer(new McpToolRegistry(), new FakeExecutor(PbwEnvelope<object?>.Failure(error)));

        var json = await server.HandleJsonRpcAsync("""{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"type","arguments":{"text":"hi","dispatch":"background"}}}""");
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("background_unavailable", text);
        Assert.Contains("background", text);
    }

    [Fact]
    public async Task Mcp_Unknown_Tool_Returns_Structured_Tool_Error()
    {
        var server = new McpServer(new McpToolRegistry(), new FakeExecutor());
        var json = await server.HandleJsonRpcAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"shell","arguments":{}}}""");
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("unknown_tool", text);
    }
}

public sealed class WindowsIntegrationTests
{
    [Fact]
    public void Window_List_Is_Safe_On_Windows_Desktop()
    {
        if (!TestEnvironment.HasInteractiveDesktop())
        {
            return;
        }

        var service = new WindowsWindowService();
        var windows = service.ListWindows();
        Assert.NotNull(windows);
    }
}

public sealed class WindowsRealApiIntegrationTests
{
    [Fact]
    public async Task Wpf_TestHost_Exercises_Win32_Capture_And_UIAutomation_Patterns()
    {
        if (!TestEnvironment.HasInteractiveDesktop())
        {
            return;
        }

        var hostExe = FindTestHostExe();
        Assert.True(File.Exists(hostExe), $"Test host was not built: {hostExe}");
        var outputPath = Path.Combine(Path.GetTempPath(), "pbw-testhost-" + Guid.NewGuid().ToString("N") + ".txt");
        var startInfo = new ProcessStartInfo(hostExe)
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(outputPath);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            await WaitForAsync(() =>
            {
                process!.Refresh();
                return process.MainWindowHandle != IntPtr.Zero;
            }, TimeSpan.FromSeconds(10));

            var handle = process!.MainWindowHandle.ToInt32();
            var windowService = new WindowsWindowService();
            var windows = windowService.ListWindows();
            Assert.Contains(windows, w => w.Handle == handle && w.Title.StartsWith("pbw-integration-", StringComparison.Ordinal));

            var backgroundDrag = Assert.Throws<PbwException>(() => new WindowsInputService().Drag(10, 10, 40, 40, InputDispatchMode.Background, handle));
            Assert.Equal("background_unavailable", backgroundDrag.Error.Code);
            Assert.Equal("background", backgroundDrag.Error.Details!["dispatch"]);

            var automation = new WindowsElementAutomationService();
            var set = automation.SetValue(new TargetSpec(AutomationId: "InputBox", WindowHandle: handle), "from-real-uia");
            Assert.True(set.Performed, set.Message);
            Assert.Equal("UIAutomation.ValuePattern", set.Method);

            var invoke = automation.PerformAction(new TargetSpec(AutomationId: "WriteButton", WindowHandle: handle), "invoke");
            Assert.True(invoke.Performed, invoke.Message);
            Assert.Equal("UIAutomation.InvokePattern", invoke.Method);

            await WaitForAsync(() => File.Exists(outputPath) && File.ReadAllText(outputPath) == "from-real-uia", TimeSpan.FromSeconds(15));

            var elements = automation.ReadTree();
            var sliderSnapshot = new ElementMatcher().Find(elements, new TargetSpec(AutomationId: "RangeSlider"));
            Assert.NotNull(sliderSnapshot);
            Assert.Contains("RangeValue", sliderSnapshot!.Patterns ?? Array.Empty<string>());

            var rangeSet = automation.SetValue(new TargetSpec(AutomationId: "RangeSlider", WindowHandle: handle), "73");
            Assert.True(rangeSet.Performed, rangeSet.Message);
            Assert.Equal("UIAutomation.RangeValuePattern", rangeSet.Method);
            Assert.NotNull(rangeSet.Details);
            Assert.Equal(73d, rangeSet.Details!["value"]);

            await WaitForAsync(() => File.Exists(outputPath) && File.ReadAllText(outputPath) == "range:73", TimeSpan.FromSeconds(10));

            var invalidRange = automation.SetValue(new TargetSpec(AutomationId: "RangeSlider", WindowHandle: handle), "not-a-number");
            Assert.False(invalidRange.Performed);
            Assert.Equal("invalid_argument", invalidRange.Details!["errorCode"]);

            var outOfRange = automation.SetValue(new TargetSpec(AutomationId: "RangeSlider", WindowHandle: handle), "1000");
            Assert.False(outOfRange.Performed);
            Assert.Equal("out_of_range", outOfRange.Details!["errorCode"]);

            var capturePath = Path.Combine(Path.GetTempPath(), "pbw-capture-" + Guid.NewGuid().ToString("N") + ".bmp");
            var capture = new WindowsCaptureService().CaptureWindow(handle, capturePath, automation.ReadTree());
            Assert.True(capture.Success, capture.Message);
            Assert.Equal(CaptureQualityStatus.Ok, capture.Status);
            Assert.NotNull(capture.Details);
            Assert.True(capture.Details!.ContainsKey("attempts"));
            Assert.True(capture.Details.ContainsKey("captureBounds"));
            Assert.True(capture.Details.ContainsKey("boundsSource"));
            if (GraphicsCaptureSession.IsSupported())
            {
                Assert.True(capture.Method == "Windows.Graphics.Capture", capture.Message ?? capture.Method);
            }
            Assert.True(File.Exists(capturePath));
            var header = File.ReadAllBytes(capturePath).Take(2).ToArray();
            Assert.Equal(new byte[] { (byte)'B', (byte)'M' }, header);

            var snapshotDir = Path.Combine(Path.GetTempPath(), "pbw-snapshot-" + Guid.NewGuid().ToString("N"));
            var snapshot = await new WindowsSnapshotSource(windowService, automation, new WindowsCaptureService(), new WindowsOcrService(), snapshotDir)
                .CaptureAsync(CancellationToken.None);
            Assert.Equal(PbwSchema.Version, snapshot.SchemaVersion);
            Assert.True(File.Exists(snapshot.ImagePath));
            Assert.NotEmpty(snapshot.Windows);
            Assert.NotEmpty(snapshot.Elements);

            var minimize = windowService.Minimize(handle);
            Assert.True(minimize.Performed, minimize.Message);
            await WaitForAsync(() => windowService.ListWindows().Any(w => w.Handle == handle && w.IsMinimized), TimeSpan.FromSeconds(5));

            var minimizedCapturePath = Path.Combine(Path.GetTempPath(), "pbw-minimized-" + Guid.NewGuid().ToString("N") + ".bmp");
            var minimizedCapture = new WindowsCaptureService().CaptureWindow(handle, minimizedCapturePath, Array.Empty<ElementSnapshot>());
            Assert.False(minimizedCapture.Success);
            Assert.Equal(CaptureQualityStatus.Unavailable, minimizedCapture.Status);
            Assert.Equal("none", minimizedCapture.Method);
            Assert.Null(minimizedCapture.ImagePath);
            Assert.NotNull(minimizedCapture.Details);
            Assert.True((bool)minimizedCapture.Details!["minimized"]!);
            Assert.True((bool)minimizedCapture.Details["noPixels"]!);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void WindowsOcrService_Recognizes_Text_From_Controlled_Bmp()
    {
        if (!TestEnvironment.HasInteractiveDesktop() || OcrEngine.TryCreateFromUserProfileLanguages() is null)
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "pbw-ocr-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            CreateTextBmp(path, "PBW OCR 12345");
            var ocr = new WindowsOcrService().Recognize(path);
            var text = string.Join(" ", ocr.Select(w => w.Text));
            Assert.Contains("PBW", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("12345", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string FindTestHostExe()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "pbw.sln")))
        {
            current = current.Parent;
        }

        var root = current?.FullName ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, "tests", "Pbw.TestHost", "bin", "Release", "net8.0-windows10.0.22621.0", "Pbw.TestHost.exe");
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (condition()) return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException(last is null ? "Condition was not met." : "Condition was not met: " + last.Message);
    }

    private static void CreateTextBmp(string path, string text)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 800, 220));
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial"),
                72,
                Brushes.Black,
                1.0);
            drawing.DrawText(formatted, new Point(32, 56));
        }

        var bitmap = new RenderTargetBitmap(800, 220, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

internal sealed class FakeSnapshotSource : ISnapshotSource
{
    public Task<Snapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var snapshot = Snapshot.Empty("fake") with
        {
            Elements = new[]
            {
                new ElementSnapshot("target", "Target", "button", new Bounds(5, 6, 20, 10), Patterns: new[] { "Invoke" })
            }
        };
        return Task.FromResult(snapshot);
    }
}

internal sealed class FakeInput : IInputService
{
    public InputDispatchMode? LastDispatch { get; private set; }
    public int? LastWindowHandle { get; private set; }
    public bool ThrowBackgroundUnavailable { get; init; }

    public ActionResult Click(int x, int y, string button = "left", InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Result("click", dispatch, windowHandle, new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["button"] = button });

    public ActionResult Move(int x, int y, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Result("move", dispatch, windowHandle, new Dictionary<string, object?> { ["x"] = x, ["y"] = y });

    public ActionResult TypeText(string text, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Result("type", dispatch, windowHandle, new Dictionary<string, object?> { ["length"] = text.Length });

    public ActionResult Press(string key, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Result("press", dispatch, windowHandle, new Dictionary<string, object?> { ["key"] = key });

    public ActionResult Hotkey(IReadOnlyList<string> keys, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Result("hotkey", dispatch, windowHandle, new Dictionary<string, object?> { ["keys"] = keys });

    public ActionResult Scroll(int delta, int? x = null, int? y = null, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Result("scroll", dispatch, windowHandle, new Dictionary<string, object?> { ["delta"] = delta, ["x"] = x, ["y"] = y });

    public ActionResult Drag(int fromX, int fromY, int toX, int toY, InputDispatchMode dispatch = InputDispatchMode.Auto, int? windowHandle = null) =>
        Result("drag", dispatch, windowHandle, new Dictionary<string, object?> { ["fromX"] = fromX, ["fromY"] = fromY, ["toX"] = toX, ["toY"] = toY });

    private ActionResult Result(string action, InputDispatchMode dispatch, int? windowHandle, Dictionary<string, object?> details)
    {
        LastDispatch = dispatch;
        LastWindowHandle = windowHandle;
        details["dispatch"] = InputDispatchPolicy.ToWireString(dispatch);
        details["hwnd"] = windowHandle;
        if (ThrowBackgroundUnavailable)
        {
            throw new PbwException(new PbwError("background_unavailable", "Background dispatch unavailable in fake input.", action, details));
        }

        return new(action, true, "fake", Details: details);
    }
}

internal sealed class FakeWin32InputBackend : IWin32InputBackend
{
    public IntPtr Foreground { get; set; }
    public IntPtr WindowAtPoint { get; set; }
    public IntPtr RootWindow { get; set; }
    public IntPtr ChildWindow { get; set; }
    public string ClassName { get; set; } = "Window";
    public List<IntPtr> SetForegroundCalls { get; } = new();
    public List<(int Flags, int Dx, int Dy, int Data)> MouseEvents { get; } = new();
    public List<(byte VirtualKey, byte ScanCode, int Flags)> KeyEvents { get; } = new();
    public List<PostMessageCall> Posts { get; } = new();

    public IntPtr GetForegroundWindow() => Foreground;

    public bool SetForegroundWindow(IntPtr hwnd)
    {
        SetForegroundCalls.Add(hwnd);
        Foreground = hwnd;
        return true;
    }

    public bool SetCursorPos(int x, int y) => true;

    public void MouseEvent(int flags, int dx, int dy, int data, UIntPtr extraInfo) =>
        MouseEvents.Add((flags, dx, dy, data));

    public void KeybdEvent(byte virtualKey, byte scanCode, int flags, UIntPtr extraInfo) =>
        KeyEvents.Add((virtualKey, scanCode, flags));

    public short VkKeyScan(char character) => (short)char.ToUpperInvariant(character);

    public uint MapVirtualKey(uint code, uint mapType) => code;

    public IntPtr WindowFromPoint(int x, int y) => WindowAtPoint;

    public IntPtr GetRootWindow(IntPtr hwnd) => RootWindow == IntPtr.Zero ? hwnd : RootWindow;

    public bool IsWindow(IntPtr hwnd) => hwnd != IntPtr.Zero;

    public string GetClassName(IntPtr hwnd) => ClassName;

    public bool ScreenToClient(IntPtr hwnd, ref int x, ref int y) => true;

    public IntPtr ChildWindowFromPointEx(IntPtr hwnd, int x, int y, uint flags) =>
        ChildWindow == IntPtr.Zero ? hwnd : ChildWindow;

    public bool IsChild(IntPtr parent, IntPtr child) => true;

    public bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        Posts.Add(new PostMessageCall(hwnd, message, wParam, lParam));
        return true;
    }

    public string LastErrorMessage() => "fake Win32 error";
}

internal sealed record PostMessageCall(IntPtr Hwnd, uint Message, IntPtr WParam, IntPtr LParam);

internal sealed class FakeAutomation : IElementAutomationService
{
    public string? LastAction { get; private set; }
    public IReadOnlyList<ElementSnapshot> ReadTree() => Array.Empty<ElementSnapshot>();
    public ActionResult SetValue(TargetSpec target, string value) => new("set-value", true, "UIAutomation");
    public ActionResult PerformAction(TargetSpec target, string action)
    {
        LastAction = action;
        return new ActionResult(action, true, "UIAutomation");
    }
    public IReadOnlyList<MenuItemInfo> ListMenus(TargetSpec target) => Array.Empty<MenuItemInfo>();
    public ActionResult ClickMenu(TargetSpec target, string menu) => new("menu.click", false, "fake");
    public IReadOnlyList<DialogInfo> ListDialogs() => Array.Empty<DialogInfo>();
    public ActionResult ClickDialog(TargetSpec target, string button) => new("dialog.click", false, "fake");
    public ActionResult InputDialog(TargetSpec target, string value) => new("dialog.input", false, "fake");
    public ActionResult DismissDialog(TargetSpec target) => new("dialog.dismiss", false, "fake");
}

internal sealed class FakeWindowService : IWindowService
{
    public IReadOnlyList<WindowSnapshot> ListWindows() => new[] { new WindowSnapshot(1, "Test", "test", new Bounds(0, 0, 10, 10)) };
    public ActionResult Focus(int handle) => new("window.focus", true, "fake");
    public ActionResult Move(int handle, int x, int y) => new("window.move", true, "fake");
    public ActionResult Resize(int handle, int width, int height) => new("window.resize", true, "fake");
    public ActionResult SetBounds(int handle, Bounds bounds) => new("window.set-bounds", true, "fake");
    public ActionResult Minimize(int handle) => new("window.minimize", true, "fake");
    public ActionResult Maximize(int handle) => new("window.maximize", true, "fake");
    public ActionResult Restore(int handle) => new("window.restore", true, "fake");
    public ActionResult Close(int handle) => new("window.close", true, "fake");
}

internal sealed class FakeAppService : IAppService
{
    public IReadOnlyList<AppInfo> ListApps() => new[] { new AppInfo(10, "test", "Test", 1) };
    public ActionResult Launch(string path, string? arguments) => new("app.launch", true, "fake");
    public ActionResult Focus(string processName) => new("app.focus", true, "fake");
    public ActionResult Switch(string processName) => new("app.switch", true, "fake");
    public ActionResult Quit(string processName) => new("app.quit", true, "fake");
}

internal sealed class FakeClipboard : IClipboardService
{
    private string? value;
    public string? GetText() => value;
    public ActionResult SetText(string text)
    {
        value = text;
        return new ActionResult("clipboard.set", true, "fake");
    }
    public ActionResult Clear()
    {
        value = null;
        return new ActionResult("clipboard.clear", true, "fake");
    }
}

internal sealed class FakeDoctor : IDoctorCheckService
{
    public IReadOnlyList<DoctorCheck> RunChecks(PbwConfig config) => new[] { new DoctorCheck("fake", "ok", "ok") };
}

internal sealed class MetadataCaptureService(CaptureResult result) : IWindowsCaptureService
{
    public CaptureResult CaptureDesktop(string imagePath, IReadOnlyList<WindowSnapshot> windows, IReadOnlyList<ElementSnapshot> elements) => result;
    public CaptureResult CaptureWindow(int handle, string imagePath, IReadOnlyList<ElementSnapshot> elements) => result;
}

internal sealed class EmptyOcrService : IWindowsOcrService
{
    public IReadOnlyList<OcrTextSnapshot> Recognize(string? imagePath) => Array.Empty<OcrTextSnapshot>();
}

internal static class TestEnvironment
{
    public static bool HasInteractiveDesktop() => OperatingSystem.IsWindows() && Environment.UserInteractive;
}

internal sealed class FakeExecutor(PbwEnvelope<object?>? response = null) : IPbwCommandExecutor
{
    public string? LastCommand { get; private set; }
    public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }

    public Task<PbwEnvelope<object?>> ExecuteAsync(string command, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        LastCommand = command;
        LastArguments = arguments;
        return Task.FromResult(response ?? PbwEnvelope<object?>.Success(new { command }));
    }
}
