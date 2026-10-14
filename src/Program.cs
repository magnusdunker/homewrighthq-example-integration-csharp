// Eksempel-tæller (C#): en ekstern integration til TheHome.
//
// Hver opsætning får en enhed med
// - en sensor, der tæller én op med et fast interval,
// - en kontakt, der sætter tælleren på pause,
// - en knap, der nulstiller den.
//
// Den viser også et opsætningsforløb med validering, et options-forløb,
// en tjeneste (example_counter_csharp.set), en hændelse og en fil i
// integrationens datamappe, så tallet overlever en genstart.

using System.Text.Json.Nodes;
using TheHome.Plugin;

PluginHost.Run(new ExampleCounter.Example());

namespace ExampleCounter
{
    internal sealed class Counter
    {
        public ulong Count;
        public bool Running = true;
        public string? CountId;
        public string? RunningId;
        public string? ResetId;
        public CancellationTokenSource? Stop;
    }

    internal sealed class Example : Integration
    {
        private const string Domain = "example_counter_csharp";
        private readonly Dictionary<string, Counter> _counters = [];
        private readonly Lock _lock = new();

        private static JsonObject UserSchema(string name, long interval) => new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("name", "interval"),
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string", ["title"] = "Navn", ["default"] = name },
                ["interval"] = new JsonObject
                {
                    ["type"] = "integer", ["title"] = "Interval (sekunder)", ["minimum"] = 1, ["maximum"] = 3600, ["default"] = interval,
                    ["description"] = "Hvor tit tælleren tæller én op",
                },
            },
        };

        private static JsonObject OptionsSchema(long interval) => new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("interval"),
            ["properties"] = new JsonObject
            {
                ["interval"] = new JsonObject { ["type"] = "integer", ["title"] = "Interval (sekunder)", ["minimum"] = 1, ["maximum"] = 3600, ["default"] = interval },
            },
        };

        private static long IntervalOf(Entry entry) => Math.Clamp((long)(entry.GetDouble("interval") ?? 10), 1, 3600);

        private static long? Int(JsonObject? o, string key) =>
            o?[key] is JsonValue v ? (v.TryGetValue(out long l) ? l : v.TryGetValue(out double d) ? (long)d : null) : null;

        /// <summary>example_counter_csharp.set gælder alle opsætninger; målet afgør hvilken.
        /// TheHome knytter tjenesten til den opsætning, der registrerede den.</summary>
        private static Task RegisterSetService(Host host, string entryId) =>
            host.RegisterServiceAsync(entryId, "set", new JsonObject
            {
                ["name"] = "Sæt tæller",
                ["description"] = "Sætter en tæller til en bestemt værdi",
                ["target"] = new JsonObject { ["entity"] = new JsonObject { ["integration"] = Domain, ["domain"] = "sensor" } },
                ["fields"] = new JsonObject
                {
                    ["value"] = new JsonObject
                    {
                        ["name"] = "Værdi", ["required"] = true, ["example"] = 100,
                        ["selector"] = new JsonObject { ["number"] = new JsonObject { ["min"] = 0, ["max"] = 1000000 } },
                    },
                },
            });

        private static string FileOf(Host host, string entryId) => Path.Combine(host.DataDir, $"{entryId}.json");

        private static ulong Load(Host host, string entryId)
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(FileOf(host, entryId)))?["count"]?.GetValue<ulong>() ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void Save(Host host, string entryId, ulong count)
        {
            try
            {
                Directory.CreateDirectory(host.DataDir);
                File.WriteAllText(FileOf(host, entryId), new JsonObject { ["count"] = count }.ToJsonString());
            }
            catch (Exception e)
            {
                Log.Warn($"kunne ikke gemme tælleren: {e.Message}");
            }
        }

        /// <summary>Send tællerens og kontaktens tilstand til TheHome.</summary>
        private async Task Publish(Host host, string entryId)
        {
            ulong count;
            bool running;
            string? countId, runningId;
            lock (_lock)
            {
                if (!_counters.TryGetValue(entryId, out var c)) return;
                (count, running, countId, runningId) = (c.Count, c.Running, c.CountId, c.RunningId);
            }
            if (countId is not null) await host.SetStateAsync(countId, count.ToString());
            if (runningId is not null) await host.SetStateAsync(runningId, running ? "on" : "off");
        }

        private async Task SetCount(Host host, string entryId, ulong value)
        {
            lock (_lock)
            {
                if (!_counters.TryGetValue(entryId, out var c)) throw PluginException.NotReady("opsætningen kører ikke");
                c.Count = value;
            }
            Save(host, entryId, value);
            await Publish(host, entryId);
        }

        private async Task Reset(Host host, string entryId)
        {
            await SetCount(host, entryId, 0);
            string? resetId;
            lock (_lock) resetId = _counters.GetValueOrDefault(entryId)?.ResetId;
            // en knaps tilstand er tidspunktet for sidste tryk
            if (resetId is not null) await host.SetStateAsync(resetId, DateTimeOffset.UtcNow.ToString("o"));
            await host.FireEventAsync($"{Domain}_reset", new JsonObject { ["entry_id"] = entryId });
        }

        public override Task<FlowReply> ConfigFlowAsync(Host host, Flow flow, string step, JsonObject? input)
        {
            if (step != "user") throw PluginException.Invalid($"ukendt trin {step}");
            if (input is null) return Task.FromResult(FlowReply.Form("user", UserSchema("Tæller", 10)));
            var name = (input["name"] is JsonValue v && v.TryGetValue(out string? s) ? s : "").Trim();
            var interval = Int(input, "interval") ?? 10;
            if (name.Length == 0)
            {
                return Task.FromResult(FlowReply.FormWithErrors("user", UserSchema("", interval), new JsonObject { ["name"] = "required" }));
            }
            var reply = FlowReply.Create(name, new JsonObject { ["name"] = name, ["interval"] = interval }).WithUniqueId(name.ToLowerInvariant());
            return Task.FromResult(reply);
        }

        public override Task<FlowReply> OptionsFlowAsync(Host host, Flow flow, string step, JsonObject? input)
        {
            var current = flow.Entry is { } e ? IntervalOf(e) : 10;
            if (input is null) return Task.FromResult(FlowReply.Form("init", OptionsSchema(current)));
            var interval = Int(input, "interval") ?? current;
            return Task.FromResult(FlowReply.Create("", new JsonObject { ["interval"] = interval }));
        }

        public override async Task SetupEntryAsync(Host host, Entry entry)
        {
            var id = entry.EntryId;
            var name = entry.GetString("name") ?? entry.Title;
            var interval = IntervalOf(entry);

            var device = await host.RegisterDeviceAsync(id, new Device
            {
                Identifiers = [(Domain, id)],
                Name = name,
                Manufacturer = "TheHome",
                Model = "Eksempel-tæller (C#)",
                SwVersion = typeof(Example).Assembly.GetName().Version?.ToString(3),
            });
            var countId = await host.AddEntityAsync(id, new Entity("sensor", $"{id}-count")
            {
                Name = "Antal", DeviceId = device, Icon = "mdi:counter", StateClass = "total_increasing",
            });
            var runningId = await host.AddEntityAsync(id, new Entity("switch", $"{id}-running")
            {
                Name = "Tæller", DeviceId = device, Icon = "mdi:play-pause",
            }, handler: true);
            var resetId = await host.AddEntityAsync(id, new Entity("button", $"{id}-reset")
            {
                Name = "Nulstil", DeviceId = device, Icon = "mdi:restart",
            }, handler: true);
            await RegisterSetService(host, id);

            var count = Load(host, id);
            var stop = new CancellationTokenSource();
            lock (_lock)
            {
                _counters[id] = new Counter { Count = count, CountId = countId, RunningId = runningId, ResetId = resetId, Stop = stop };
            }
            if (resetId is not null) await host.SetStateAsync(resetId, "unknown");
            await Publish(host, id);

            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
                try
                {
                    while (await timer.WaitForNextTickAsync(stop.Token))
                    {
                        ulong n;
                        string? sensor;
                        lock (_lock)
                        {
                            if (!_counters.TryGetValue(id, out var c)) return;
                            if (!c.Running) continue;
                            n = ++c.Count;
                            sensor = c.CountId;
                        }
                        Save(host, id, n);
                        if (sensor is null) continue;
                        try
                        {
                            await host.SetStateAsync(sensor, n.ToString());
                        }
                        catch (PluginException e)
                        {
                            Log.Warn($"kunne ikke opdatere {sensor}: {e.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
            Log.Info($"{name} tæller hvert {interval}. sekund fra {count}");
        }

        public override async Task UnloadEntryAsync(Host host, string entryId)
        {
            string? other;
            lock (_lock)
            {
                if (_counters.Remove(entryId, out var c)) c.Stop?.Cancel();
                other = _counters.Keys.FirstOrDefault();
            }
            // TheHome fjerner tjenesten med opsætningen; de andre skal stadig have den
            if (other is not null) await RegisterSetService(host, other);
        }

        public override async Task EntityServiceAsync(Host host, string entryId, string entityId, string service, JsonObject data)
        {
            bool isSwitch, isButton;
            lock (_lock)
            {
                if (!_counters.TryGetValue(entryId, out var c)) throw PluginException.Unavailable(entityId);
                isSwitch = c.RunningId == entityId;
                isButton = c.ResetId == entityId;
            }
            if (isSwitch && service is "turn_on" or "turn_off" or "toggle")
            {
                lock (_lock)
                {
                    if (_counters.TryGetValue(entryId, out var c))
                    {
                        c.Running = service switch { "turn_on" => true, "turn_off" => false, _ => !c.Running };
                    }
                }
                await Publish(host, entryId);
            }
            else if (isButton && service == "press")
            {
                await Reset(host, entryId);
            }
            else
            {
                throw PluginException.NotSupported($"{service} på {entityId}");
            }
        }

        public override async Task<JsonNode?> DomainServiceAsync(Host host, string entryId, string service, JsonObject data, IReadOnlyList<string> entityIds)
        {
            if (service != "set") throw PluginException.NotSupported(service);
            var value = data["value"] is JsonValue v && v.TryGetValue(out double d) ? d : throw PluginException.Invalid("value mangler");
            if (value < 0) throw PluginException.Invalid("value skal være 0 eller mere");
            List<string> targets;
            lock (_lock)
            {
                targets = _counters
                    .Where(kv => (kv.Value.CountId is { } a && entityIds.Contains(a)) || (kv.Value.RunningId is { } b && entityIds.Contains(b)))
                    .Select(kv => kv.Key)
                    .ToList();
            }
            if (targets.Count == 0) throw PluginException.Invalid("vælg en tæller som mål");
            foreach (var target in targets) await SetCount(host, target, (ulong)value);
            return null;
        }

        public override Task<JsonNode?> DiagnosticsAsync(Host host, Entry entry)
        {
            lock (_lock)
            {
                JsonNode result = _counters.TryGetValue(entry.EntryId, out var c)
                    ? new JsonObject { ["count"] = c.Count, ["running"] = c.Running, ["interval"] = IntervalOf(entry) }
                    : new JsonObject { ["loaded"] = false };
                return Task.FromResult<JsonNode?>(result);
            }
        }
    }
}
