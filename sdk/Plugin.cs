// SDK til eksterne TheHome-integrationer.
//
// En ekstern integration er et selvstændigt program. TheHome starter det og
// taler med det over JSON-RPC 2.0 på stdin og stdout, én besked pr. linje.
// stderr er integrationens log. Programmet stopper, når stdin lukkes.
//
// Arv fra Integration og kald PluginHost.Run fra Main:
//
//     class Mine : Integration
//     {
//         public override async Task SetupEntryAsync(Host host, Entry entry)
//         {
//             var id = await host.AddEntityAsync(entry.EntryId, new Entity("sensor", "mine-1"));
//             if (id is not null) await host.SetStateAsync(id, "42");
//         }
//     }
//
//     PluginHost.Run(new Mine());
//
// Alt JSON håndteres med JsonNode, så programmet kan trimmes og AOT-kompileres.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace TheHome.Plugin;

/// <summary>Versionen af protokollen, som denne SDK taler.</summary>
public static class Protocol
{
    public const int ApiVersion = 1;
}

/// <summary>
/// En fejl til TheHome. <see cref="Kind"/> afgør, hvad TheHome gør:
/// <c>not_ready</c> prøver igen senere, <c>auth_failed</c> beder brugeren logge ind
/// igen, <c>invalid_argument</c>, <c>not_supported</c>, <c>unavailable</c> og
/// <c>not_found</c> vises ved tjenestekald, og <c>failed</c> er alt andet.
/// </summary>
public class PluginException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;

    public static PluginException NotReady(string m) => new("not_ready", m);
    public static PluginException AuthFailed(string m) => new("auth_failed", m);
    public static PluginException Failed(string m) => new("failed", m);
    public static PluginException Invalid(string m) => new("invalid_argument", m);
    public static PluginException NotSupported(string m) => new("not_supported", m);
    public static PluginException Unavailable(string m) => new("unavailable", m);
    public static PluginException NotFound(string m) => new("not_found", m);
}

/// <summary>En opsætning af integrationen (en "config entry").</summary>
public sealed class Entry
{
    public string EntryId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Title { get; init; } = "";
    public JsonObject Data { get; init; } = [];
    public JsonObject Options { get; init; } = [];
    public string? UniqueId { get; init; }

    /// <summary>Værdi fra options først, ellers fra data.</summary>
    public JsonNode? Get(string key) => Options[key] ?? Data[key];

    public string? GetString(string key) => Get(key) is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public double? GetDouble(string key) => Get(key) is JsonValue v && v.TryGetValue(out double d) ? d : null;

    public static Entry FromJson(JsonNode? n) => new()
    {
        EntryId = Json.Str(n, "entry_id"),
        Domain = Json.Str(n, "domain"),
        Title = Json.Str(n, "title"),
        Data = n?["data"] as JsonObject is { } d ? (JsonObject)d.DeepClone() : [],
        Options = n?["options"] as JsonObject is { } o ? (JsonObject)o.DeepClone() : [],
        UniqueId = n?["unique_id"]?.GetValue<string>(),
    };
}

/// <summary>Et opsætningsforløb i gang.</summary>
public sealed class Flow
{
    public string FlowId { get; init; } = "";
    /// <summary><c>user</c>, <c>options</c>, <c>reauth</c>, <c>zeroconf</c> ...</summary>
    public string Source { get; init; } = "";
    public JsonNode? Discovery { get; init; }
    /// <summary>Data samlet i tidligere trin.</summary>
    public JsonObject Data { get; init; } = [];
    public string? UniqueId { get; init; }
    /// <summary>Ved options- og reauth-forløb: opsætningen, der ændres.</summary>
    public Entry? Entry { get; init; }

    public static Flow FromJson(JsonNode? n) => new()
    {
        FlowId = Json.Str(n, "flow_id"),
        Source = Json.Str(n, "source"),
        Discovery = n?["discovery"]?.DeepClone(),
        Data = n?["data"] as JsonObject is { } d ? (JsonObject)d.DeepClone() : [],
        UniqueId = n?["unique_id"]?.GetValue<string>(),
        Entry = n?["entry"] is JsonObject e ? Entry.FromJson(e) : null,
    };
}

