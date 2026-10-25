namespace PadMapper.Core.Devices;

/// <summary>
/// HID 解析（HidP）で取り出した1レポート分の値。Windows に依存する解析は App 側で行い、ここに詰めて Core に渡す。
/// </summary>
public sealed class HidSnapshot
{
    /// <summary>押されているボタン番号（Button ページの Usage）。</summary>
    public HashSet<int> Buttons { get; } = new();
    /// <summary>軸の値（Generic Desktop ページの Usage → 値）。機種による範囲の違いをなくすため、Logical Min〜Max を 0〜65535 に正規化してある。</summary>
    public Dictionary<int, int> Values { get; } = new();
    /// <summary>ハットスイッチ（0=上, 1=右上, 2=右 … 7=左上。中立は null）。</summary>
    public int? Hat { get; set; }

    public const int UsageX = 0x30, UsageY = 0x31, UsageZ = 0x32, UsageRx = 0x33, UsageRy = 0x34, UsageRz = 0x35,
        UsageSlider = 0x36, UsageDial = 0x37, UsageHat = 0x39;

    public static string UsageName(int usage) => usage switch
    {
        UsageX => "X", UsageY => "Y", UsageZ => "Z", UsageRx => "Rx", UsageRy => "Ry", UsageRz => "Rz",
        UsageSlider => "Slider", UsageDial => "Dial", UsageHat => "Hat",
        _ => $"0x{usage:X2}",
    };

    /// <summary>ハットの値が方向を含むか（斜めは2方向とも含む）。</summary>
    public static bool HatHas(int? hat, string direction)
    {
        if (hat is not int h) return false;
        return direction switch
        {
            "up" => h is 7 or 0 or 1,
            "right" => h is 1 or 2 or 3,
            "down" => h is 3 or 4 or 5,
            "left" => h is 5 or 6 or 7,
            _ => false,
        };
    }
}

/// <summary>軸の値の範囲（HID の Logical Minimum / Maximum）。</summary>
public readonly record struct HidRange(int Min, int Max)
{
    public const int Full = 65535;

    /// <summary>0〜65535 に正規化する。</summary>
    public int Normalize(int v) => Max == Min ? 0 : (int)Math.Round(Math.Clamp((v - Min) / (double)(Max - Min), 0, 1) * Full);
}
