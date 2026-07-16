# klammerkrebs

A **Roslyn-native disposal-leak gate** for C#. From German *klammern* — "to cling, to hold on and
not let go": it finds code that grabs an `IDisposable` and never releases it, so OS handles, sockets,
and streams leak for the lifetime of the process.

Sibling to [marmorkrebs](https://github.com/anagnorisis2peripeteia/marmorkrebs) (mutation),
[signalkrebs](https://github.com/anagnorisis2peripeteia/signalkrebs) (concurrency),
[einsiedlerkrebs](https://github.com/anagnorisis2peripeteia/einsiedlerkrebs) (invariants),
[kanarienkrebs](https://github.com/anagnorisis2peripeteia/kanarienkrebs) (runtime validation), and
[raeuberkrebs](https://github.com/anagnorisis2peripeteia/raeuberkrebs) (security). Each krebs is a
per-lane static gate; klammerkrebs is the **C# resource-disposal lane**, and unlike the TypeScript /
Go / Swift krebs it is written directly on the compiler API rather than wrapping a borrowed linter.

## What it finds

Two detectors, both **semantic** — they resolve `IDisposable` through a real Roslyn compilation, so
they never fire on a same-named type that isn't actually disposable:

- **`DK002` field-kept leak** *(the reason this exists)* — an `IDisposable` is `new`-ed and stored in
  a field, or added to a field collection, that the owning type **never disposes anywhere**. This is
  the class the built-in `CA2000` analyzer misses: CA2000 reasons about locals, not "a handle parked
  in a field/collection for the object's whole life."
- **`DK001` local leak** — an `IDisposable` is `new`-ed into a local and then neither disposed,
  returned, nor handed to something that takes ownership. (Overlaps `CA2000`; useful when it's off.)

It is **precision-first**: it deliberately under-reports rather than cry wolf, because its findings are
meant to go in front of maintainers. It understands the idioms that trip up naive checkers —
`field?.Dispose()` (null-conditional), `foreach (var x in _field) x.Dispose()` (element disposal), and
`listener.Stop()` (socket-family release).

## Demo — run against Stryker.NET, found real leaks

Pointed at [stryker-mutator/stryker-net](https://github.com/stryker-mutator/stryker-net)'s
`Stryker.Core` (which has the `CA*` analyzers off), it reported **4 issues, 0 false positives**:

| Finding | Rule | Verdict |
|---|---|---|
| `SseServer._writers` — a `List<StreamWriter>` over live HTTP responses; `CloseSseEndpoint()` flushes but never disposes them | `DK002` | **real leak** |
| `CrossPlatformBrowserOpener.wslPathProcess` — a started `Process` dropped undisposed (handle + stdout pipe) | `DK001` | **real leak** |
| `AzureFileShareBaselineProvider.reader` — undisposed `BinaryReader` over a caller-owned stream | `DK001` | minor |
| `DashboardClient._httpClient` — undisposed `HttpClient` (arguable; meant to be long-lived) | `DK002` | soft |

Raw output: [`example-findings-stryker-net.json`](./example-findings-stryker-net.json).

Dogfooding on real code also improved the tool itself: the first run had 2 false positives
(`ProgressBar` disposed via `_progressBar?.Dispose()`, and a `TcpListener` released via `.Stop()`),
which is exactly how the idiom handling above got added.

## Usage

```bash
dotnet run -- <path-to-.csproj-or-.sln> [--json]
```

Exit code is linter-style: `0` = clean, `1` = findings, `2` = bad invocation.

## Status

Prototype. Detectors are conservative by design; the roadmap is more ownership-transfer patterns
(factory-returned disposables, fields seeded through helper methods), a fixture test-suite, and the
`hunt` skill lane that the other krebs carry.