/// <summary>Svaret på et trin i et opsætningsforløb.</summary>
public sealed class FlowReply
{
    private readonly JsonObject _result;
    private JsonObject? _data;
    private string? _uniqueId;

    private FlowReply(JsonObject result) => _result = result;

    /// <summary>Vis en formular. <paramref name="schema"/> er et JSON Schema-objekt (<c>type: object</c>).</summary>
    public static FlowReply Form(string stepId, JsonObject schema) =>
        new(new JsonObject { ["type"] = "form", ["step_id"] = stepId, ["schema"] = schema });

    /// <summary>Vis formularen igen med fejl, f.eks. <c>{"base": "cannot_connect"}</c>.</summary>
    public static FlowReply FormWithErrors(string stepId, JsonObject schema, JsonObject errors) =>
        new(new JsonObject { ["type"] = "form", ["step_id"] = stepId, ["schema"] = schema, ["errors"] = errors });

    public static FlowReply Menu(string stepId, params string[] options) =>
        new(new JsonObject { ["type"] = "menu", ["step_id"] = stepId, ["options"] = new JsonArray([.. options.Select(o => (JsonNode)o)]) });

    /// <summary>Afslut og opret opsætningen. I et options-forløb bliver <paramref name="data"/> de nye options.</summary>
    public static FlowReply Create(string title, JsonObject data) =>
        new(new JsonObject { ["type"] = "create_entry", ["title"] = title, ["data"] = data });

    public static FlowReply Abort(string reason) => new(new JsonObject { ["type"] = "abort", ["reason"] = reason });

    /// <summary>Gem data til de næste trin.</summary>
    public FlowReply WithData(JsonObject data)
    {
        _data = data;
        return this;
    }

    /// <summary>Sæt et unikt id. TheHome afbryder med <c>already_configured</c>, hvis en anden opsætning har det.</summary>
    public FlowReply WithUniqueId(string id)
    {
        _uniqueId = id;
        return this;
    }

    internal JsonObject ToJson()
    {
        var o = new JsonObject { ["result"] = _result };
        if (_data is not null) o["data"] = _data;
        if (_uniqueId is not null) o["unique_id"] = _uniqueId;
        return o;
    }
}

/// <summary>En enhed. Felterne svarer til TheHomes enhedsregister.</summary>
public sealed class Device
{
    /// <summary>F.eks. <c>("mit_domæne", "serienummer")</c>. Holder enheden stabil.</summary>
    public List<(string Domain, string Id)> Identifiers { get; init; } = [];
    public required string Name { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SwVersion { get; init; }
    public string? SerialNumber { get; init; }
    public string? SuggestedArea { get; init; }
    public string? ConfigurationUrl { get; init; }

    internal JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["identifiers"] = new JsonArray([.. Identifiers.Select(i => (JsonNode)new JsonArray(i.Domain, i.Id))]),
            ["name"] = Name,
        };
        Json.Opt(o, "manufacturer", Manufacturer);
        Json.Opt(o, "model", Model);
        Json.Opt(o, "sw_version", SwVersion);
        Json.Opt(o, "serial_number", SerialNumber);
        Json.Opt(o, "suggested_area", SuggestedArea);
        Json.Opt(o, "configuration_url", ConfigurationUrl);
        return o;
    }
}

/// <summary>En entity. Felterne svarer til TheHomes entity-register.</summary>
public sealed class Entity(string domain, string uniqueId)
{
    /// <summary><c>sensor</c>, <c>switch</c>, <c>light</c>, <c>button</c>, <c>climate</c> ...</summary>
    public string Domain { get; } = domain;
    /// <summary>Stabilt id, der aldrig må ændre sig.</summary>
    public string UniqueId { get; } = uniqueId;
    /// <summary>Navn efter enhedens navn. <c>null</c> betyder enhedens hoved-entity.</summary>
    public string? Name { get; init; }
    public string? DeviceId { get; init; }
    public string? Icon { get; init; }
    public string? DeviceClass { get; init; }
    public string? UnitOfMeasurement { get; init; }
    public string? StateClass { get; init; }
    public string? EntityCategory { get; init; }
    /// <summary>Faste attributter, f.eks. <c>supported_features</c> eller <c>options</c>.</summary>
    public JsonObject? Capabilities { get; init; }

