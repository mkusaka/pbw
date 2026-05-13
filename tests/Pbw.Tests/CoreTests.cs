using System.Text.Json;
using Pbw.Cli;
using Pbw.Core;
using Pbw.Mcp;
using Pbw.Windows;
using Xunit;

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

        var result = await router.ClickAsync(new TargetSpec(Id: "target"));

        Assert.True(result.Performed);
        Assert.Equal("UIAutomation", result.Method);
        Assert.Equal("invoke", automation.LastAction);
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
    public async Task Cli_Destructive_Command_Requires_Confirmation_By_Default()
    {
        var cli = TestCli();
        var result = await cli.ExecuteAsync(new[] { "window", "close", "--hwnd", "1" });
        using var doc = JsonDocument.Parse(result.Json);

        Assert.Equal(0, result.ExitCode);
        Assert.False(doc.RootElement.GetProperty("data").GetProperty("performed").GetBoolean());
        Assert.Equal("safety-policy", doc.RootElement.GetProperty("data").GetProperty("method").GetString());
    }

    private static PbwCli TestCli()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbw-tests", Guid.NewGuid().ToString("N"));
        var config = PbwConfig.Defaults(root);
        var source = new FakeSnapshotSource();
        var automation = new FakeAutomation();
        return new PbwCli(
            new ConfigLoader(Path.Combine(root, "config.json")),
            config,
            source,
            new SnapshotStore(config.SnapshotDirectory),
            new FakeInput(),
            new ActionRouter(new FakeInput(), automation, source),
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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = new WindowsWindowService();
        var windows = service.ListWindows();
        Assert.NotNull(windows);
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
    public ActionResult Click(int x, int y, string button = "left") => new("click", true, "fake", Details: new Dictionary<string, object?> { ["x"] = x, ["y"] = y });
    public ActionResult Move(int x, int y) => new("move", true, "fake");
    public ActionResult TypeText(string text) => new("type", true, "fake");
    public ActionResult Press(string key) => new("press", true, "fake");
    public ActionResult Hotkey(IReadOnlyList<string> keys) => new("hotkey", true, "fake");
    public ActionResult Scroll(int delta, int? x = null, int? y = null) => new("scroll", true, "fake");
    public ActionResult Drag(int fromX, int fromY, int toX, int toY) => new("drag", true, "fake");
}

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

internal sealed class FakeExecutor : IPbwCommandExecutor
{
    public Task<PbwEnvelope<object?>> ExecuteAsync(string command, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
        Task.FromResult(PbwEnvelope<object?>.Success(new { command }));
}
