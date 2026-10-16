using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RazerBatteryTray;

public enum ReadStatus { Ok, Asleep, NotFound }

public sealed record BatteryReading(ReadStatus Status, string? DeviceName = null, int Percent = 0, bool Charging = false);

/// <summary>
/// Reads battery level from Razer mice over the Razer 90-byte HID feature report protocol
/// (same packets as OpenMouse's mouse-protocol and RazerBatteryTaskbar).
///
/// Packet (90 bytes, sent as HID report id 0):
///   [0] status   [1] transaction id   [5] data size   [6] command class   [7] command id
///   [8..87] args [88] XOR of bytes 2..87
/// Battery: class 0x07 / id 0x80, percent = args[1] * 100 / 255.
/// Charging: class 0x07 / id 0x84, charging when args[1] == 1.
/// </summary>
public static class RazerHid
{
    const ushort RazerVid = 0x1532;
    const int PacketLength = 90;
    const int ReportLength = PacketLength + 1; // + report id byte on Windows

    const byte StatusBusy = 0x01, StatusOk = 0x02, StatusTimeout = 0x04;

    // 0x1f for current-generation mice (Viper V3 Pro, DeathAdder V3, Basilisk V3...),
    // 0x3f for older ones (DeathAdder V2 Pro, Viper Ultimate...), 0xff on some dongles.
    static readonly byte[] TransactionIds = [0x1f, 0x3f, 0xff];

    // Remembers which interface path / transaction id answered last time.
    static string? _lastPath;
    static byte _lastTid = 0x1f;

    /// <summary>
    /// Finds the mouse and reads its battery. Interfaces whose top-level collection is a
    /// Generic Desktop Mouse are tried first, and when any exists only those are used, so a
    /// Razer headset or keyboard can never be reported in place of the mouse.
    /// </summary>
    public static BatteryReading Read(Action<string>? log = null)
    {
        var candidates = new List<(string Path, bool IsMouse)>();
        foreach (var path in EnumerateRazerInterfaces())
        {
            using var handle = Open(path);
            var caps = handle == null ? null : Caps(handle);
            log?.Invoke($"{path}\n    open={handle != null} usage={caps?.UsagePage:X4}/{caps?.Usage:X4} featureLen={caps?.FeatureReportByteLength}");
            if (caps is { } c && c.FeatureReportByteLength == ReportLength)
                candidates.Add((path, c.UsagePage == 0x01 && c.Usage == 0x02));
        }

        bool anyMouse = candidates.Any(c => c.IsMouse);
        var ordered = candidates
            .Where(c => c.IsMouse || !anyMouse)
            .OrderBy(c => c.Path == _lastPath ? 0 : 1)
            // The control channel is usually on the first interface's mouse collection.
            .ThenBy(c => c.Path.Contains("mi_00", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(c => c.Path);
        log?.Invoke($"candidates: {candidates.Count}, mouse interfaces: {candidates.Count(c => c.IsMouse)}");

        bool sawAsleep = false;
        string? asleepName = null;

        foreach (var path in ordered)
        {
            using var handle = Open(path);
            if (handle == null) continue;

            var tids = new List<byte> { _lastTid };
            tids.AddRange(TransactionIds.Where(t => t != _lastTid));

            foreach (var tid in tids)
            {
                var battery = Query(handle, tid, 0x07, 0x80, out byte status);
                log?.Invoke($"  {path}\n    tid=0x{tid:X2} battery status=0x{status:X2}{(battery != null ? $" raw={battery[1]}" : "")}");
                if (battery == null)
                {
                    if (status == StatusTimeout)
                    {
                        sawAsleep = true;
                        asleepName ??= ProductName(handle);
                    }
                    continue;
                }

                var charging = Query(handle, tid, 0x07, 0x84, out _);
                _lastPath = path;
                _lastTid = tid;
                return new BatteryReading(
                    ReadStatus.Ok,
                    ProductName(handle),
                    (int)Math.Round(battery[1] * 100.0 / 255.0),
                    charging != null && charging[1] == 1);
            }
        }

        return sawAsleep ? new BatteryReading(ReadStatus.Asleep, asleepName) : new BatteryReading(ReadStatus.NotFound);
    }

    /// <summary>Sends one command and returns the reply args, or null (status says why).</summary>
    static byte[]? Query(SafeFileHandle handle, byte tid, byte cls, byte id, out byte status)
    {
        status = 0;
        var request = new byte[ReportLength];
        // request[0] = report id 0; packet starts at offset 1.
        request[1 + 1] = tid;
        request[1 + 5] = 0x02;
        request[1 + 6] = cls;
        request[1 + 7] = id;
        request[1 + 88] = Checksum(request);

        for (int send = 0; send < 3; send++)
        {
            if (!HidD_SetFeature(handle, request, ReportLength)) return null;

            for (int read = 0; read < 6; read++)
            {
                Thread.Sleep(100);
                var reply = new byte[ReportLength];
                if (!HidD_GetFeature(handle, reply, ReportLength)) return null;

                status = reply[1];
                if (status == StatusBusy) continue;
                if (reply[1 + 88] != Checksum(reply)) break; // corrupt: re-send
                if (status != StatusOk) return null;
                if (reply[1 + 6] != cls || reply[1 + 7] != id) break; // stale reply: re-send

                return reply.AsSpan(1 + 8, 80).ToArray();
            }
        }
        return null;
    }

    static byte Checksum(byte[] report)
    {
        byte crc = 0;
        for (int i = 1 + 2; i < 1 + 88; i++) crc ^= report[i];
        return crc;
    }

    static HIDP_CAPS? Caps(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var pp)) return null;
        try
        {
            return HidP_GetCaps(pp, out var caps) == HIDP_STATUS_SUCCESS ? caps : null;
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    static string? ProductName(SafeFileHandle handle)
    {
        var buf = new byte[256];
        if (!HidD_GetProductString(handle, buf, buf.Length)) return null;
        var s = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
        return s.Length > 0 ? s : null;
    }

    static SafeFileHandle? Open(string path)
    {
        // Access 0: Windows keeps mouse collections exclusive for reads,
        // but feature reports still work on a handle opened without access rights.
        var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }

    static List<string> EnumerateRazerInterfaces()
    {
        var result = new List<string>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size == 0) continue;
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (path != null && path.Contains($"vid_{RazerVid:x4}", StringComparison.OrdinalIgnoreCase))
                        result.Add(path);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    #region Win32

    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    #endregion
}