    internal JsonObject ToJson()
    {
        var o = new JsonObject { ["domain"] = Domain, ["unique_id"] = UniqueId };
        Json.Opt(o, "name", Name);
        Json.Opt(o, "device_id", DeviceId);
        Json.Opt(o, "icon", Icon);
        Json.Opt(o, "device_class", DeviceClass);
        Json.Opt(o, "unit_of_measurement", UnitOfMeasurement);
        Json.Opt(o, "state_class", StateClass);
        Json.Opt(o, "entity_category", EntityCategory);
        if (Capabilities is not null) o["capabilities"] = Capabilities.DeepClone();
        return o;
    }
}

/// <summary>Det, en integration implementerer. Kun <see cref="SetupEntryAsync"/> er påkrævet.</summary>
public abstract class Integration
{
    /// <summary>
    /// Opsætningsforløbet. Første kald har <paramref name="step"/> = kilden (<c>user</c> ...) og
    /// <paramref name="input"/> = <c>null</c>. De næste har den sidste formulars <c>step_id</c>.
    /// </summary>
    public virtual Task<FlowReply> ConfigFlowAsync(Host host, Flow flow, string step, JsonObject? input) =>
        Task.FromResult(FlowReply.Abort("not_supported"));

    /// <summary>Indstillinger for en eksisterende opsætning (<c>flow.Entry</c> er sat).</summary>
    public virtual Task<FlowReply> OptionsFlowAsync(Host host, Flow flow, string step, JsonObject? input) =>
        Task.FromResult(FlowReply.Abort("not_supported"));

    /// <summary>Sæt en opsætning op: registrér enheder og entities, start arbejde.</summary>
    public abstract Task SetupEntryAsync(Host host, Entry entry);

    /// <summary>Stop arbejdet for en opsætning. TheHome fjerner selv dens tilstande.</summary>
    public virtual Task UnloadEntryAsync(Host host, string entryId) => Task.CompletedTask;

    /// <summary>Et tjenestekald på en entity tilføjet med <c>handler = true</c>, f.eks. <c>switch.turn_on</c>.</summary>
    public virtual Task EntityServiceAsync(Host host, string entryId, string entityId, string service, JsonObject data) =>
        throw PluginException.NotSupported($"{service} på {entityId}");

    /// <summary>Et kald af en tjeneste registreret med <see cref="Host.RegisterServiceAsync"/>.</summary>
    public virtual Task<JsonNode?> DomainServiceAsync(Host host, string entryId, string service, JsonObject data, IReadOnlyList<string> entityIds) =>
        throw PluginException.NotSupported(service);

    /// <summary>Fejlsøgningsdata. Skjul hemmeligheder.</summary>
    public virtual Task<JsonNode?> DiagnosticsAsync(Host host, Entry entry) => Task.FromResult<JsonNode?>(null);

    /// <summary>Brugeren sletter en enhed.</summary>
    public virtual Task RemoveDeviceAsync(Host host, Entry entry, JsonNode? device) => Task.CompletedTask;
}

