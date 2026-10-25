using PadMapper.Core.Devices;

namespace PadMapper.Core.Diagnostics;

/// <summary>
/// 「ボタンを押して、変化したビットを割り当てる」ウィザードの判定ロジック。
/// 1. 何も触らない状態のレポートを AddIdle で集める（ノイズのあるビット・バイトを除外するため）
/// 2. 対象を操作している間のレポートを AddActive で集める
/// 3. LearnButton / LearnAxis / LearnSensor で、もっとも変化した位置を返す
/// </summary>
public sealed class MappingLearner
{
    private readonly List<byte[]> _idle = new();
    private readonly List<byte[]> _active = new();

    public int IdleCount => _idle.Count;
    public int ActiveCount => _active.Count;

    public void AddIdle(ReadOnlySpan<byte> report) => _idle.Add(report.ToArray());
    public void AddActive(ReadOnlySpan<byte> report) => _active.Add(report.ToArray());

    public void Reset()
    {
        _idle.Clear();
        _active.Clear();
    }

    public void ResetActive() => _active.Clear();

    private int Length => Math.Min(_idle.Count == 0 ? 0 : _idle.Min(r => r.Length), _active.Count == 0 ? 0 : _active.Min(r => r.Length));

    /// <summary>何も触っていない間に一度も変化しなかったビット（バイトごと）。</summary>
    private byte[] IdleStableBits(int length)
    {
        var and = Enumerable.Repeat((byte)0xFF, length).ToArray();
        var or = new byte[length];
        foreach (var r in _idle)
            for (int i = 0; i < length; i++)
            {
                and[i] &= r[i];
                or[i] |= r[i];
            }
        var stable = new byte[length];
        for (int i = 0; i < length; i++) stable[i] = (byte)~(and[i] ^ or[i]);
        return stable;
    }

    public ButtonMap? LearnButton(string input, ISet<int>? excludeOffsets = null)
    {
        int len = Length;
        if (len == 0 || _idle.Count < 2 || _active.Count == 0) return null;
        var stable = IdleStableBits(len);
        var idle = _idle[^1];

        // バイトごとに、アイドル時と違う値になったサンプル数を数える
        int bestOffset = -1, bestCount = 0;
        for (int i = 0; i < len; i++)
        {
            if (excludeOffsets?.Contains(i) == true || stable[i] == 0) continue;
            // 何通りもの値をとるバイトはスティックなどのアナログ値なので、ボタンの候補にしない
            if (_idle.Concat(_active).Select(r => r[i]).Distinct().Count() > 3) continue;
            int count = _active.Count(r => ((r[i] ^ idle[i]) & stable[i]) != 0);
            if (count > bestCount)
            {
                bestCount = count;
                bestOffset = i;
            }
        }
        int minSamples = Math.Max(2, _active.Count / 5);
        if (bestOffset < 0 || bestCount < minSamples) return null;

        // 変化したビットと、押下時にいちばん多かった値
        var changedSamples = _active.Where(r => ((r[bestOffset] ^ idle[bestOffset]) & stable[bestOffset]) != 0).ToList();
        int mask = 0;
        foreach (var r in changedSamples) mask |= (r[bestOffset] ^ idle[bestOffset]) & stable[bestOffset];
        bool singleBit = (mask & (mask - 1)) == 0;
        if (!singleBit)
        {
            // ハットスイッチは4ビットの値なので、変化したビットを含むニブル全体で判定する
            mask = ((mask & 0x0F) != 0 ? 0x0F : 0) | ((mask & 0xF0) != 0 ? 0xF0 : 0);
        }
        int pressedValue = changedSamples
            .GroupBy(r => r[bestOffset] & mask)
            .OrderByDescending(g => g.Count())
            .First().Key;

        if (singleBit && (idle[bestOffset] & mask) == 0)
            return new ButtonMap { Input = input, Offset = bestOffset, Mask = mask };
        // 複数ビット（十字キーのハット値など）や、離すと 1 になるビットは値で判定する
        return new ButtonMap { Input = input, Offset = bestOffset, Mask = mask, Value = pressedValue };
    }

