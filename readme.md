# HWHash
## _HWHash Collects HWiNFO's sensor information in realtime, via shared memory and writes them directly to a easily accessible Dictionary._
[![N|Solid](https://i.imgur.com/EyqeszJ.png)](https://kernelriot.com)

[![GLWTPL](https://img.shields.io/badge/GLWT-Public_License-red.svg)](https://github.com/me-shaon/GLWTPL)



A tiny, singleton (static) class that reads HWiNFO Shared Memory and packs it to a Dictionary.

- 🦄 Single file static class with no external dependencies.
- 😲 Tiny footprint, no memory leaks and 0.01% CPU Usage.
- 💨Blazing fast, <1millisecond to iterate over 600+ sensors.
- ✨It simply works.

## Features

- Unique ID for each sensor avoid name collision
- Compatible with all HWiNFO versions with Shared Memory Support
- Collects "Parent Sensor" information such as Name, ID and Instance
- Hashes both the Sensor's Original name and the User Defined name
- Exports sensor information in the same order HWiNFO UI
- Exports to a List or JSON string in both Full and Minified versions
**check the minified struct version below.*

Installation
---
Nuget package is available:
```c#
NuGet\Install-Package HWHash
```

Usage
---

It is as simple as:
```c#
HWHash.Launch();
```
---
Options
---

| Option | Default |
| ------ | ------ |
| Delay | ![](https://img.shields.io/static/v1?label=&message=1000ms&color=b0a2f9) |

---
How to configure
---
*Make sure you set the parameters **before** Lauching the HWHash thread.*

Delay:
```c#
//update the Dictionary every 500ms (twice per second)
HWHash.SetDelay(500);
```

Then -> ```Launch()```
```c#
HWHash.SetDelay(500);
HWHash.Launch();
```
---
Stopping
---
```c#
//Fire and forget
HWHash.Stop();

//Graceful shutdown (awaits last poll cycle)
await HWHash.StopAsync();
```
---
Basic Functions
---
```c#
//Returns a List<HWINFO_HASH> in the same order as HWiNFO UI
List<HWINFO_HASH> MyHWHashList = HWHash.GetOrderedList();
//Same as above but in a minified version
List<HWINFO_HASH_MINI> MyHWHashListMini = HWHash.GetOrderedListMini();
```
JSON Functions
---
```c#
//Returns a JSON string containing all sensors information (full/mini)
string _HWHashJson = HWHash.GetJsonString();
string _HWHashJsonMini = HWHash.GetJsonStringMini();
//If set to true, it will return a ordered list*
string _HWHashJsonOrdered = HWHash.GetJsonString(true);
//Same for the minified version
string _HWHashJsonMiniOrdered = HWHash.GetJsonStringMini(true);
```
Default Struct
---
This is the base struct, it contains all HWiNFO sensor data, such as min, max and avg values.
```c#
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
Minified Struct
---
The minified version is more suitable for 'realtime' monitoring, since it is packed in a much smaller package.
```c#
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
Relevant Sensor List
---
If you prefer to avoid manually searching for sensor IDs and wish to access a curated List<HWINFO_HASH> of relevant sensors directly, use this function.

The relevant sensors are stored in a `FrozenSet<string>` for O(1) lookups.
```c#
List<HWINFO_HASH> MyRelevantSensors = HWHash.GetRelevantList();
```

Default relevant sensors:
```
Physical Memory Load, Physical Memory Used, P-core 0 VID, P-core 0 Clock,
Ring/LLC Clock, Total CPU Usage, CPU Package, Core Max, CPU Package Power,
Vcore, +12V, SPD Hub Temperature, GPU Temperature,
GPU Memory Junction Temperature, GPU 8-pin #1/2/3 Input Voltage,
GPU Power (Total), GPU Core Load, GPU Memory Controller Load,
Current DL rate, Current UP rate, Total Errors
```

PowerShell Integration
---
In case you want to invoke **HWHash** from **PowerShell**, it is possible to do so, follow the steps below:

 - Ensure you have **PowerShell 7.0 or newer** [\[Here\]](https://github.com/PowerShell/PowerShell/releases/download/v7.4.0/PowerShell-7.4.0-win-x64.msi)
 - Download the latest release of **HWHash** DLL [\[Here\]](https://github.com/layer07/HWHash/releases/download/release/HWHash.dll)
 - Create a test script with the code below

```powershell
#Don't forget to change the line below
$Path = "A:\GITHUB\HWHash\bin\Debug\net9.0\HWHash.dll"
$ClassName = "HWHash"
$MethodLaunch = "Launch"
$MethodJsonStringMini = "GetJsonStringMini"

Add-Type -Path $Path

$Type = [System.Reflection.Assembly]::LoadFrom($Path).GetTypes() | Where-Object { $_.Name -eq $ClassName }

if ($Type -ne $null) {
    $Instance = [Activator]::CreateInstance($Type)
    $Type.GetMethod($MethodLaunch).Invoke($Instance, $null)

    function Get-JsonStringMini {
        param (
            [bool]$Order = $false
        )

        $result = $Type.GetMethod($MethodJsonStringMini).Invoke($Instance, @($Order))
        Write-Host $result
    }

    Get-JsonStringMini -Order $true
} else {
    Write-Host "Type '$ClassName' not found in the assembly."
}
```
Result:

<p align="center">
  <img src="https://github.com/layer07/HWHash/blob/main/media/PowerShell.webp">
</p>


Performance
---
You can access HWHash performance metrics by invoking the following method:
```c#
HWHashStats _Stats = HWHash.GetHWHashStats();
```
 HWHashStats *struct*
```c#
public readonly record struct HWHashStats(
    double CollectionTime,
    long CollectionTimeTicks,
    uint TotalCategories,
    uint TotalEntries
);
```
The most critical information we want to inspect is
```c#
...
double ProfilingTime = _Stats.CollectionTime;
...
```
On a decent modern system, even if there are over 600 sensors, profiling times should stay <1 millisecond. Which is not a concern since HWiNFO will flush new data with a minimum delay of 100ms between readings.

[![N|Solid](https://i.imgur.com/NHrArS2.png)]()

*CollectionTime returns the time in milliseconds between each full loop, in the screenshot above, there are 359 distinct sensor readings.*

We know that for Overclockers and Hardware enthusiasts, it is important to have fast, reliable and accurate readings, and a 1 millisecond overhead is well within what is considered a safe margin.

Notes on Sensor Poll Rate
---
This library relies on a third party application, which is HWiNFO, and HWiNFO relies on the exposed sensors from your hardware, such as motherboard sensors, CPU, GPU sensors, etc.

Usually sensor access/read is deadly fast (nanoseconds) and it is never a bottleneck. There are few rare examples, for instance, on my personal system I am currently using Corsair Vengeance memory sticks, and each memory stick has a temperature sensor, out of 359 different readings on my system, the DIMMs are the only ones who take more than nanoseconds to be read, in my case, HWiNFO takes around 6MS to poll the Memory Temperature from all chips.

Since HWiNFO fastest "poll rate" is 50MS, it is not a problem, but it is definitely something that we should keep an eye on when reading from sensors exposed by our hardware.

Usecase
---
CruelMonitor was built using HWHash as its 'data provider.' CruelMonitor uses C# backend data source, it also serves as a WebSockets server to share the content in realtime, messages packed with MessagePack and are delivered with minimal delays.

Performance metrics are drawed directly on the Windows Desktop, 60FPS, <1ms delay and low CPU usage.
<p align="center">
  <img src="https://github.com/layer07/HWHash/blob/main/media/HWHashDemo1.webp">
</p>


To-do
---

### Lacking 👀
- [ ] Smoothing/interpolation for values
- [ ] Add the option to Flush to InfluxDB
- [ ] Option to create triggers/alerts
- [ ] Save presets and sensor preferences
- [ ] Visual interface to select/deselect sensors


### Added  💖
- [x] JSON export with no third party libraries
- [x] Add Min, Max, Average
- [x] Store previous reading value
- [x] PowerShell Integration
- [x] ArrayPool for zero-alloc sensor reads
- [x] FrozenSet for O(1) relevant sensor lookups
- [x] StopAsync for graceful shutdown
- [x] Readonly record structs

### License
This project is licensed under [GLWTPL](./LICENSE)
