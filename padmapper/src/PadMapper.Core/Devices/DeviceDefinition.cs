namespace PadMapper.Core.Devices;

/// <summary>接続中の HID デバイス（トップレベルコレクション）1つ分の情報。</summary>
public sealed record HidDeviceInfo
{
    public string Path { get; init; } = "";
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public int UsagePage { get; init; }
    public int Usage { get; init; }
    public string Product { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public int InputReportLength { get; init; }
    public int OutputReportLength { get; init; }
    public string Connection { get; init; } = "";

    public string VidPid => $"{VendorId:X4}:{ProductId:X4}";
    public string UsageText => $"{UsagePage:X4}/{Usage:X2}";

    /// <summary>ゲームパッド・ジョイスティック・ベンダー独自のコレクションかどうか（キーボード/マウスは除外）。</summary>
    public bool IsCandidate =>
        (UsagePage == 0x01 && (Usage == 0x04 || Usage == 0x05 || Usage == 0x08)) || UsagePage >= 0xFF00;

    /// <summary>デバイスパスから接続経路を推定する。</summary>
    public static string GuessConnection(string path)
    {
        var p = path.ToUpperInvariant();
        if (p.Contains("{00001124-0000-1000-8000-00805F9B34FB}") || p.Contains("BTHENUM")) return "Bluetooth";
        if (p.Contains("{00001812-0000-1000-8000-00805F9B34FB}") || p.Contains("BTHLEDEVICE")) return "Bluetooth LE";
        if (p.Contains("VID_") || p.Contains("USB")) return "USB";
        return "Other";
    }
}

/// <summary>
/// 生レポート → ControllerState の変換定義。組み込み（Vader 4 Pro）と、診断ウィザードで作った JSON の両方をこの形で扱う。
/// </summary>
public sealed class DeviceDefinition
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"builtin-sdl"（SDL のソース由来・未検証）または "learned"（ウィザードで実機から取得）。</summary>
    public string Source { get; set; } = "learned";
    /// <summary>実機で全項目を確認済みなら true。</summary>
    public bool Verified { get; set; }
    public DeviceMatch Match { get; set; } = new();
    /// <summary>先頭バイト（レポート ID）。null なら確認しない。</summary>
    public int? ReportId { get; set; }
    public int MinLength { get; set; } = 1;
    /// <summary>このレポートだと判定するための固定バイト。</summary>
    public List<ByteValue> Signature { get; set; } = new();
    public List<ButtonMap> Buttons { get; set; } = new();
    public List<AxisMap> Axes { get; set; } = new();
    public List<SensorMap> Gyro { get; set; } = new();
    public List<SensorMap> Accel { get; set; } = new();
    /// <summary>レポートを流してもらうために必要な初期化コマンド（16進文字列）。既定では送らない。</summary>
    public string? InitCommandHex { get; set; }
    public string? Notes { get; set; }
}

public sealed class DeviceMatch
{
    public int? VendorId { get; set; }
    public int? ProductId { get; set; }
    public int? UsagePage { get; set; }
    public int? Usage { get; set; }
    /// <summary>製品名に含まれる文字列（大文字小文字は区別しない）。</summary>
    public string? ProductContains { get; set; }

    public bool Matches(HidDeviceInfo d)
    {
        if (VendorId is int v && v != d.VendorId) return false;
        if (ProductId is int p && p != d.ProductId) return false;
        if (UsagePage is int up && up != d.UsagePage) return false;
        if (Usage is int u && u != d.Usage) return false;
        if (!string.IsNullOrEmpty(ProductContains) &&
            d.Product.IndexOf(ProductContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return VendorId != null || ProductId != null || UsagePage != null || !string.IsNullOrEmpty(ProductContains);
    }
}

public sealed class ByteValue
{
    public int Offset { get; set; }
    public int Value { get; set; }
}

/// <summary>ボタン1つ分。Value が null なら (byte &amp; Mask) != 0、あれば (byte &amp; Mask) == Value で押下。</summary>
public sealed class ButtonMap
{
    public string Input { get; set; } = "";
    public int Offset { get; set; }
    public int Mask { get; set; }
    public int? Value { get; set; }
}

public sealed class AxisMap
{
    /// <summary>LX / LY / RX / RY / LT / RT。</summary>
    public string Axis { get; set; } = "";
    public int Offset { get; set; }
    /// <summary>16ビット値の上位バイトの位置。省略時は Offset+1。</summary>
    public int? OffsetHi { get; set; }
    /// <summary>u8 / s8 / u16 / s16（16ビットはリトルエンディアン）。</summary>
    public string Format { get; set; } = "u8";
    /// <summary>スティックの中心値。トリガーでは使わない。</summary>
    public double? Center { get; set; }
    public double Min { get; set; }
    public double Max { get; set; } = 255;
    public bool Invert { get; set; }
}

public sealed class SensorMap
{
    /// <summary>x / y / z（ジャイロでは x=ピッチ, y=ヨー, z=ロール）。</summary>
    public string Axis { get; set; } = "";
    public int Offset { get; set; }
    public int? OffsetHi { get; set; }
    public string Format { get; set; } = "s16";
    /// <summary>生値に掛ける係数（ジャイロは deg/s、加速度は G になるように）。負なら向きが反転。</summary>
    public double Scale { get; set; } = 1;
}
