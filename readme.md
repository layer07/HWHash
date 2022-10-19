# HWHash
## _Grab all HWInfo realtime sensor information directly to a easily accessible Dictionary._
[![N|Solid](https://i.imgur.com/EyqeszJ.png)](https://divinelain.com)

[![GLWTPL](https://img.shields.io/badge/GLWT-Public_License-red.svg)](https://github.com/me-shaon/GLWTPL)



A tiny, singleton (static) class that reads HWInfo Shared Memory and packs it to a Dictionary.

- 🦄 Single file static class with no external dependencies.
- 😲 Tiny footprint, no memory leaks and 0.01% CPU Usage.
- ✨It simply works.

## Features

- Unique ID for each sensor avoid duplicate name collision
- Compatible with all HWInfo versions with Shared Memory Support
- Collects "Parent Sensor" information such as Name, ID and Instance
- Hashes both the Sensor's Original name and the User Defined name
- Exports an ordered list in the same order as HWInfo UI
- Exports to a JSON string

---
Usage
---

It is as simple as:
```c#
HWHash.Launch();
```
---
Options
---
There are three startup options for HWHash.

| Option | Default |
| ------ | ------ |
| HighPrecision | ![](https://img.shields.io/static/v1?label=&message=false&color=ff7da8)  |
| HighPriority | ![](https://img.shields.io/static/v1?label=&message=false&color=ff7da8) |
| Delay | ![](https://img.shields.io/static/v1?label=&message=1000ms&color=b0a2f9) |

---
How to configure
---

High Precision:
```c#
HWHash.HighPrecision = true;
```
High Priority:
```c#
HWHash.HighPriority = true;
```

Delay:
```c#
//update the Dictionary every 500ms (twice per second)
HWHash.SetDelay(500);
```


### License
This project is licensed under [GLWTPL](./LICENSE)