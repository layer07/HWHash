using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Timers;

public class HWHash
{
    const string SHARED_MEM_PATH = "Global\\HWiNFO_SENS_SM2";
    const int SENSOR_STRING_LEN = 128;
    const int READING_STRING_LEN = 16;
    private static MemoryMappedFile? MEM_MAP;
    private static MemoryMappedViewAccessor? MEM_ACC;
    private static HWINFO_MEM HWINFO_MEMREGION;

    private static readonly Stopwatch SW = Stopwatch.StartNew();
    private static HWHashStats SelfData;

    private static int IndexOrder = 0;

    private static System.Timers.Timer aTimer = new(1000);

    private static Dictionary<uint, HWHASH_HEADER> HEADER_DICT = new Dictionary<uint, HWHASH_HEADER>();
    private static ConcurrentDictionary<ulong, HWINFO_HASH> SENSORHASH = new ConcurrentDictionary<ulong, HWINFO_HASH>();
    private static Thread? CoreThread;
    
    /// <summary>
    /// If [true], the thread will run at a high priority to avoid being delayed by other tasks.
    /// </summary>
    public static bool HighPriority = false;
    /// <summary>
    /// If [true], it will enable 1ms resolution, much better for newer systems, beware that it is a WIN32API call and all process are affected.
    /// </summary>
    public static bool HighPrecision = false;
    /// <summary>
    /// If [true] the measurements will be taken at precise intervals (every 1000ms) for instance.
    /// </summary>
    private static bool RoundMS = false; //edit: Not needed now [?]
    /// <summary>
    /// Delay in milliseconds (ms) between each update. Default is 1000 [ms], minimum is 100 [ms], maximum is 60000 [ms]. Make sure you configure HWInfo interval too so it will pull at the same rate.
    /// </summary>
    /// <param name="delayms">Time in milliseconds to wait, minimum 100, max 60000.</param>
    /// <returns>true if new delay is between the safe range, false otherwise.</returns>
    public static bool SetDelay(int delayms)
    {
        if(delayms < 100 || delayms > 60000)
        {
            return false;
        }
        aTimer.Interval = delayms;
        return true;
    }


    public static bool Launch()
    {        
        ReadMem();
        BuildHeaders();        

        CoreThread = new Thread(TimedStart);

        if (HighPriority == true)
        {
            CoreThread.Priority = ThreadPriority.Highest;
        }

        if(HighPrecision == true)
        {
            WinApi.TimeBeginPeriod(1);
        }

        CoreThread.Start();

      

        return true;
    }

    public static void TimedStart()
    {       
        aTimer.Elapsed += new ElapsedEventHandler(PollSensorData);        
        aTimer.Enabled = true;
    }

    private static async void PollSensorData(object source, ElapsedEventArgs e)
    {        
        ReadSensors();
    }

 

    private static void ReadMem()
    {
        HWINFO_MEMREGION = new HWINFO_MEM();
        try
        {
            MEM_MAP = MemoryMappedFile.OpenExisting(SHARED_MEM_PATH, MemoryMappedFileRights.Read);
            MEM_ACC = MEM_MAP.CreateViewAccessor(0L, Marshal.SizeOf(typeof(HWINFO_MEM)), MemoryMappedFileAccess.Read);
            MEM_ACC.Read(0L, out HWINFO_MEMREGION);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Cannot read HWINFO Shared Memory. Make sure that HWInfo is running and that you have admin privileges.");
        }
    }