/// <summary>Forbindelsen til TheHome.</summary>
public sealed class Host
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly ChannelWriter<string> _out;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;

    internal Host(ChannelWriter<string> output)
    {
        _out = output;
        DataDir = Environment.GetEnvironmentVariable("THEHOME_DATA_DIR") ?? "data";
    }

    /// <summary>Integrationens egen mappe til filer. Den bevares ved opdateringer.</summary>
    public string DataDir { get; internal set; }

    /// <summary>Kald en metode i TheHome og vent på svaret.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonObject parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
        if (!_out.TryWrite(msg.ToJsonString()))
        {
            _pending.TryRemove(id, out _);
            throw PluginException.NotReady("forbindelsen til TheHome er lukket");
        }
        try
        {
            return await tcs.Task.WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw PluginException.Failed($"{method}: TheHome svarede ikke");
        }
    }

    internal void Resolve(JsonNode msg)
    {
        if (msg["id"] is not JsonValue v || !v.TryGetValue(out long id) || !_pending.TryRemove(id, out var tcs)) return;
        if (msg["error"] is JsonObject e)
        {
            var kind = e["data"]?["kind"]?.GetValue<string>() ?? "failed";
            tcs.TrySetException(new PluginException(kind, e["message"]?.GetValue<string>() ?? ""));
        }
        else
        {
            tcs.TrySetResult(msg["result"]?.DeepClone());
        }
    }

    internal void CloseAll()
    {
        foreach (var (_, tcs) in _pending) tcs.TrySetException(PluginException.NotReady("forbindelsen til TheHome er lukket"));
        _pending.Clear();
    }

    internal void Send(JsonObject msg) => _out.TryWrite(msg.ToJsonString());

    /// <summary>Registrér en enhed. Returnerer dens id.</summary>
    public async Task<string> RegisterDeviceAsync(string entryId, Device device)
    {
        var r = await RequestAsync("device/register", new JsonObject { ["entry_id"] = entryId, ["device"] = device.ToJson() });
        return r?["id"]?.GetValue<string>() ?? throw PluginException.Failed("intet enheds-id");
    }

    /// <summary>
    /// Tilføj en entity. Med <paramref name="handler"/> sender TheHome dens tjenestekald til
    /// <see cref="Integration.EntityServiceAsync"/>. Returnerer entity_id, eller <c>null</c>
    /// hvis brugeren har slået den fra.
    /// </summary>
    public async Task<string?> AddEntityAsync(string entryId, Entity entity, bool handler = false)
    {
        var r = await RequestAsync("entity/add", new JsonObject { ["entry_id"] = entryId, ["entity"] = entity.ToJson(), ["handler"] = handler });
        return r?["entity_id"]?.GetValue<string>();
    }

    public Task SetStateAsync(string entityId, string state, JsonObject? attributes = null) =>
        RequestAsync("state/set", new JsonObject { ["entity_id"] = entityId, ["state"] = state, ["attributes"] = attributes ?? [] });

    public Task SetUnavailableAsync(string entityId) => RequestAsync("state/unavailable", new JsonObject { ["entity_id"] = entityId });

    /// <summary>Tilstanden af en vilkårlig entity, eller <c>null</c>.</summary>
    public Task<JsonNode?> GetStateAsync(string entityId) => RequestAsync("state/get", new JsonObject { ["entity_id"] = entityId });

    /// <summary>Registrér tjenesten <c>&lt;domæne&gt;.&lt;service&gt;</c>. Kald ender i <see cref="Integration.DomainServiceAsync"/>.</summary>
    public Task RegisterServiceAsync(string entryId, string service, JsonObject? description = null) =>
        RequestAsync("service/register", new JsonObject { ["entry_id"] = entryId, ["service"] = service, ["description"] = description });

    /// <summary>Kald en tjeneste i TheHome, f.eks. <c>light.turn_on</c>.</summary>
    public Task<JsonNode?> CallServiceAsync(string domain, string service, JsonObject data) =>
        RequestAsync("hub/call_service", new JsonObject { ["domain"] = domain, ["service"] = service, ["data"] = data });

    public Task FireEventAsync(string eventType, JsonObject data) =>
        RequestAsync("event/fire", new JsonObject { ["event_type"] = eventType, ["data"] = data });

    /// <summary>Bed brugeren rette noget. <paramref name="severity"/>: <c>warning</c> eller <c>error</c>.</summary>
    public Task CreateIssueAsync(string entryId, string issueId, string severity, string title, string description) =>
        RequestAsync("issue/create", new JsonObject
        {
            ["entry_id"] = entryId, ["issue_id"] = issueId, ["severity"] = severity, ["title"] = title, ["description"] = description,
        });

    public Task DeleteIssueAsync(string entryId, string issueId) =>
        RequestAsync("issue/delete", new JsonObject { ["entry_id"] = entryId, ["issue_id"] = issueId });

    /// <summary>Gem ny data (f.eks. et fornyet token) på opsætningen.</summary>
    public Task UpdateEntryDataAsync(string entryId, JsonObject data) =>
        RequestAsync("entry/update", new JsonObject { ["entry_id"] = entryId, ["data"] = data });
}

/// <summary>Skriv til TheHomes log via stderr.</summary>
public static class Log
{
    public static void Error(string message) => Write("ERROR", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Info(string message) => Write("INFO", message);
    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message) => Console.Error.WriteLine($"{level} {message}");
}

