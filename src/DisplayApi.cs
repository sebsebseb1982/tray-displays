using System;
using System.Runtime.InteropServices;

namespace TrayDisplays
{
    // P/Invoke + constantes Windows.
    // Les tailles et offsets des structures DISPLAYCONFIG_* et DEVMODEW ont été vérifiés avec
    // wingdi.h du SDK Windows 10 (10.0.18362.0) : SRC=20, TGT=48, PATH=72, MODE=64, DEVMODEW=220.
    // Les structures sont lues/écrites dans des buffers bruts (little-endian) pour éviter toute
    // dépendance au marshalling des unions C.
    internal static class Native
    {
        // --- QueryDisplayConfig ---
        public const uint QdcAllPaths = 0x00000001;
        public const uint QdcOnlyActivePaths = 0x00000002;
        public const uint QdcDatabaseCurrent = 0x00000004;
        public const uint QdcVirtualModeAware = 0x00000010;
        public const uint QdcIncludeHmd = 0x00000020;

        // --- SetDisplayConfig ---
        public const uint SdcTopologyMinimumCount = 0x00000001;
        public const uint SdcTopologyClone = 0x00000002;
        public const uint SdcTopologyExtend = 0x00000004;
        public const uint SdcTopologyExternal = 0x00000008;
        public const uint SdcTopologySupplied = 0x00000010;
        public const uint SdcUseSuppliedConfig = 0x00000020;
        public const uint SdcValidate = 0x00000040;
        public const uint SdcApply = 0x00000080;
        public const uint SdcSaveToDatabase = 0x00000200;
        public const uint SdcAllowChanges = 0x00000400;
        public const uint SdcAllowPathOrderChanges = 0x00002000;
        public const uint SdcVirtualModeAware = 0x00008000;

        // Flags d'un chemin (DISPLAYCONFIG_PATH_INFO.flags)
        public const uint PathActive = 0x00000001;
        public const uint PathSupportVirtualMode = 0x00000008;

        // DISPLAYCONFIG_MODE_INFO_TYPE
        public const uint ModeTypeSource = 1;
        public const uint ModeTypeTarget = 2;
        public const uint ModeTypeDesktop = 3;

        // Indicateur « index invalide » dans modeInfoIdx (0xFFFF)
        public const uint InvalidModeIndex = 0xFFFF;

        // adapterId est un LUID (8 octets) : id commence à l'offset 8.
        // --- offsets DISPLAYCONFIG_PATH_SOURCE_INFO ---
        public const int SrcSize = 20;
        public const int SrcAdapterId = 0;
        public const int SrcId = 8;
        public const int SrcModeInfoIdx = 12;  // high16 = sourceModeInfoIdx, low16 = cloneGroupId
        public const int SrcStatusFlags = 16;

        // --- offsets DISPLAYCONFIG_PATH_TARGET_INFO ---
        public const int TgtSize = 48;
        public const int TgtAdapterId = 0;
        public const int TgtId = 8;
        public const int TgtModeInfoIdx = 12;  // high16 = targetModeInfoIdx, low16 = desktopModeInfoIdx
        public const int TgtOutputTechnology = 16;
        public const int TgtRotation = 20;
        public const int TgtScaling = 24;
        public const int TgtRefreshNumerator = 28;
        public const int TgtRefreshDenominator = 32;
        public const int TgtScanLineOrdering = 36;
        public const int TgtAvailable = 40;    // BOOL targetAvailable
        public const int TgtStatusFlags = 44;

        // --- offsets DISPLAYCONFIG_PATH_INFO ---
        public const int PathSize = 72;
        public const int PathSrc = 0;
        public const int PathTgt = 20;
        public const int PathFlags = 68;

        // --- offsets DISPLAYCONFIG_MODE_INFO ---
        public const int ModeSize = 64;
        public const int ModeInfoType = 0;
        public const int ModeId = 4;
        public const int ModeAdapterId = 8;
        public const int ModeUnion = 16;

        // DISPLAYCONFIG_SOURCE_MODE (dans l'union)
        public const int SmWidth = ModeUnion + 0;
        public const int SmHeight = ModeUnion + 4;
        public const int SmStride = ModeUnion + 8;
        public const int SmPosX = ModeUnion + 12;
        public const int SmPosY = ModeUnion + 16;

        // DISPLAYCONFIG_TARGET_MODE (dans l'union)
        public const int TmPixelRate = ModeUnion + 0;
        // vSyncFreq (hSyncFreq est à +8) : c'est lui qui donne la fréquence de rafraîchissement.
        public const int TmRefreshNumerator = ModeUnion + 16;
        public const int TmRefreshDenominator = ModeUnion + 20;
        public const int TmActiveCx = ModeUnion + 24;
        public const int TmActiveCy = ModeUnion + 28;