    private static void BuildHeaders()
    {

        for (uint index = 0; index < HWINFO_MEMREGION.SS_SensorElements; ++index)
        {

            using (MemoryMappedViewStream viewStream = MEM_MAP.CreateViewStream(HWINFO_MEMREGION.SS_OFFSET + index * HWINFO_MEMREGION.SS_SIZE, HWINFO_MEMREGION.SS_SIZE, MemoryMappedFileAccess.Read))
            {
                byte[] buffer = new byte[(int)HWINFO_MEMREGION.SS_SIZE];
                viewStream.Read(buffer, 0, (int)HWINFO_MEMREGION.SS_SIZE);
                string hex = BitConverter.ToString(buffer).Replace("-", "");
                string str = Encoding.ASCII.GetString(buffer);
                GCHandle gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                HWHASH_HEADER structure = (HWHASH_HEADER)Marshal.PtrToStructure(gcHandle.AddrOfPinnedObject(), typeof(HWHASH_HEADER));
                HEADER_DICT.Add(index, structure);
            }
        }
        SelfData.TotalCategories = HWINFO_MEMREGION.SS_SensorElements;
    }

    private static void ReadSensors()
    {
        MiniBenchmark(0);
        SelfData.TotalEntries = HWINFO_MEMREGION.TOTAL_ReadingElements;
        for (uint index = 0; index < HWINFO_MEMREGION.TOTAL_ReadingElements; ++index)
        {
            using (MemoryMappedViewStream viewStream = MEM_MAP.CreateViewStream(HWINFO_MEMREGION.OFFSET_Reading + index * HWINFO_MEMREGION.SIZE_Reading, HWINFO_MEMREGION.SIZE_Reading, MemoryMappedFileAccess.Read))
            {
                byte[] buffer = new byte[(int)HWINFO_MEMREGION.SIZE_Reading];
                viewStream.Read(buffer, 0, (int)HWINFO_MEMREGION.SIZE_Reading);
                GCHandle gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                HWHASH_ELEMENT structure = (HWHASH_ELEMENT)Marshal.PtrToStructure(gcHandle.AddrOfPinnedObject(), typeof(HWHASH_ELEMENT));
                FormatSensor(structure);
                gcHandle.Free();
            }
        }
        MiniBenchmark(1);
    }

    private static void FormatSensor(HWHASH_ELEMENT READING)
    {
        ulong UNIQUE_ID = FastConcat(READING.ID, READING.Index);
        bool FirstTest = SENSORHASH.ContainsKey(UNIQUE_ID);
        if (FirstTest == false)
        {
            HWINFO_HASH curr = new()
            {
                ReadingType = TypeToString(READING.SENSOR_TYPE),
                SensorIndex = READING.Index,
                SensorID = READING.ID,
                UniqueID = UNIQUE_ID,
                NameDefault = READING.NameDefault,
                NameCustom = READING.NameCustom,
                Unit = READING.Unit,
                ValueNow = READING.Value,
                ValueMin = READING.ValueMin,
                ValueMax = READING.ValueMax,
                ValueAvg = READING.ValueAvg,
                ParentNameDefault = HEADER_DICT[READING.Index].NameDefault,
                ParentNameCustom = HEADER_DICT[READING.Index].NameCustom,
                ParentID = HEADER_DICT[READING.Index].ID,
                ParentInstance = HEADER_DICT[READING.Index].Instance,
                ParentUniqueID = FastConcat(HEADER_DICT[READING.Index].ID, HEADER_DICT[READING.Index].Instance),
                IndexOrder = IndexOrder++
            };
            SENSORHASH.TryAdd(UNIQUE_ID, curr);
        }
        else
        {
            HWINFO_HASH THIS_ENTRY = SENSORHASH[UNIQUE_ID];
            THIS_ENTRY.ValueNow = READING.Value;
            THIS_ENTRY.ValueMin = READING.ValueMin;
            THIS_ENTRY.ValueMax = READING.ValueMax;
            THIS_ENTRY.ValueAvg = READING.ValueAvg;
        }        
    }

