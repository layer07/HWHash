// SPDX-License-Identifier: GPL-2.0-or-later
/*
 * HWHash.cs
 *
 * Version: @(#)HWHash.cs 1.6.0 22/10/2026
 *
 * Description: HWiNFO Shared Memory Interface
 *
 * Author: Magnus Leatti (Forbannet)
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
    private const uint SIG_ACTIVE = 0x53695748; // "HWiS", HWiNFO writes "DEAD" on exit

    // native offsets inside a reading element, values are 4 consecutive doubles: now, min, max, avg
    private static readonly int OFF_INDEX = (int)Marshal.OffsetOf<HWHASH_ELEMENT>(nameof(HWHASH_ELEMENT.Index));
    private static readonly int OFF_ID = (int)Marshal.OffsetOf<HWHASH_ELEMENT>(nameof(HWHASH_ELEMENT.ID));
    private static readonly int OFF_VALUE = (int)Marshal.OffsetOf<HWHASH_ELEMENT>(nameof(HWHASH_ELEMENT.Value));
    private static readonly int SIZE_ELEMENT = Marshal.SizeOf<HWHASH_ELEMENT>(), SIZE_HEADER = Marshal.SizeOf<HWHASH_HEADER>();

    private static readonly object _sync = new();
    private static readonly object _statsLock = new();

    private static MemoryMappedFile? _memMap;
    private static MemoryMappedViewAccessor? _view;
    private static unsafe byte* _base;
    private static HWINFO_MEM _layout;
    private static HWHashStats _stats;
    private static long _pollTime;
    private static bool _lastReadOk;
    private static CancellationTokenSource? _pollingCTS;
    private static Task? _pollingTask;
    private static HWHASH_HEADER[] _headers = [];
    private static int _sensorSetVersion;
    private static RelevantCache? _relevant;

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

    /// <summary>Raised on the polling thread after every successful read cycle.</summary>
    public static event Action? Updated;

    /// <summary>True while HWiNFO's shared memory is open and active.</summary>
    public static unsafe bool IsConnected => _base != null;

    /// <summary>HWiNFO's own timestamp of its last sensor poll. Stops moving when the data goes stale.</summary>
    public static DateTimeOffset LastUpdate => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _pollTime));

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
        lock (_sync)
        {
            if (_pollingCTS != null) return true;

            ReadSensors();
            if (!_lastReadOk)
            {
                Debug.WriteLine("[HWHash] HWiNFO shared memory not available.");
                return false;
            }

            _pollingCTS = new CancellationTokenSource();
            _pollingTask = PollSensorsAsync(_pollingCTS.Token);
            return true;
        }
    }

    public static void Stop()
    {
        lock (_sync)
        {
            _pollingCTS?.Cancel();
            _pollingCTS = null;
        }
    }

    public static async Task StopAsync()
    {
        Task? task;
        lock (_sync)
        {
            _pollingCTS?.Cancel();
            _pollingCTS = null;
            task = _pollingTask;
        }
        if (task != null) await task;
    }

    public static string GetJsonString(bool order = false) =>
        order ? JsonSerializer.Serialize(GetOrderedList(), HWHashJsonContext.Default.ListHWINFO_HASH) :
                JsonSerializer.Serialize(Sensors, HWHashJsonContext.Default.ConcurrentDictionaryUInt64HWINFO_HASH);

    public static string GetJsonStringMini(bool order = false) =>
        order ? JsonSerializer.Serialize(GetOrderedListMini(), HWHashJsonContext.Default.ListHWINFO_HASH_MINI) :
                JsonSerializer.Serialize(SensorsMini, HWHashJsonContext.Default.ConcurrentDictionaryUInt64HWINFO_HASH_MINI);

    public static HWHashStats GetHWHashStats()
    {
        lock (_statsLock) return _stats;
    }

    public static List<HWINFO_HASH> GetOrderedList() => Ordered(Sensors, static s => s.IndexOrder);

    public static List<HWINFO_HASH_MINI> GetOrderedListMini() => Ordered(SensorsMini, static s => s.IndexOrder);

    // IndexOrder is the dense slot in HWiNFO's reading array, so each value drops straight into place: O(n), no sort.
    // Falls back to a sort if the orders are ever not a clean 0..n-1 permutation.
    private static List<T> Ordered<T>(ConcurrentDictionary<ulong, T> source, Func<T, int> order)
    {
        var list = new List<T>(source.Count);
        CollectionsMarshal.SetCount(list, list.Capacity);
        var span = CollectionsMarshal.AsSpan(list);
        var placed = new bool[span.Length];
        bool dense = true;
        int n = 0;

        foreach (var kv in source)
        {
            int i = order(kv.Value);
            if (n == span.Length || (uint)i >= (uint)span.Length || placed[i]) { dense = false; break; }
            span[i] = kv.Value;
            placed[i] = true;
            n++;
        }

        if (dense && n == span.Length) return list;

        list.Clear();
        foreach (var kv in source) list.Add(kv.Value);
        list.Sort((a, b) => order(a).CompareTo(order(b)));
        return list;
    }

    public static List<HWINFO_HASH> GetRelevantList()
    {
        // versioned so a build that races with a sensor-set change can never be cached as current
        int version = Volatile.Read(ref _sensorSetVersion);
        var cache = _relevant;
        if (cache == null || cache.Version != version)
            _relevant = cache = new RelevantCache(version, BuildRelevant());

        var list = new List<HWINFO_HASH>(cache.Items.Length);

        foreach (var (uid, clean) in cache.Items)
        {
            if (Sensors.TryGetValue(uid, out var sensor))
                list.Add(sensor with { NameCustom = clean });
        }

        return list;
    }

    private static (ulong, string)[] BuildRelevant()
    {
        var found = new List<HWINFO_HASH>();

        foreach (var kv in Sensors)
        {
            if (RelevantSensorsSet.Contains(kv.Value.NameDefault))
                found.Add(kv.Value);
        }

        found.Sort(static (a, b) => a.IndexOrder.CompareTo(b.IndexOrder));
        return found.Select(s => (s.UniqueID, s.NameDefault.Replace(" ", "").Replace("/", "") + s.SensorIndex)).ToArray();
    }

    private static async Task PollSensorsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_delayMs));

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (timer.Period.TotalMilliseconds != _delayMs)
                    timer.Period = TimeSpan.FromMilliseconds(_delayMs);

                long start = Stopwatch.GetTimestamp();
                ReadSensors();
                long elapsed = Stopwatch.GetTimestamp() - start;

                double ms = elapsed * 1000.0 / Stopwatch.Frequency;
                lock (_statsLock) _stats = _stats with { CollectionTime = ms, CollectionTimeTicks = elapsed };

                if (_lastReadOk && Updated is { } handler)
                {
                    try { handler(); }
                    catch (Exception ex) { Debug.WriteLine($"[HWHash] Updated handler threw: {ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static unsafe void ReadSensors()
    {
        lock (_sync)
        {
            _lastReadOk = false;

            try
            {
                if (_base == null && !Connect()) return;

                var mem = Unsafe.ReadUnaligned<HWINFO_MEM>(_base);
                if (mem.Sig != SIG_ACTIVE || !FitsView(mem))
                {
                    // HWiNFO exited or restarted: drop the handle so it can recreate the section, retry next cycle
                    Debug.WriteLine("[HWHash] Shared memory went inactive, reconnecting.");
                    Disconnect();
                    if (!Connect()) return;
                    mem = Unsafe.ReadUnaligned<HWINFO_MEM>(_base);
                    if (!FitsView(mem)) return;
                }

                bool rebuild = !SameLayout(mem, _layout);
                if (rebuild)
                {
                    BuildHeaders(mem);
                    _layout = mem;
                }

                int count = (int)mem.TOTAL_ReadingElements;
                int stride = (int)mem.SIZE_Reading;
                byte* readings = _base + mem.OFFSET_Reading;
                HashSet<ulong>? seen = rebuild ? new HashSet<ulong>(count) : null;

                for (int i = 0; i < count; i++)
                {
                    byte* e = readings + (long)i * stride;
                    ulong uid = MakeUID(Unsafe.ReadUnaligned<uint>(e + OFF_ID), Unsafe.ReadUnaligned<uint>(e + OFF_INDEX));

                    if (!rebuild && Sensors.TryGetValue(uid, out var existing))
                    {
                        double* v = (double*)(e + OFF_VALUE);
                        UpdateValues(uid, existing,
                            Unsafe.ReadUnaligned<double>(v), Unsafe.ReadUnaligned<double>(v + 1),
                            Unsafe.ReadUnaligned<double>(v + 2), Unsafe.ReadUnaligned<double>(v + 3));
                    }
                    else
                    {
                        AddOrReplace(uid, e, i);
                        seen?.Add(uid);
                    }
                }

                if (seen != null)
                {
                    foreach (var uid in Sensors.Keys)
                    {
                        if (!seen.Contains(uid))
                        {
                            Sensors.TryRemove(uid, out _);
                            SensorsMini.TryRemove(uid, out _);
                        }
                    }
                }

                lock (_statsLock) _stats = _stats with { TotalCategories = mem.SS_SensorElements, TotalEntries = mem.TOTAL_ReadingElements };
                Interlocked.Exchange(ref _pollTime, mem.PollTime);
                _lastReadOk = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWHash] Error reading sensors: {ex.Message}");
            }
        }
    }

    // Hot path. Skips the write (and the dictionary node allocation) when nothing changed;
    // the skip condition guarantees the stored record is identical to what a write would produce.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateValues(ulong uid, in HWINFO_HASH existing, double now, double min, double max, double avg)
    {
        if (!Same(existing.ValueNow, now) || !Same(existing.ValuePrev, existing.ValueNow) ||
            !Same(existing.ValueMin, min) || !Same(existing.ValueMax, max) || !Same(existing.ValueAvg, avg))
        {
            Sensors[uid] = existing with
            {
                ValuePrev = existing.ValueNow,
                ValueNow = now,
                ValueMin = min,
                ValueMax = max,
                ValueAvg = avg
            };
        }

        if (SensorsMini.TryGetValue(uid, out var mini) &&
            (!Same(mini.ValueNow, now) || !Same(mini.ValuePrev, mini.ValueNow)))
        {
            SensorsMini[uid] = mini with
            {
                ValuePrev = mini.ValueNow,
                ValueNow = now
            };
        }
    }

    // Cold path: first sighting or layout change. Full parse, names refreshed, ValuePrev carried over.
    private static unsafe void AddOrReplace(ulong uid, byte* e, int order)
    {
        var r = Marshal.PtrToStructure<HWHASH_ELEMENT>((IntPtr)e);
        string typeStr = TypeToString(r.SENSOR_TYPE);

        var header = r.Index < _headers.Length ? _headers[r.Index] : default;
        ulong parentUid = MakeUID(header.ID, header.Instance);

        double prev = Sensors.TryGetValue(uid, out var old) ? old.ValueNow : r.Value;
        double prevMini = SensorsMini.TryGetValue(uid, out var oldMini) ? oldMini.ValueNow : r.Value;

        Sensors[uid] = new HWINFO_HASH(
            typeStr, r.Index, r.ID, uid,
            r.NameDefault, r.NameCustom, r.Unit,
            r.Value, r.ValueMin, r.ValueMax, r.ValueAvg, prev,
            header.NameDefault ?? "", header.NameCustom ?? "",
            header.ID, header.Instance, parentUid, order
        );
        SensorsMini[uid] = new HWINFO_HASH_MINI(uid, r.NameCustom, r.Unit, prevMini, r.Value, order, typeStr);

        Interlocked.Increment(ref _sensorSetVersion);
    }

    private sealed record RelevantCache(int Version, (ulong UID, string CleanName)[] Items);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

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

    // One persistent view for the lifetime of the connection instead of a map/copy per poll.
    private static unsafe bool Connect()
    {
        try
        {
            _memMap = MemoryMappedFile.OpenExisting(SHARED_MEM_PATH, MemoryMappedFileRights.Read);
            _view = _memMap.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read);

            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _base = ptr + _view.PointerOffset;

            if (Unsafe.ReadUnaligned<HWINFO_MEM>(_base).Sig == SIG_ACTIVE) return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HWHash] Error opening shared memory: {ex.Message}");
        }

        Disconnect();
        return false;
    }

    private static unsafe void Disconnect()
    {
        if (_base != null)
        {
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }

        _view?.Dispose();
        _view = null;
        _memMap?.Dispose();
        _memMap = null;
        _layout = default;
    }

    private static bool FitsView(in HWINFO_MEM m) =>
        (ulong)m.SS_OFFSET + (ulong)m.SS_SIZE * m.SS_SensorElements <= (ulong)_view!.Capacity &&
        (ulong)m.OFFSET_Reading + (ulong)m.SIZE_Reading * m.TOTAL_ReadingElements <= (ulong)_view.Capacity &&
        m.SIZE_Reading >= SIZE_ELEMENT && m.SS_SIZE >= SIZE_HEADER;

    private static bool SameLayout(in HWINFO_MEM a, in HWINFO_MEM b) =>
        a.SS_OFFSET == b.SS_OFFSET && a.SS_SIZE == b.SS_SIZE && a.SS_SensorElements == b.SS_SensorElements &&
        a.OFFSET_Reading == b.OFFSET_Reading && a.SIZE_Reading == b.SIZE_Reading &&
        a.TOTAL_ReadingElements == b.TOTAL_ReadingElements;

    private static unsafe void BuildHeaders(in HWINFO_MEM mem)
    {
        var headers = new HWHASH_HEADER[mem.SS_SensorElements];
        byte* basePtr = _base + mem.SS_OFFSET;

        for (int i = 0; i < headers.Length; i++)
            headers[i] = Marshal.PtrToStructure<HWHASH_HEADER>((IntPtr)(basePtr + (long)i * mem.SS_SIZE));

        _headers = headers;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MakeUID(uint a, uint b) => ((ulong)a << 32) | b;

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

// Source-generated serializers: no runtime reflection, trim/AOT friendly, byte-identical output.
[JsonSerializable(typeof(List<HWHash.HWINFO_HASH>))]
[JsonSerializable(typeof(List<HWHash.HWINFO_HASH_MINI>))]
[JsonSerializable(typeof(ConcurrentDictionary<ulong, HWHash.HWINFO_HASH>))]
[JsonSerializable(typeof(ConcurrentDictionary<ulong, HWHash.HWINFO_HASH_MINI>))]
internal partial class HWHashJsonContext : JsonSerializerContext { }