/// <summary>Løkken, der forbinder en <see cref="Integration"/> med TheHome.</summary>
public static class PluginHost
{
    /// <summary>Kør integrationen, indtil TheHome lukker stdin eller beder om at stoppe.</summary>
    public static void Run(Integration integration) => RunAsync(integration).GetAwaiter().GetResult();

    public static async Task RunAsync(Integration integration)
    {
        var utf8 = new UTF8Encoding(false);
        Console.InputEncoding = utf8;
        Console.OutputEncoding = utf8;
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var host = new Host(channel.Writer);

        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
        var writer = Task.Run(async () =>
        {
            await foreach (var line in channel.Reader.ReadAllAsync())
            {
                await stdout.WriteLineAsync(line);
            }
        });

        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            JsonNode? msg;
            try
            {
                msg = JsonNode.Parse(line);
            }
            catch (Exception)
            {
                Log.Warn($"ugyldig besked fra TheHome: {line}");
                continue;
            }
            if (msg is null) continue;
            var method = msg["method"]?.GetValue<string>();
            if (method is null)
            {
                host.Resolve(msg);
                continue;
            }
            if (method == "shutdown") break;
            var id = msg["id"]?.DeepClone();
            var parameters = msg["params"]?.DeepClone();
            // Hver besked i sin egen opgave, så integrationen kan kalde TheHome, mens TheHome venter.
            _ = Task.Run(async () =>
            {
                JsonObject reply;
                try
                {
                    var result = await Dispatch(integration, host, method, parameters);
                    reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
                }
                catch (Exception ex)
                {
                    var kind = ex is PluginException pe ? pe.Kind : "failed";
                    if (ex is not PluginException) Log.Error($"{method}: {ex}");
                    reply = new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id,
                        ["error"] = new JsonObject { ["code"] = -32000, ["message"] = ex.Message, ["data"] = new JsonObject { ["kind"] = kind } },
                    };
                }
                if (id is not null) host.Send(reply);
            });
        }
        host.CloseAll();
        channel.Writer.TryComplete();
        await writer.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
    }

    private static async Task<JsonNode?> Dispatch(Integration i, Host host, string method, JsonNode? p)
    {
        switch (method)
        {
            case "initialize":
                if (p?["data_dir"]?.GetValue<string>() is { } dir) host.DataDir = dir;
                return new JsonObject { ["api_version"] = Protocol.ApiVersion };
            case "config_flow":
            case "options_flow":
            {
                var flow = Flow.FromJson(p?["flow"]);
                var step = Json.Str(p, "step");
                var input = p?["input"] as JsonObject;
                var reply = method == "config_flow"
                    ? await i.ConfigFlowAsync(host, flow, step, input)
                    : await i.OptionsFlowAsync(host, flow, step, input);
                return reply.ToJson();
            }
            case "setup_entry":
                await i.SetupEntryAsync(host, Entry.FromJson(p?["entry"]));
                return null;
            case "unload_entry":
                await i.UnloadEntryAsync(host, Json.Str(p, "entry_id"));
                return null;
            case "entity/call_service":
                await i.EntityServiceAsync(host, Json.Str(p, "entry_id"), Json.Str(p, "entity_id"), Json.Str(p, "service"), Json.Obj(p, "data"));
                return null;
            case "service/call":
            {
                var ids = (p?["entity_ids"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? [];
                return await i.DomainServiceAsync(host, Json.Str(p, "entry_id"), Json.Str(p, "service"), Json.Obj(p, "data"), ids);
            }
            case "diagnostics":
                return await i.DiagnosticsAsync(host, Entry.FromJson(p?["entry"]));
            case "remove_device":
                await i.RemoveDeviceAsync(host, Entry.FromJson(p?["entry"]), p?["device"]?.DeepClone());
                return null;
            default:
                throw PluginException.NotSupported($"ukendt metode {method}");
        }
    }
}

internal static class Json
{
    public static string Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    public static JsonObject Obj(JsonNode? n, string key) => n?[key] is JsonObject o ? (JsonObject)o.DeepClone() : [];

    public static void Opt(JsonObject o, string key, string? value)
    {
        if (value is not null) o[key] = value;
    }
}
