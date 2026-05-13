using System.Text.Json;
using Pbw.Core;

namespace Pbw.Mcp;

public sealed class McpToolRegistry
{
    private readonly string[] names =
    {
        "see", "image", "click", "type", "press", "hotkey", "scroll", "drag", "move", "set-value",
        "perform-action", "window.list", "window.focus", "window.move", "window.resize", "window.set-bounds",
        "window.minimize", "window.maximize", "window.restore", "window.close", "app.list", "app.launch",
        "app.focus", "app.switch", "app.quit", "menu.list", "menu.click", "dialog.list", "dialog.click",
        "dialog.input", "dialog.dismiss", "clipboard.get", "clipboard.set", "clipboard.clear", "clipboard.paste",
        "snapshot.list", "snapshot.show", "snapshot.inspect", "snapshot.clean", "config.show", "config.validate",
        "config.get", "config.set", "doctor"
    };

    public IReadOnlyList<McpTool> ListTools() => names.Select(n => new McpTool(n, $"pbw {n.Replace('.', ' ')}", SchemaFor(n))).ToArray();

    private static IReadOnlyDictionary<string, object?> SchemaFor(string name)
    {
        var properties = new Dictionary<string, object?>();
        var required = new List<string>();
        void Add(string key, string type, bool isRequired = false)
        {
            properties[key] = new Dictionary<string, object?> { ["type"] = type };
            if (isRequired) required.Add(key);
        }

        if (name is "click" or "set-value" or "perform-action" or "menu.list" or "dialog.click" or "dialog.input" or "dialog.dismiss" or "snapshot.inspect")
        {
            Add("id", "string");
            Add("text", "string");
            Add("role", "string");
            Add("automation_id", "string");
            Add("x", "integer");
            Add("y", "integer");
            Add("hwnd", "integer");
            Add("index", "integer");
        }

        switch (name)
        {
            case "type": Add("text", "string", true); break;
            case "press": Add("key", "string", true); break;
            case "hotkey": Add("keys", "string", true); break;
            case "scroll": Add("delta", "integer"); Add("x", "integer"); Add("y", "integer"); break;
            case "drag": Add("from_x", "integer", true); Add("from_y", "integer", true); Add("to_x", "integer", true); Add("to_y", "integer", true); break;
            case "move": Add("x", "integer", true); Add("y", "integer", true); break;
            case "set-value": Add("value", "string", true); break;
            case "perform-action": Add("action", "string"); break;
            case "window.focus":
            case "window.minimize":
            case "window.maximize":
            case "window.restore":
            case "window.close":
                Add("hwnd", "integer", true); Add("confirm", "boolean"); break;
            case "window.move": Add("hwnd", "integer", true); Add("x", "integer", true); Add("y", "integer", true); break;
            case "window.resize": Add("hwnd", "integer", true); Add("width", "integer", true); Add("height", "integer", true); break;
            case "window.set-bounds": Add("hwnd", "integer", true); Add("x", "integer", true); Add("y", "integer", true); Add("width", "integer", true); Add("height", "integer", true); break;
            case "app.launch": Add("path", "string", true); Add("args", "string"); break;
            case "app.focus":
            case "app.switch":
            case "app.quit":
                Add("name", "string", true); Add("confirm", "boolean"); break;
            case "menu.click": Add("text", "string", true); break;
            case "dialog.click": Add("button", "string", true); break;
            case "dialog.input": Add("value", "string", true); break;
            case "clipboard.set": Add("text", "string", true); break;
            case "snapshot.show":
            case "snapshot.inspect":
                Add("id", "string", true); break;
            case "config.get":
            case "config.set":
                Add("key", "string", true); if (name == "config.set") Add("value", "string", true); break;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }
}

public sealed record McpTool(string Name, string Description, IReadOnlyDictionary<string, object?> InputSchema);

public interface IPbwCommandExecutor
{
    Task<PbwEnvelope<object?>> ExecuteAsync(string command, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
}

public sealed class McpServer
{
    private readonly McpToolRegistry registry;
    private readonly IPbwCommandExecutor executor;

    public McpServer(McpToolRegistry registry, IPbwCommandExecutor executor)
    {
        this.registry = registry;
        this.executor = executor;
        Registry = registry;
    }

    public McpToolRegistry Registry { get; }

    public Task<string> HandleJsonRpcAsync(string json, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;
        var method = root.GetProperty("method").GetString();
        return method switch
        {
            "initialize" => Task.FromResult(Response(id, new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2024-11-05",
                ["serverInfo"] = new Dictionary<string, object?> { ["name"] = "pbw", ["version"] = "0.1.0" },
                ["capabilities"] = new Dictionary<string, object?> { ["tools"] = new { }, ["resources"] = new { } }
            })),
            "tools/list" => Task.FromResult(Response(id, new Dictionary<string, object?> { ["tools"] = registry.ListTools() })),
            "tools/call" => HandleToolCall(id, root, cancellationToken),
            _ => Task.FromResult(Error(id, -32601, "Method not found"))
        };
    }

    public async Task RunStdioAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        string? line;
        while ((line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            await output.WriteLineAsync(await HandleJsonRpcAsync(line, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task<string> HandleToolCall(JsonElement id, JsonElement root, CancellationToken cancellationToken)
    {
        var parameters = root.GetProperty("params");
        var name = parameters.GetProperty("name").GetString() ?? "";
        var args = parameters.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(argsElement.GetRawText(), PbwSchema.Json) ?? new()
            : new Dictionary<string, object?>();
        if (!registry.ListTools().Any(t => t.Name == name))
        {
            return Response(id, ToolEnvelope(PbwEnvelope<object?>.Failure(new PbwError("unknown_tool", $"Tool '{name}' is not registered.", name))));
        }

        var result = await executor.ExecuteAsync(name, args, cancellationToken).ConfigureAwait(false);
        return Response(id, ToolEnvelope(result));
    }

    private static Dictionary<string, object?> ToolEnvelope(PbwEnvelope<object?> envelope) => new()
    {
        ["content"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = JsonSerializer.Serialize(envelope, PbwSchema.Json) } },
        ["isError"] = !envelope.Ok
    };

    private static string Response(JsonElement id, object result) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<object>(id.GetRawText()),
        ["result"] = result
    }, PbwSchema.Json);

    private static string Error(JsonElement id, int code, string message) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Deserialize<object>(id.GetRawText()),
        ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message }
    }, PbwSchema.Json);
}