    private static string TypeToString(SENSOR_READING_TYPE IN)
    {
        string OUT = "Unknown";
        switch (IN)
        {
            case SENSOR_READING_TYPE.SENSOR_TYPE_NONE:
                OUT = "None";
                break;
            case SENSOR_READING_TYPE.SENSOR_TYPE_TEMP:
                OUT = "Temperature";
                break;
            case SENSOR_READING_TYPE.SENSOR_TYPE_VOLT:
                OUT = "Voltage";
                break;
            case SENSOR_READING_TYPE.SENSOR_TYPE_FAN:
                OUT = "Fan";
                break;
            case SENSOR_READING_TYPE.SENSOR_TYPE_CURRENT:
                OUT = "Current";
                break;
            case SENSOR_READING_TYPE.SENSOR_TYPE_POWER:
                OUT = "Power";
                break;
            case SENSOR_READING_TYPE.SENSOR_TYPE_CLOCK:
                OUT = "Frequency";
                break;
            case SENSOR_READING_TYPE.SENSOR_TYPE_USAGE:
                OUT = "Usage";
                break;
            case SENSOR_READING_TYPE.SENSOR_TYPE_OTHER:
                OUT = "Other";
                break;
        }

        return OUT;
    }
    /// <summary>
    /// Get basic information about collection time (in milliseconds), total entries, etc...
    /// </summary>
    /// <returns>Returns a struct [HWHashStats] containing information about HWHash running thread.</returns>
    public static HWHashStats GetHWHashStats()
    {       
        return SelfData;
    }

    private static void MiniBenchmark(int Mode)
    {
        if(Mode == 0)
        {
            SW.Restart();
        }

        if(Mode == 1)
        {
            SelfData.CollectionTime = SW.ElapsedMilliseconds;
        }
        
    }

    /// <summary>
    /// Returns a list respecting the same order as HWInfo original user interface.
    /// </summary>
    public static List<HWINFO_HASH> GetOrderedList()
    {
        List<HWINFO_HASH> OrderedList = SENSORHASH.Values.OrderBy(x => x.IndexOrder).ToList();
        return OrderedList;
    }
    public struct HWHashStats
    {
        public long CollectionTime { get; set; }
        public uint TotalCategories { get; set; }
        public uint TotalEntries { get; set; }
    }

    public struct HWINFO_HASH
    {
        public string ReadingType { get; set; }
        public uint SensorIndex { get; set; }
        public uint SensorID { get; set; }
        public ulong UniqueID { get; set; }
        public string NameDefault { get; set; }
        public string NameCustom { get; set; }
        public string Unit { get; set; }
        public double ValueNow { get; set; }
        public double ValueMin { get; set; }
        public double ValueMax { get; set; }
        public double ValueAvg { get; set; }
        public string ParentNameDefault { get; set; }
        public string ParentNameCustom { get; set; }
        public uint ParentID { get; set; }
        public uint ParentInstance { get; set; }
        public ulong ParentUniqueID { get; set; }
        public int IndexOrder { get; set; }
    }

    public struct HWINFO_HASH_MINI
    {     
        public ulong UniqueID { get; set; }
        public string NameCustom { get; set; }
        public string Unit { get; set; }
        public double ValueNow { get; set; }        
        public int IndexOrder { get; set; }
    }


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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HWHASH_Sensor
    {
        public uint ID;
        public uint Instance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
        public string NameDefault;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = SENSOR_STRING_LEN)]
        public string NameCustom;
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

    private static ulong FastConcat(uint a, uint b)
    {
        if (b < 10U) return 10UL * a + b;
        if (b < 100U) return 100UL * a + b;
        if (b < 1000U) return 1000UL * a + b;
        if (b < 10000U) return 10000UL * a + b;
        if (b < 100000U) return 100000UL * a + b;
        if (b < 1000000U) return 1000000UL * a + b;
        if (b < 10000000U) return 10000000UL * a + b;
        if (b < 100000000U) return 100000000UL * a + b;
        return 1000000000UL * a + b;
    }

   


    private static class WinApi
    {
        /// <summary>TimeBeginPeriod(). See the Windows API documentation for details.</summary>

        [SuppressUnmanagedCodeSecurity]
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]

        public static extern uint TimeBeginPeriod(uint uMilliseconds);

        /// <summary>TimeEndPeriod(). See the Windows API documentation for details.</summary>

        [SuppressUnmanagedCodeSecurity]
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]

        public static extern uint TimeEndPeriod(uint uMilliseconds);
    }



}
