using System.Text;

namespace PadMapper.Core.Diagnostics;

/// <summary>生レポートの HEX 表示と、前回との差分。</summary>
public static class ReportDiff
{
    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public static byte[] FromHex(string hex)
    {
        var clean = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length % 2 == 1) clean = clean[..^1];
        var result = new byte[clean.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return result;
    }

    /// <summary>前回と値が変わったビット（バイトごとの XOR）。長さが違う部分は全ビット変化扱い。</summary>
    public static byte[] ChangedBits(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur)
    {
        var result = new byte[cur.Length];
        for (int i = 0; i < cur.Length; i++)
            result[i] = i < prev.Length ? (byte)(prev[i] ^ cur[i]) : (byte)0xFF;
        return result;
    }

    /// <summary>変化したビットを「オフセット:ビット番号」の一覧にする。</summary>
    public static IEnumerable<(int offset, int bit)> ChangedBitList(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur)
    {
        var list = new List<(int, int)>();
        var x = ChangedBits(prev, cur);
        for (int i = 0; i < x.Length; i++)
            for (int b = 0; b < 8; b++)
                if ((x[i] & (1 << b)) != 0) list.Add((i, b));
        return list;
    }
}