    /// <summary>スティック・トリガー。format は u8 / s8 / u16 / s16。</summary>
    public AxisMap? LearnAxis(string axis, string format = "u8", ISet<int>? excludeOffsets = null)
    {
        int len = Length;
        if (len == 0 || _idle.Count < 2 || _active.Count < 2) return null;
        bool wide = format is "u16" or "s16";
        double full = wide ? 65535 : 255;
        int last = wide ? len - 1 : len;

        int best = -1;
        double bestRange = 0, bestCenter = 0, bestMin = 0, bestMax = 0;
        for (int i = 0; i < last; i++)
        {
            if (excludeOffsets?.Contains(i) == true || (wide && excludeOffsets?.Contains(i + 1) == true)) continue;
            var idleVals = _idle.Select(r => DeviceParser.Read(r, i, null, format)).ToList();
            double idleRange = idleVals.Max() - idleVals.Min();
            if (idleRange > full * 0.05) continue; // 何もしなくても揺れる位置（ジャイロ等）は除外
            var vals = _active.Select(r => DeviceParser.Read(r, i, null, format)).ToList();
            double range = vals.Max() - vals.Min();
            if (range > bestRange)
            {
                best = i;
                bestRange = range;
                bestCenter = idleVals.Average();
                bestMin = vals.Min();
                bestMax = vals.Max();
            }
        }
        if (best < 0 || bestRange < full * 0.25) return null;

        bool trigger = axis.ToUpperInvariant() is "LT" or "RT";
        if (trigger)
        {
            // アイドル値を 0、いちばん離れた値を 1 とする（逆向きのトリガーもそのまま扱える）
            double far = Math.Abs(bestMax - bestCenter) >= Math.Abs(bestMin - bestCenter) ? bestMax : bestMin;
            return new AxisMap { Axis = axis, Offset = best, Format = format, Min = Math.Round(bestCenter), Max = far };
        }
        return new AxisMap
        {
            Axis = axis, Offset = best, Format = format,
            Center = Math.Round(bestCenter), Min = bestMin, Max = bestMax,
        };
    }

    /// <summary>ジャイロ・加速度（16ビット符号付きを想定）。回転させている間にいちばん分散が大きい位置を選ぶ。</summary>
    public SensorMap? LearnSensor(string axis, double scale, ISet<int>? excludeOffsets = null)
    {
        int len = Length;
        if (len < 2 || _active.Count < 4) return null;
        // 候補ごとに「変化の大きさ」と「滑らかさ」を求める。
        // 1バイトずれた組み合わせは下位バイトが上位に来るため値が大きく飛ぶ（滑らかでない）ので除外できる。
        var candidates = new List<(int offset, double var, double smooth)>();
        for (int i = 0; i < len - 1; i++)
        {
            if (excludeOffsets?.Contains(i) == true || excludeOffsets?.Contains(i + 1) == true) continue;
            var values = _active.Select(r => DeviceParser.Read(r, i, null, "s16")).ToList();
            double activeVar = Variance(values);
            double idleVar = _idle.Count > 1 ? Variance(_idle.Select(r => DeviceParser.Read(r, i, null, "s16"))) : 0;
            double v = activeVar - idleVar;
            if (v <= 0) continue;
            double msd = 0;
            for (int k = 1; k < values.Count; k++) msd += (values[k] - values[k - 1]) * (values[k] - values[k - 1]);
            msd /= values.Count - 1;
            candidates.Add((i, v, activeVar / (msd + 1)));
        }
        if (candidates.Count == 0) return null;
        double maxVar = candidates.Max(c => c.var);
        int best = candidates.Where(c => c.var >= maxVar * 0.01).OrderByDescending(c => c.smooth).First().offset;
        return new SensorMap { Axis = axis, Offset = best, OffsetHi = best + 1, Format = "s16", Scale = scale };
    }

    private static double Variance(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0;
        double mean = list.Average();
        return list.Sum(v => (v - mean) * (v - mean)) / list.Count;
    }
}
