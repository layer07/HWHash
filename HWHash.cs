// SPDX-License-Identifier: GPL-2.0-or-later
/*
 * HWHash.cs
 * 
 * Version: @(#)HWHash.cs 1.0.3 22/02/2025
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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public static class HWHash
{
    private const string SHARED_MEM_PATH = "Global\\HWiNFO_SENS_SM2";
    private const int SENSOR_STRING_LEN = 128, READING_STRING_LEN = 16;

    private static MemoryMappedFile _memMap;
    private static HWINFO_MEM _memRegion;
    private static HWHashStats _stats = new HWHashStats(0, 0, 0, 0);
    private static int _indexOrder = 0;
    private static CancellationTokenSource _pollingCTS;
    private static Task _pollingTask;

    private static readonly Dictionary<uint, HWHASH_HEADER> _headers = new Dictionary<uint, HWHASH_HEADER>();
    public static readonly ConcurrentDictionary<ulong, HWINFO_HASH> Sensors = new ConcurrentDictionary<ulong, HWINFO_HASH>();
    public static readonly ConcurrentDictionary<ulong, HWINFO_HASH_MINI> SensorsMini = new ConcurrentDictionary<ulong, HWINFO_HASH_MINI>();

    public static readonly List<string> RelevantSensors = new List<string>
    {
        "Physical Memory Load", "Physical Memory Used", "P-core 0 VID", "P-core 0 Clock", "Ring/LLC Clock",
        "Total CPU Usage", "CPU Package", "Core Max", "CPU Package Power", "Vcore", "+12V", "SPD Hub Temperature",
        "GPU Temperature", "GPU Memory Junction Temperature", "GPU 8-pin #1 Input Voltage",
        "GPU 8-pin #2 Input Voltage", "GPU 8-pin #3 Input Voltage", "GPU Power (Total)", "GPU Core Load",
        "GPU Memory Controller Load", "Current DL rate", "Current UP rate", "Total Errors"
    };

    public static bool HighPriority { get; set; } = false;
    public static bool HighPrecision { get; set; } = false;
    private static int _delayMs = 1000;

    /// <summary>Sets polling delay in milliseconds (20–60000).</summary>
    public static bool SetDelay(int ms) => (ms >= 20 && ms <= 60000) ? (_delayMs = ms, true).Item2 : false;

    /// <summary>Initializes HWHash and starts polling.</summary>
    public static bool Launch()
    {
        if (!IsHWInfoRunning())
            throw new InvalidOperationException("HWiNFO process not found.");
        if (!ReadMem()) return false;
        BuildHeaders();
        if (HighPrecision) { _ = WinApi.TimeBeginPeriod(1); }
        ReadSensors();
        _pollingCTS = new CancellationTokenSource();
        _pollingTask = PollSensorsAsync(_pollingCTS.Token);
        return true;
    }

    /// <summary>Stops the polling loop.</summary>
    public static void Stop()
    {
        if (_pollingCTS != null)
        {
            _pollingCTS.Cancel();
            _pollingCTS = null;
        }
        if (HighPrecision) { _ = WinApi.TimeEndPeriod(1); }
    }

    /// <summary>Returns JSON-serialized sensor data. If order==true, returns sensors in display order.</summary>
    public static string GetJsonString(bool order = false) =>
        order ? JsonSerializer.Serialize<List<HWINFO_HASH>>(GetOrderedList()) :
        JsonSerializer.Serialize<ConcurrentDictionary<ulong, HWINFO_HASH>>(Sensors);

    /// <summary>Returns JSON-serialized minified sensor data. If order==true, returns sensors in display order.</summary>
    public static string GetJsonStringMini(bool order = false) =>
        order ? JsonSerializer.Serialize<List<HWINFO_HASH_MINI>>(GetOrderedListMini()) :
        JsonSerializer.Serialize<ConcurrentDictionary<ulong, HWINFO_HASH_MINI>>(SensorsMini);

    /// <summary>Returns collection statistics (includes elapsed milliseconds and raw ticks).</summary>
    public static HWHashStats GetHWHashStats() => _stats;

    /// <summary>Returns sensors ordered by display index.</summary>
    public static List<HWINFO_HASH> GetOrderedList()
    {
        List<HWINFO_HASH> list = new List<HWINFO_HASH>(Sensors.Values);
        list.Sort(ExplicitComparison);
        return list;
    }

    /// <summary>Returns minified sensors ordered by display index.</summary>
    public static List<HWINFO_HASH_MINI> GetOrderedListMini()
    {
        List<HWINFO_HASH_MINI> list = new List<HWINFO_HASH_MINI>(SensorsMini.Values);
        list.Sort(ExplicitComparisonMini);
        return list;
    }

    /// <summary>Returns only relevant sensors.</summary>
    public static List<HWINFO_HASH> GetRelevantList()
    {
        List<HWINFO_HASH> list = new List<HWINFO_HASH>();
        foreach (HWINFO_HASH sensor in Sensors.Values)
            if (RelevantSensors.Contains(sensor.NameDefault))
            {
                string clean = sensor.NameDefault.Replace(" ", "").Replace("/", "");
                list.Add(sensor with { NameCustom = clean + sensor.SensorIndex });
            }
        list.Sort(ExplicitComparison);
        return list;
    }

    private static async Task PollSensorsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Stopwatch sw = Stopwatch.StartNew();
            ReadSensors();
            sw.Stop();
            double ms = sw.ElapsedTicks / (double)Stopwatch.Frequency * 1000;
            _stats = _stats with { CollectionTime = ms, CollectionTimeTicks = sw.ElapsedTicks };
            try { await Task.Delay(_delayMs, token); } catch (TaskCanceledException) { break; }
        }
    }

    private static void ReadSensors()
    {
        _stats = _stats with { TotalEntries = _memRegion.TOTAL_ReadingElements };
        MiniBenchmark(0);
        long totalSize = _memRegion.TOTAL_ReadingElements * _memRegion.SIZE_Reading;
        try
        {
            using (var accessor = _memMap.CreateViewAccessor(_memRegion.OFFSET_Reading, totalSize, MemoryMappedFileAccess.Read))
            {
                byte[] allData = new byte[totalSize];
                accessor.ReadArray(0, allData, 0, allData.Length);
                GCHandle handle = GCHandle.Alloc(allData, GCHandleType.Pinned);
                IntPtr basePtr = handle.AddrOfPinnedObject();
                Parallel.For(0, (int)_memRegion.TOTAL_ReadingElements,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    (int i) =>
                    {
                        IntPtr ptr = IntPtr.Add(basePtr, i * (int)_memRegion.SIZE_Reading);
                        HWHASH_ELEMENT reading = Marshal.PtrToStructure<HWHASH_ELEMENT>(ptr);
                        UpdateSensorData(reading);
                    });
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error reading sensors: " + ex.Message);
        }
        MiniBenchmark(1);
    }

    private static void UpdateSensorData(HWHASH_ELEMENT r)
    {
        ulong uid = FastConcat(r.ID, r.Index);
        if (!Sensors.ContainsKey(uid))
        {
            int order = Interlocked.Increment(ref _indexOrder) - 1;
            HWINFO_HASH_MINI mini = new HWINFO_HASH_MINI(uid, r.NameCustom, r.Unit, r.Value, r.Value, order, TypeToString(r.SENSOR_TYPE));
            HWINFO_HASH full = new HWINFO_HASH(
                TypeToString(r.SENSOR_TYPE),
                r.Index, r.ID, uid,
                r.NameDefault, r.NameCustom, r.Unit,
                r.Value, r.ValueMin, r.ValueMax, r.ValueAvg, r.Value,
                _headers[r.Index].NameDefault, _headers[r.Index].NameCustom,
                _headers[r.Index].ID, _headers[r.Index].Instance,
                FastConcat(_headers[r.Index].ID, _headers[r.Index].Instance),
                order);
            Sensors.TryAdd(uid, full);
            SensorsMini.TryAdd(uid, mini);
        }
        else
        {
            Sensors.AddOrUpdate(uid,
                (ulong key) => throw new Exception("Unexpected condition."),
                (ulong key, HWINFO_HASH prev) => prev with
                {
                    ValuePrev = prev.ValueNow,
                    ValueNow = r.Value,
                    ValueMin = r.ValueMin,
                    ValueMax = r.ValueMax,
                    ValueAvg = r.ValueAvg
                });
            SensorsMini.AddOrUpdate(uid,
                (ulong key) => throw new Exception("Unexpected condition."),
                (ulong key, HWINFO_HASH_MINI prev) => prev with
                {
                    ValuePrev = prev.ValueNow,
                    ValueNow = r.Value
                });
        }
    }

    private static int ExplicitComparison(HWINFO_HASH a, HWINFO_HASH b) => a.IndexOrder.CompareTo(b.IndexOrder);
    private static int ExplicitComparisonMini(HWINFO_HASH_MINI a, HWINFO_HASH_MINI b) => a.IndexOrder.CompareTo(b.IndexOrder);

    private static string TypeToString(SENSOR_READING_TYPE t)
    {
        int index = (int)t;
        return (index >= 0 && index < SensorTypeStrings.Length) ? SensorTypeStrings[index] : "Unknown";
    }

    private static bool ReadMem()
    {
        try
        {
            _memMap = MemoryMappedFile.OpenExisting(SHARED_MEM_PATH, MemoryMappedFileRights.Read);
            _memRegion = new HWINFO_MEM();
            using (var accessor = _memMap.CreateViewAccessor(0L, Marshal.SizeOf(typeof(HWINFO_MEM)), MemoryMappedFileAccess.Read))
            {
                accessor.Read(0L, out _memRegion);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error reading shared memory: " + ex.Message);
            return false;
        }
    }

    private static void BuildHeaders()
    {
        long totalSize = _memRegion.SS_SensorElements * _memRegion.SS_SIZE;
        try
        {
            using (var accessor = _memMap.CreateViewAccessor(_memRegion.SS_OFFSET, totalSize, MemoryMappedFileAccess.Read))
            {
                byte[] headerData = new byte[totalSize];
                accessor.ReadArray(0, headerData, 0, headerData.Length);
                GCHandle handle = GCHandle.Alloc(headerData, GCHandleType.Pinned);
                IntPtr basePtr = handle.AddrOfPinnedObject();
                for (uint i = 0; i < _memRegion.SS_SensorElements; i++)
                {
                    IntPtr ptr = IntPtr.Add(basePtr, (int)(i * _memRegion.SS_SIZE));
                    HWHASH_HEADER header = Marshal.PtrToStructure<HWHASH_HEADER>(ptr);
                    _headers[i] = header;
                }
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error building headers: " + ex.Message);
        }
        _stats = _stats with { TotalCategories = _memRegion.SS_SensorElements };
    }

    private static Stopwatch _benchSW = new Stopwatch();
    private static void MiniBenchmark(int mode)
    {
        if (mode == 0) _benchSW.Restart();
        else _stats = _stats with { CollectionTime = _benchSW.ElapsedMilliseconds };
    }

    private static ulong FastConcat(uint a, uint b) =>
        b < 10 ? 10UL * a + b :
        b < 100 ? 100UL * a + b :
        b < 1000 ? 1000UL * a + b :
        b < 10000 ? 10000UL * a + b :
        b < 100000 ? 100000UL * a + b :
        b < 1000000 ? 1000000UL * a + b :
        b < 10000000 ? 10000000UL * a + b :
        b < 100000000 ? 100000000UL * a + b :
        1000000000UL * a + b;

    private static bool IsHWInfoRunning()
    {
        Process[] processes = Process.GetProcesses();
        Regex regex = new Regex(@"hwinfo(?:32|64)?", RegexOptions.IgnoreCase);
        foreach (Process proc in processes)
            if (regex.IsMatch(proc.ProcessName))
                return true;
        return false;
    }

    private static class WinApi
    {
        [SuppressUnmanagedCodeSecurity]
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
        public static extern uint TimeBeginPeriod(uint uMilliseconds);
        [SuppressUnmanagedCodeSecurity]
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
        public static extern uint TimeEndPeriod(uint uMilliseconds);
    }

    public record struct HWHashStats(double CollectionTime, long CollectionTimeTicks, uint TotalCategories, uint TotalEntries)
    {
        public HWHashStats() : this(0, 0, 0, 0) { }
    }

    public record struct HWINFO_HASH(
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

    public record struct HWINFO_HASH_MINI(
        ulong UniqueID,
        string NameCustom,
        string Unit,
        double ValuePrev,
        double ValueNow,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Always)] int IndexOrder,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Always)] string ReadingType
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

    private enum SENSOR_READING_TYPE
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

    private static readonly string[] SensorTypeStrings = new string[]
    {
        "None",
        "Temperature",
        "Voltage",
        "Fan",
        "Current",
        "Power",
        "Frequency",
        "Usage",
        "Other"
    };
}
