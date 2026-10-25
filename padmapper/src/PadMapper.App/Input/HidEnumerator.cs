using System.Runtime.InteropServices;
using System.Text;
using PadMapper.App.Native;
using PadMapper.Core.Devices;
using PadMapper.Core.Logging;

namespace PadMapper.App.Input;

/// <summary>
/// 接続中の HID デバイスを SetupAPI で列挙する。
/// デバイスはアクセス権 0（問い合わせのみ）で開くので、ゲームや他のアプリの邪魔をしない。
/// </summary>
internal static class HidEnumerator
{
    public static List<HidDeviceInfo> Enumerate(ILog log)
    {
        var result = new List<HidDeviceInfo>();
        Win32.HidD_GetHidGuid(out var guid);
        var set = Win32.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, Win32.DIGCF_PRESENT | Win32.DIGCF_DEVICEINTERFACE);
        if (set == Win32.INVALID_HANDLE_VALUE) return result;
        try
        {
            var data = new Win32.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Win32.SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; Win32.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                var path = GetPath(set, ref data);
                if (path == null) continue;
                var info = Query(path, log);
                if (info != null) result.Add(info);
            }
        }
        finally
        {
            Win32.SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    private static string? GetPath(IntPtr set, ref Win32.SP_DEVICE_INTERFACE_DATA data)
    {
        Win32.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize は 64 ビットで 8、32 ビットで 6
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!Win32.SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>1つのデバイスの情報を取る。開けないデバイスは null。</summary>
    public static HidDeviceInfo? Query(string path, ILog log)
    {
        using var h = Win32.CreateFile(path, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var attr = new Win32.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Win32.HIDD_ATTRIBUTES>() };
        if (!Win32.HidD_GetAttributes(h, ref attr)) return null;

        int usagePage = 0, usage = 0, inLen = 0, outLen = 0;
        if (Win32.HidD_GetPreparsedData(h, out var pp))
        {
            try
            {
                if (Win32.HidP_GetCaps(pp, out var caps) == Win32.HIDP_STATUS_SUCCESS)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                }
            }
            finally
            {
                Win32.HidD_FreePreparsedData(pp);
            }
        }

        return new HidDeviceInfo
        {
            Path = path,
            VendorId = attr.VendorID,
            ProductId = attr.ProductID,
            UsagePage = usagePage,
            Usage = usage,
            InputReportLength = inLen,
            OutputReportLength = outLen,
            Product = ReadString(h, Win32.HidD_GetProductString),
            Manufacturer = ReadString(h, Win32.HidD_GetManufacturerString),
            Connection = HidDeviceInfo.GuessConnection(path),
        };
    }

    private delegate bool StringGetter(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buffer, int length);

    private static string ReadString(Microsoft.Win32.SafeHandles.SafeFileHandle h, StringGetter getter)
    {
        var buf = new byte[512];
        try
        {
            if (!getter(h, buf, buf.Length)) return "";
            var s = Encoding.Unicode.GetString(buf);
            int end = s.IndexOf('\0');
            return (end >= 0 ? s[..end] : s).Trim();
        }
        catch
        {
            return "";
        }
    }
}
