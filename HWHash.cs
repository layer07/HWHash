// SPDX-License-Identifier: GPL-2.0-or-later
/*
 * HWHash.cs
 * 
 * Version: @(#)HWHash.cs 1.5.0 07/04/2026
 *
 * Description: HWiNFO Shared Memory Interface
 *
 * Author: D. Leatti (Forbannet)
 * URL: https://kernelriot.com
 * Github: /layer07
 *
 *        ██▓    ▄▄▄     ▓██   ██▓▓█████  ██▀███  
 *       ▓██▒   ▒████▄    ▒██  ██▒▓█   ▀ ▓██ ▒ ██▒
 *       ▒██░   ▒██  ▀█▄   ▒██ ██░▒███   ▓██ ░▄█ ▒
 *       ▒██░   ░██▄▄▄▄██  ░ ▐██▓░▒▓█ ▄ ▒██▀▀█▄  
 *       ░██████▒▓█   ▓██▒ ░ ██▒▓░░▒████▒░██▓ ▒██▒
 *       ░ ▒░▓  ░▒▒   ▓▒█░  ██▒▒▒ ░░ ▒░ ░░ ▒▓ ░▒▓░
 *       ░ ░ ▒  ░ ▒   ▒▒ ░▓██ ░▒░  ░ ░  ░  ░▒ ░ ▒░
 *         ░ ░    ░   ▒   ▒ ▒ ░░     ░     ░░   ░ 
 *           ░  ░     ░  ░░ ░        ░  ░   ░     
 */
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class HWHash
{
    private const string SHARED_MEM_PATH = "Global\\HWiNFO_SENS_SM2";
    private const int SENSOR_STRING_LEN = 128, READING_STRING_LEN = 16;

    private static MemoryMappedFile? _memMap;
    private static HWINFO_MEM _memRegion;
    private static HWHashStats _stats;
    private static int _indexOrder;
    private static CancellationTokenSource? _pollingCTS;
    private static Task? _pollingTask;
    private static bool? _hwInfoRunningCache;
    private static HWHASH_HEADER[]? _headers;

    public static readonly ConcurrentDictionary<ulong, HWINFO_HASH> Sensors = new();
    public static readonly ConcurrentDictionary<ulong, HWINFO_HASH_MINI> SensorsMini = new();

    private static readonly FrozenSet<string> RelevantSensorsSet = new HashSet<string>
    {
        "Physical Memory Load", "Physical Memory Used", "P-core 0 VID", "P-core 0 Clock", "Ring/LLC Clock",
        "Total CPU Usage", "CPU Package", "Core Max", "CPU Package Power", "Vcore", "+12V", "SPD Hub Temperature",
        "GPU Temperature", "GPU Memory Junction Temperature", "GPU 8-pin #1 Input Voltage",
        "GPU 8-pin #2 Input Voltage", "GPU 8-pin #3 Input Voltage", "GPU Power (Total)", "GPU Core Load",
        "GPU Memory Controller Load", "Current DL rate", "Current UP rate", "Total Errors"
    }.ToFrozenSet();

    [Obsolete("HighPriority has no effect. Kept for backwards compatibility only.")]
    public static bool HighPriority { get; set; }

    [Obsolete("HighPrecision has no effect on modern Windows. Kept for backwards compatibility only.")]
    public static bool HighPrecision { get; set; }

    private static int _delayMs = 1000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SetDelay(int ms)
    {
        if (ms < 20 || ms > 60000) return false;
        _delayMs = ms;
        return true;
    }

    public static bool Launch()
    {
        if (!IsHWInfoRunning())
        {
            Debug.WriteLine("[HWHash] HWiNFO process not found.");
            return false;
        }

        if (!ReadMem())
        {
            Debug.WriteLine("[HWHash] Failed to read shared memory.");
            return false;
        }

        BuildHeaders();
        ReadSensors();

        _pollingCTS = new CancellationTokenSource();
        _pollingTask = PollSensorsAsync(_pollingCTS.Token);

        return true;
    }

    public static void Stop()
    {
        _pollingCTS?.Cancel();
        _pollingCTS = null;
    }

    public static async Task StopAsync()
    {
        _pollingCTS?.Cancel();
        if (_pollingTask != null) await _pollingTask;
        _pollingCTS = null;
    }

    public static string GetJsonString(bool order = false) =>
        order ? JsonSerializer.Serialize(GetOrderedList()) :
                JsonSerializer.Serialize(Sensors);

    public static string GetJsonStringMini(bool order = false) =>
        order ? JsonSerializer.Serialize(GetOrderedListMini()) :
                JsonSerializer.Serialize(SensorsMini);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HWHashStats GetHWHashStats() => _stats;

    public static List<HWINFO_HASH> GetOrderedList()
    {
        var list = new List<HWINFO_HASH>(Sensors.Values);
        list.Sort((a, b) => a.IndexOrder.CompareTo(b.IndexOrder));
        return list;
    }

    public static List<HWINFO_HASH_MINI> GetOrderedListMini()
    {
        var list = new List<HWINFO_HASH_MINI>(SensorsMini.Values);
        list.Sort((a, b) => a.IndexOrder.CompareTo(b.IndexOrder));
        return list;
    }

    public static List<HWINFO_HASH> GetRelevantList()
    {
        var list = new List<HWINFO_HASH>(RelevantSensorsSet.Count);

        foreach (var sensor in Sensors.Values)
        {
            if (RelevantSensorsSet.Contains(sensor.NameDefault))
            {
                string clean = sensor.NameDefault.Replace(" ", "").Replace("/", "") + sensor.SensorIndex;
                list.Add(sensor with { NameCustom = clean });
            }
        }

        list.Sort((a, b) => a.IndexOrder.CompareTo(b.IndexOrder));
        return list;
    }

    private static async Task PollSensorsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            long start = Stopwatch.GetTimestamp();
            ReadSensors();
            long elapsed = Stopwatch.GetTimestamp() - start;

            double ms = elapsed * 1000.0 / Stopwatch.Frequency;
            _stats = new HWHashStats(ms, elapsed, _stats.TotalCategories, _stats.TotalEntries);

            try { await Task.Delay(_delayMs, token); }
            catch (TaskCanceledException) { break; }
        }
    }

    private static void ReadSensors()
    {
        uint totalEntries = _memRegion.TOTAL_ReadingElements;
        _stats = new HWHashStats(_stats.CollectionTime, _stats.CollectionTimeTicks, _stats.TotalCategories, totalEntries);

        long totalSize = totalEntries * _memRegion.SIZE_Reading;
        byte[] pooledBuffer = ArrayPool<byte>.Shared.Rent((int)totalSize);

        try
        {
            using var accessor = _memMap!.CreateViewAccessor(
                            _memRegion.OFFSET_Reading,
                totalSize,
                MemoryMappedFileAccess.Read
            );

            accessor.ReadArray(0, pooledBuffer, 0, (int)totalSize);

            GCHandle handle = GCHandle.Alloc(pooledBuffer, GCHandleType.Pinned);
            IntPtr basePtr = handle.AddrOfPinnedObject();

            try
            {
                int stride = (int)_memRegion.SIZE_Reading;
                int limit = (int)totalEntries;

                // Unrolled loop for better ILP
                int i = 0;
                int unrolledLimit = limit - (limit % 4);

                for (; i < unrolledLimit; i += 4)
                {
                    ReadSensorElement(IntPtr.Add(basePtr, i * stride));
                    ReadSensorElement(IntPtr.Add(basePtr, (i + 1) * stride));
                    ReadSensorElement(IntPtr.Add(basePtr, (i + 2) * stride));
                    ReadSensorElement(IntPtr.Add(basePtr, (i + 3) * stride));
                }

                for (; i < limit; i++)
                {
                    ReadSensorElement(IntPtr.Add(basePtr, i * stride));
                }
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HWHash] Error reading sensors: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooledBuffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadSensorElement(IntPtr ptr)
    {
        var reading = Marshal.PtrToStructure<HWHASH_ELEMENT>(ptr);
        UpdateSensorData(reading);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateSensorData(HWHASH_ELEMENT r)
    {
        ulong uid = MakeUID(r.ID, r.Index);

        if (!Sensors.TryGetValue(uid, out var existing))
        {
            int order = Interlocked.Increment(ref _indexOrder) - 1;
            string typeStr = TypeToString(r.SENSOR_TYPE);

            var header = _headers![r.Index];
            ulong parentUid = MakeUID(header.ID, header.Instance);

            var mini = new HWINFO_HASH_MINI(uid, r.NameCustom, r.Unit, r.Value, r.Value, order, typeStr);
            var full = new HWINFO_HASH(
                typeStr, r.Index, r.ID, uid,
                r.NameDefault, r.NameCustom, r.Unit,
                r.Value, r.ValueMin, r.ValueMax, r.ValueAvg, r.Value,
                header.NameDefault, header.NameCustom,
                header.ID, header.Instance, parentUid, order
            );

            Sensors.TryAdd(uid, full);
            SensorsMini.TryAdd(uid, mini);
        }
        else
        {
            Sensors[uid] = existing with
            {
                ValuePrev = existing.ValueNow,
                ValueNow = r.Value,
                ValueMin = r.ValueMin,
                ValueMax = r.ValueMax,
                ValueAvg = r.ValueAvg
            };

            SensorsMini[uid] = SensorsMini[uid] with
            {
                ValuePrev = SensorsMini[uid].ValueNow,
                ValueNow = r.Value
            };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string TypeToString(SENSOR_READING_TYPE t) => t switch
    {
        SENSOR_READING_TYPE.SENSOR_TYPE_TEMP => "Temperature",
        SENSOR_READING_TYPE.SENSOR_TYPE_VOLT => "Voltage",
        SENSOR_READING_TYPE.SENSOR_TYPE_FAN => "Fan",
        SENSOR_READING_TYPE.SENSOR_TYPE_CURRENT => "Current",
        SENSOR_READING_TYPE.SENSOR_TYPE_POWER => "Power",
        SENSOR_READING_TYPE.SENSOR_TYPE_CLOCK => "Frequency",
        SENSOR_READING_TYPE.SENSOR_TYPE_USAGE => "Usage",
        SENSOR_READING_TYPE.SENSOR_TYPE_OTHER => "Other",
        _ => "None"
    };

    private static bool ReadMem()
    {
        try
        {
            _memMap = MemoryMappedFile.OpenExisting(SHARED_MEM_PATH, MemoryMappedFileRights.Read);

            using var accessor = _memMap.CreateViewAccessor(
                0L,
                Marshal.SizeOf<HWINFO_MEM>(),
                MemoryMappedFileAccess.Read
            );

            accessor.Read(0L, out _memRegion);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HWHash] Error reading shared memory: {ex.Message}");
            return false;
        }
    }

    private static void BuildHeaders()
    {
        uint count = _memRegion.SS_SensorElements;
        _headers = new HWHASH_HEADER[count];

        long totalSize = count * _memRegion.SS_SIZE;
        byte[] pooledBuffer = ArrayPool<byte>.Shared.Rent((int)totalSize);

        try
        {
            using var accessor = _memMap!.CreateViewAccessor(
                _memRegion.SS_OFFSET,
                totalSize,
                MemoryMappedFileAccess.Read
            );

            accessor.ReadArray(0, pooledBuffer, 0, (int)totalSize);

            GCHandle handle = GCHandle.Alloc(pooledBuffer, GCHandleType.Pinned);
            IntPtr basePtr = handle.AddrOfPinnedObject();

            try
            {
                int stride = (int)_memRegion.SS_SIZE;

                for (uint i = 0; i < count; i++)
                {
                    IntPtr ptr = IntPtr.Add(basePtr, (int)(i * stride));
                    _headers[i] = Marshal.PtrToStructure<HWHASH_HEADER>(ptr);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HWHash] Error building headers: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooledBuffer);
        }

        _stats = new HWHashStats(0, 0, count, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MakeUID(uint a, uint b) => ((ulong)a << 32) | b;

    private static bool IsHWInfoRunning()
    {
        if (_hwInfoRunningCache.HasValue)
            return _hwInfoRunningCache.Value;

        var processes = Process.GetProcesses();

        foreach (var proc in processes)
        {
            var name = proc.ProcessName;

            if (name.Length >= 6 &&
                (name.StartsWith("hwinfo", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("HWiNFO", StringComparison.Ordinal)))
            {
                _hwInfoRunningCache = true;
                return true;
            }
        }

        _hwInfoRunningCache = false;
        return false;
    }

    public readonly record struct HWHashStats(
        double CollectionTime,
        long CollectionTimeTicks,
        uint TotalCategories,
        uint TotalEntries
    );

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

    public readonly record struct HWINFO_HASH_MINI(
        ulong UniqueID,
        string NameCustom,
        string Unit,
        double ValuePrev,
        double ValueNow,
        [property: JsonIgnore] int IndexOrder,
        [property: JsonIgnore] string ReadingType
    );

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HWHASH_ELEMENT
    {
        public SENSOR_READING_TYPE SENSOR_TYPE;
        public uint Index;
        public uint ID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
        public string NameDefault;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
        public string NameCustom;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = READING_STRING_LEN)]
        public string Unit;
        public double Value;
        public double ValueMin;
        public double ValueMax;
        public double ValueAvg;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HWHASH_HEADER
    {
        public uint ID;
        public uint Instance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
        public string NameDefault;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
        public string NameCustom;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HWINFO_MEM
    {
        public uint Sig;
        public uint Ver;
        public uint Rev;
        public long PollTime;
        public uint SS_OFFSET;
        public uint SS_SIZE;
        public uint SS_SensorElements;
        public uint OFFSET_Reading;
        public uint SIZE_Reading;
        public uint TOTAL_ReadingElements;
    }

    private enum SENSOR_READING_TYPE : uint
    {
        SENSOR_TYPE_NONE,
        SENSOR_TYPE_TEMP,
        SENSOR_TYPE_VOLT,
        SENSOR_TYPE_FAN,
        SENSOR_TYPE_CURRENT,
        SENSOR_TYPE_POWER,
        SENSOR_TYPE_CLOCK,
        SENSOR_TYPE_USAGE,
        SENSOR_TYPE_OTHER,
    }
}