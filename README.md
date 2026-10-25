# Example counter (C#) for HomewrightHQ

A small external integration for [HomewrightHQ](https://github.com/magnusdunker/homewrighthq), written in C# and .NET 10. It does the same as the [Rust example](https://github.com/magnusdunker/thehome-example-integration) and is for trying out integrations from GitHub or as a starting point for your own.

Each setup creates a device with:

- a sensor, **Antal** (count), that counts up by one at a fixed interval
- a switch, **Tæller** (counter), that pauses the counting
- a button, **Nulstil** (reset), that sets the number to 0 and fires the event `example_counter_csharp_reset`

It also has the service `example_counter_csharp.set`, which sets a counter to a given value, and an options flow where you can change the interval. The number is stored in the integration's data folder, so it survives a restart.

## Installation

In HomewrightHQ: **Settings → Integrations → From GitHub**. Paste

```
https://github.com/magnusdunker/homewrighthq-example-integration-csharp
```

and press **Install**. Then add **Eksempel-tæller (C#)** under **Add integration**.

## How it works

An external integration is a standalone program. HomewrightHQ starts it and talks to it over JSON-RPC on stdin and stdout. The protocol is described in HomewrightHQ's [docs/custom-integrations.md](https://github.com/magnusdunker/homewrighthq/blob/main/docs/custom-integrations.md).

The repo contains:

- `thehome.json`: domain, name and protocol version
- `sdk/`: the library `HomewrightHQ.Plugin`, which handles the protocol. Inherit from `Integration` and call `PluginHost.Run`.
- `src/Program.cs`: the integration itself
- `.github/workflows/release.yml`: builds a binary for each platform and puts them on a release

The program is published as one self-contained, trimmed file per platform (11-13 MB), so .NET does not need to be installed where HomewrightHQ runs. The SDK uses only `JsonNode`, so trimming gives no warnings.

## Make your own

1. Copy the repo and change `domain` and `name` in `thehome.json`. The domain may only contain lowercase letters, digits and `_`, and must not be the domain of a built-in integration.
2. Change `AssemblyName` in `src/ExampleCounter.csproj`, and `BIN` and `DOMAIN` at the top of `release.yml`.
3. Inherit from `Integration` in `HomewrightHQ.Plugin`. Only `SetupEntryAsync` is required.
4. Push a tag, e.g. `git tag v0.1.0 && git push --tags`. The workflow builds for Windows, Linux (x86_64 and arm64) and macOS and creates the release.

HomewrightHQ always fetches the newest release. The binaries must be named `<domain>-<target>`, e.g. `example_counter_csharp-aarch64-unknown-linux-gnu`.

The Linux binaries are built against glibc. .NET's musl builds are not statically linked and cannot start in HomewrightHQ's Debian-based Docker image.

Build locally with `dotnet build`. The program waits for messages from HomewrightHQ on stdin, so it does nothing when started on its own.

## License

MIT
