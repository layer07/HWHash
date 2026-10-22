# HWHash

HWHash collects HWiNFO sensor data in realtime via shared memory and exposes it through Dictionaries, Lists and JSON.

Single file, static class, no dependencies. Under 1ms to read 600+ sensors.

## Installation

```
NuGet\Install-Package HWHash
```

## Quick Start

```csharp
HWHash.SetDelay(500); // optional, default 1000ms
HWHash.Launch();

// ordered like HWiNFO UI
List<HWINFO_HASH> sensors = HWHash.GetOrderedList();

// minified version
List<HWINFO_HASH_MINI> mini = HWHash.GetOrderedListMini();

// curated subset of common sensors
List<HWINFO_HASH> relevant = HWHash.GetRelevantList();

// JSON export (pass true for ordered)
string json = HWHash.GetJsonString(true);
string jsonMini = HWHash.GetJsonStringMini(true);

// stop polling
HWHash.Stop();
await HWHash.StopAsync(); // graceful, awaits last cycle
```

## Structs

### HWINFO_HASH

```csharp
public readonly record struct HWINFO_HASH(
    string ReadingType,
    uint SensorIndex,
    uint SensorID,
    ulong UniqueID,
    string NameDefault,
    string NameCustom,
    string Unit,
    double ValueNow,
    double ValueMin,
    double ValueMax,
    double ValueAvg,
    double ValuePrev,
    string ParentNameDefault,
    string ParentNameCustom,
    uint ParentID,
    uint ParentInstance,
    ulong ParentUniqueID,
    int IndexOrder
);
```

### HWINFO_HASH_MINI

```csharp
public readonly record struct HWINFO_HASH_MINI(
    ulong UniqueID,
    string NameCustom,
    string Unit,
    double ValuePrev,
    double ValueNow,
    [property: JsonIgnore] int IndexOrder,
    [property: JsonIgnore] string ReadingType
);
```

### HWHashStats

```csharp
public readonly record struct HWHashStats(
    double CollectionTime,
    long CollectionTimeTicks,
    uint TotalCategories,
    uint TotalEntries
);
```

## Performance

```csharp
HWHashStats stats = HWHash.GetHWHashStats();
// stats.CollectionTime -> milliseconds per full read cycle
```

On modern hardware, collection stays under 1ms even with 600+ sensors. HWiNFO flushes new data every 50ms minimum, so overhead is negligible.

## Relevant Sensors

`GetRelevantList()` returns a curated subset stored in a `FrozenSet<string>` for O(1) lookups:

Physical Memory Load, Physical Memory Used, P-core 0 VID, P-core 0 Clock, Ring/LLC Clock, Total CPU Usage, CPU Package, Core Max, CPU Package Power, Vcore, +12V, SPD Hub Temperature, GPU Temperature, GPU Memory Junction Temperature, GPU 8-pin #1/#2/#3 Input Voltage, GPU Power (Total), GPU Core Load, GPU Memory Controller Load, Current DL rate, Current UP rate, Total Errors

## Requirements

- .NET 9.0+
- HWiNFO running with Shared Memory enabled
- Windows

## Changelog

### 1.6.0

Same API. Nothing removed or renamed.

- Read cycle ~47x faster (295µs -> 6µs, 649 sensors), zero alloc when values don't change
- Worst case, all sensors changed: 5.3x faster, half the allocations
- `GetOrderedList()` 2.7x, `GetRelevantList()` 25x, JSON up to 1.4x faster
- Reconnects on its own when HWiNFO restarts
- Picks up sensors added/removed in HWiNFO
- Fix: `Launch()` before HWiNFO started could never succeed later
- Fix: a second `Launch()` spawned a duplicate poller
- Fix: torn reads on `GetHWHashStats()`
- New: `Updated` event, `IsConnected`, `LastUpdate`
- JSON via source generators, same output byte for byte

### 1.5.0

- ArrayPool, FrozenSet, readonly record structs, branchless UID, dropped Regex and Parallel.For

## License

[GLWTPL](https://github.com/layer07/HWHash/blob/main/LICENSE)

## Contact

Questions, bugs or ideas? Feel free to reach out.

**Magnus Leatti** · [magnusleatti@kernelriot.com](mailto:magnusleatti@kernelriot.com)
