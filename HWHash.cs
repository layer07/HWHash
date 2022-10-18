using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Timers;

public class HWHash
{
    const string HWiNFO_SHARED_MEM_FILE_NAME = "Global\\HWiNFO_SENS_SM2";
    const int HWiNFO_SENSORS_STRING_LEN = 128;
    const int HWiNFO_UNIT_STRING_LEN = 16;
    private static MemoryMappedFile? mmf;
    private static MemoryMappedViewAccessor? accessor;
    private static _HWiNFO_SHARED_MEM HWiNFOMemory;

    private static readonly Stopwatch SW = Stopwatch.StartNew();
    private static Diagnostics SelfData = new Diagnostics();
    
    private static int IndexOrder = 0;

    private static System.Timers.Timer aTimer = new System.Timers.Timer(1000);

    private static Dictionary<uint, HWINFO_HEADER> HEADER_DICT = new Dictionary<uint, HWINFO_HEADER>();
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
        //Thread(() => DivineMemLaunch()).Start();

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
        //Console.Out.WriteLineAsync(SW.ElapsedMilliseconds.ToString());
        MiniBenchmark(0);
        ReadSensors();
        MiniBenchmark(1);
    }

 

    private static void ReadMem()
    {
        HWiNFOMemory = new _HWiNFO_SHARED_MEM();
        try
        {
            mmf = MemoryMappedFile.OpenExisting(HWiNFO_SHARED_MEM_FILE_NAME, MemoryMappedFileRights.Read);
            accessor = mmf.CreateViewAccessor(0L, Marshal.SizeOf(typeof(_HWiNFO_SHARED_MEM)), MemoryMappedFileAccess.Read);
            accessor.Read(0L, out HWiNFOMemory);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Cannot read HWINFO Shared Memory. Make sure that HWInfo is running and that you have admin privileges");
        }
    }

    private static void BuildHeaders()
    {

        for (uint index = 0; index < HWiNFOMemory.dwNumSensorElements; ++index)
        {

            using (MemoryMappedViewStream viewStream = mmf.CreateViewStream(HWiNFOMemory.dwOffsetOfSensorSection + index * HWiNFOMemory.dwSizeOfSensorElement, HWiNFOMemory.dwSizeOfSensorElement, MemoryMappedFileAccess.Read))
            {
                byte[] buffer = new byte[(int)HWiNFOMemory.dwSizeOfSensorElement];
                viewStream.Read(buffer, 0, (int)HWiNFOMemory.dwSizeOfSensorElement);
                string hex = BitConverter.ToString(buffer).Replace("-", "");
                string str = Encoding.ASCII.GetString(buffer);
                GCHandle gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                HWINFO_HEADER structure = (HWINFO_HEADER)Marshal.PtrToStructure(gcHandle.AddrOfPinnedObject(), typeof(HWINFO_HEADER));
                int d = 0;
                //global::System.Console.WriteLine("{0} {1} -> {2}", structure.ID, structure.Instance, structure.NameDefault);
                //global::System.Console.WriteLine(structure.NameDefault);

                HEADER_DICT.Add(index, structure);
            }
        }

        SelfData.TotalCategories = HWiNFOMemory.dwNumSensorElements;
        //Console.ReadKey();
    }

    private static void ReadSensors()
    {
        SelfData.TotalEntries = HWiNFOMemory.dwNumReadingElements;
        for (uint index = 0; index < HWiNFOMemory.dwNumReadingElements; ++index)
        {
            using (MemoryMappedViewStream viewStream = mmf.CreateViewStream(HWiNFOMemory.dwOffsetOfReadingSection + index * HWiNFOMemory.dwSizeOfReadingElement, HWiNFOMemory.dwSizeOfReadingElement, MemoryMappedFileAccess.Read))
            {
                byte[] buffer = new byte[(int)HWiNFOMemory.dwSizeOfReadingElement];
                viewStream.Read(buffer, 0, (int)HWiNFOMemory.dwSizeOfReadingElement);
                GCHandle gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                _HWiNFO_ELEMENT structure = (_HWiNFO_ELEMENT)Marshal.PtrToStructure(gcHandle.AddrOfPinnedObject(), typeof(_HWiNFO_ELEMENT));
                FormatSensor(structure);
                gcHandle.Free();
            }

        }
    }

    private static void FormatSensor(_HWiNFO_ELEMENT READING)
    {
        ulong UNIQUE_ID = concat(READING.dwSensorID, READING.dwSensorIndex);
        bool FirstTest = SENSORHASH.ContainsKey(UNIQUE_ID);
        if (FirstTest == false)
        {
            HWINFO_HASH curr = new HWINFO_HASH();
            curr.ReadingType = _PrettyType(READING.tReading);
            curr.SensorIndex = READING.dwSensorIndex;
            curr.SensorID = READING.dwSensorID;
            curr.UniqueID = UNIQUE_ID;
            curr.NameDefault = READING.szLabelOrig;
            curr.NameCustom = READING.szLabelUser;
            curr.Unit = READING.szUnit;
            curr.ValueNow = READING.Value;
            curr.ValueMin = READING.ValueMin;
            curr.ValueMax = READING.ValueMax;
            curr.ValueAvg = READING.ValueAvg;
            curr.ParentNameDefault = HEADER_DICT[READING.dwSensorIndex].NameDefault;
            curr.ParentNameCustom = HEADER_DICT[READING.dwSensorIndex].NameCustom;
            curr.ParentID = HEADER_DICT[READING.dwSensorIndex].ID;
            curr.ParentInstance = HEADER_DICT[READING.dwSensorIndex].Instance;
            curr.ParentUniqueID = concat(HEADER_DICT[READING.dwSensorIndex].ID, HEADER_DICT[READING.dwSensorIndex].Instance);
            curr.IndexOrder = IndexOrder++;
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
        //curr.ReadingType = Enum.Parse()
    }

    private static string _PrettyType(SENSOR_READING_TYPE IN)
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

    public static Diagnostics GetSelfDiagnoticData()
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

    public struct Diagnostics
    {
        public long CollectionTime { get; set; }
        public uint TotalCategories { get; set; }
        public uint TotalEntries { get; set; }
    }

    private struct HWINFO_HASH
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct _HWiNFO_ELEMENT
    {
        public SENSOR_READING_TYPE tReading;
        public uint dwSensorIndex;
        public uint dwSensorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN)]
        public string szLabelOrig;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN)]
        public string szLabelUser;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_UNIT_STRING_LEN)]
        public string szUnit;
        public double Value;
        public double ValueMin;
        public double ValueMax;
        public double ValueAvg;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HWINFO_HEADER
    {
        public uint ID;
        public uint Instance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN)]
        public string NameDefault;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN)]
        public string NameCustom;
    }


    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct _HWiNFO_SHARED_MEM
    {
        public uint dwSignature;
        public uint dwVersion;
        public uint dwRevision;
        public long poll_time;
        public uint dwOffsetOfSensorSection;
        public uint dwSizeOfSensorElement;
        public uint dwNumSensorElements;
        public uint dwOffsetOfReadingSection;
        public uint dwSizeOfReadingElement;
        public uint dwNumReadingElements;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct _HWiNFO_SENSOR
    {
        public uint dwSensorID;
        public uint dwSensorInst;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN)]
        public string szSensorNameOrig;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = HWiNFO_SENSORS_STRING_LEN)]
        public string szSensorNameUser;
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

    private static ulong concat(uint a, uint b)
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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Interoperability", "CA1401:PInvokesShouldNotBeVisible"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2118:ReviewSuppressUnmanagedCodeSecurityUsage"), SuppressUnmanagedCodeSecurity]
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]

        public static extern uint TimeBeginPeriod(uint uMilliseconds);

        /// <summary>TimeEndPeriod(). See the Windows API documentation for details.</summary>

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Interoperability", "CA1401:PInvokesShouldNotBeVisible"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2118:ReviewSuppressUnmanagedCodeSecurityUsage"), SuppressUnmanagedCodeSecurity]
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]

        public static extern uint TimeEndPeriod(uint uMilliseconds);
    }



}
