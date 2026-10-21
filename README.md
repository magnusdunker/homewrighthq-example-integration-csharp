# Eksempel-tæller (C#) til HomewrightHQ

En lille ekstern integration til [HomewrightHQ](https://github.com/magnusdunker/homewrighthq), skrevet i C# og .NET 10. Den gør det samme som [Rust-eksemplet](https://github.com/magnusdunker/thehome-example-integration) og er til at afprøve integrationer fra GitHub eller som udgangspunkt for sin egen.

Hver opsætning giver en enhed med:

- en sensor, **Antal**, der tæller én op med et fast interval
- en kontakt, **Tæller**, der sætter tællingen på pause
- en knap, **Nulstil**, der sætter tallet til 0 og sender hændelsen `example_counter_csharp_reset`

Desuden har den tjenesten `example_counter_csharp.set`, der sætter en tæller til en bestemt værdi, og et options-forløb, hvor intervallet kan ændres. Tallet gemmes i integrationens datamappe, så det overlever en genstart.

## Installation

I HomewrightHQ: **Indstillinger → Integrationer → Fra GitHub**. Indsæt

```
https://github.com/magnusdunker/homewrighthq-example-integration-csharp
```

og tryk **Installér**. Tilføj derefter **Eksempel-tæller (C#)** under **Tilføj integration**.

## Sådan virker det

En ekstern integration er et selvstændigt program. HomewrightHQ starter det og taler med det over JSON-RPC på stdin og stdout. Protokollen er beskrevet i HomewrightHQ's [docs/custom-integrations.md](https://github.com/magnusdunker/homewrighthq/blob/main/docs/custom-integrations.md).

Repoet indeholder:

- `thehome.json`: domæne, navn og protokolversion
- `sdk/`: biblioteket `HomewrightHQ.Plugin`, der klarer protokollen. Arv fra `Integration` og kald `PluginHost.Run`.
- `src/Program.cs`: selve integrationen
- `.github/workflows/release.yml`: bygger en binær til hver platform og lægger dem på en release

Programmet udgives som én selvstændig, trimmet fil pr. platform (11-13 MB), så .NET ikke skal være installeret, hvor HomewrightHQ kører. SDK'en bruger kun `JsonNode`, så trimming giver ingen advarsler.

## Lav din egen

1. Kopiér repoet og ret `domain` og `name` i `thehome.json`. Domænet må kun indeholde små bogstaver, tal og `_` og må ikke være en indbygget integrations domæne.
2. Ret `AssemblyName` i `src/ExampleCounter.csproj` og `BIN` og `DOMAIN` øverst i `release.yml`.
3. Arv fra `Integration` i `HomewrightHQ.Plugin`. Kun `SetupEntryAsync` er påkrævet.
4. Push et tag, f.eks. `git tag v0.1.0 && git push --tags`. Workflowet bygger til Windows, Linux (x86_64 og arm64) og macOS og opretter releasen.

HomewrightHQ henter altid den nyeste release. Binærerne skal hedde `<domæne>-<target>`, f.eks. `example_counter_csharp-aarch64-unknown-linux-gnu`.

Linux-binærerne er bygget mod glibc. .NET's musl-builds er ikke statisk linket og kan ikke starte i HomewrightHQ's Debian-baserede Docker-image.

Byg lokalt med `dotnet build`. Programmet venter på beskeder fra HomewrightHQ på stdin, så det gør ingenting, når det startes alene.

## Licens

MIT