        // DISPLAYCONFIG_DESKTOP_IMAGE_INFO (dans l'union)
        public const int DiSourceCx = ModeUnion + 0;
        public const int DiSourceCy = ModeUnion + 4;
        public const int DiRegionLeft = ModeUnion + 8;
        public const int DiRegionTop = ModeUnion + 12;
        public const int DiRegionRight = ModeUnion + 16;
        public const int DiRegionBottom = ModeUnion + 20;

        // --- DEVMODEW (taille 220) ---
        public const uint DmPosition = 0x00000020;
        public const uint CdsTest = 0x00000002;
        public const uint CdsNoReset = 0x10000000;
        public const uint CdsReset = 0x40000000;

        public const uint EnumDisplaySettingsMode = 0;
        public const uint EnumRegistrySettings = 0xFFFFFFFE;
        public const uint EnumCurrentSettings = 0xFFFFFFFF;

        public const int DispChangeSuccessful = 0;
        public const int DispChangeRestart = 1;

        // --- Moniteurs ---
        public const uint MonitorIsPrimary = 0x00000001;

        // --- Flags DISPLAY_DEVICEW.StateFlags ---
        public const uint DevicePrimary = 0x00000004;
        public const uint DeviceAttachedToDesktop = 0x00000008;
        public const uint DeviceMirroringDriver = 0x00000002;
        public const uint DeviceRemoved = 0x00000080;
        public const uint EnumDisplayDevicesInitialized = 0x00000001;

        // --- Messages ---
        public const int WmDisplayChange = 0x001A;

        public const int ErrorSuccess = 0;
        public const int ErrorNoMoreItems = 259;

        [DllImport("user32.dll")]
        public static extern uint GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPaths,
            out uint numModes);

        // Paquet DISPLAYCONFIG_TARGET_DEVICE_NAME (420 octets) passé en buffer brut.
        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(IntPtr requestPacket);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint QueryDisplayConfig(
            uint flags,
            ref uint numPaths,
            IntPtr pathArray,
            ref uint numModes,
            IntPtr modeArray,
            IntPtr configLuid);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SetDisplayConfig(
            uint numPaths,
            IntPtr pathArray,
            uint numModes,
            IntPtr modeArray,
            uint flags,
            IntPtr configLuid);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsExW(
            string deviceName,
            ref DevMode devMode,
            IntPtr hwnd,
            uint flags,
            IntPtr param);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettingsExW(
            string deviceName,
            uint modeIndex,
            ref DevMode devMode,
            uint flags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevicesW(
            string device,
            uint index,
            ref DisplayDeviceW deviceInfo,
            uint flags);

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx info);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }

    // DEVMODEW : layout séquentiel, total 220 octets.
    // Offsets vérifiés par offsetof dans le SDK : dmSize@68, dmFields@72, dmPosition@76/80,
    // dmBitsPerPel@168, dmPelsWidth@172, dmPelsHeight@176, dmDisplayFrequency@184.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;      // 0..63
        public ushort dmSpecVersion;     // 64
        public ushort dmDriverVersion;   // 66
        public ushort dmSize;            // 68
        public ushort dmDriverExtra;     // 70
        public uint dmFields;            // 72
        public int dmPositionX;          // 76
        public int dmPositionY;          // 80
        public uint dmDisplayOrientation;// 84
        public uint dmDisplayFixedOutput;// 88
        public short dmColor;            // 92
        public short dmDuplex;           // 94
        public short dmYResolution;      // 96
        public short dmTTOption;         // 98
        public short dmCollate;          // 100
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;        // 102..165
        public ushort dmLogPixels;       // 166
        public uint dmBitsPerPel;        // 168
        public uint dmPelsWidth;         // 172
        public uint dmPelsHeight;        // 176
        public uint dmDisplayFlags;      // 180
        public uint dmDisplayFrequency;  // 184
        public uint dmICMMethod;         // 188
        public uint dmICMIntent;         // 192
        public uint dmMediaType;         // 196
        public uint dmDitherType;        // 200
        public uint dmReserved1;         // 204
        public uint dmReserved2;         // 208
        public uint dmPanningWidth;      // 212
        public uint dmPanningHeight;     // 216
    }

    // DISPLAY_DEVICEW (840 octets) : cb, DeviceName[32], DeviceString[128], StateFlags,
    // DeviceID[128], DeviceKey[128].
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDeviceW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;            // \\.\DISPLAY1 ou \\.\DISPLAY1\Monitor0
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;          // description (carte ou moniteur)
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    // MONITORINFOEXW : cb@0, rcMonitor@4, rcWork@20, dwFlags@36, szDevice@40.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public int cb;                       // 0
        public WinRect rcMonitor;            // 4
        public WinRect rcWork;               // 20
        public int dwFlags;                  // 36
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;              // 40
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal sealed class DisplayApiException : Exception
    {
        public readonly uint Code;

        public DisplayApiException(string message, uint code)
            : base(string.Format("{0} (code {1})", message, code))
        {
            this.Code = code;
        }
    }
}
