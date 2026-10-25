using PadMapper.Core.Output;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Engine;

/// <summary>
/// 押しっぱなしのキー・マウスボタンを参照カウントで管理する。
/// 複数のバインディングが同じキーを押しても、最後の1つが離すまでキーアップしない。
/// ReleaseAll で押されているものを必ず全て離す（プロファイル切替・一時停止・終了・切断時）。
/// </summary>
public sealed class OutputTracker
{
    private readonly Dictionary<string, int> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MouseButton, int> _mouse = new();
    private readonly List<OutputCommand> _pending = new();

    public int HeldCount => _keys.Count + _mouse.Count;
    public IEnumerable<string> HeldKeys => _keys.Keys;
    public IEnumerable<MouseButton> HeldButtons => _mouse.Keys;

    public void KeyDown(string key)
    {
        _keys.TryGetValue(key, out var n);
        _keys[key] = n + 1;
        if (n == 0) _pending.Add(OutputCommand.KeyDown(key));
    }

    public void KeyUp(string key)
    {
        if (!_keys.TryGetValue(key, out var n)) return;
        if (n <= 1)
        {
            _keys.Remove(key);
            _pending.Add(OutputCommand.KeyUp(key));
        }
        else _keys[key] = n - 1;
    }

    public void MouseDown(MouseButton b)
    {
        _mouse.TryGetValue(b, out var n);
        _mouse[b] = n + 1;
        if (n == 0) _pending.Add(OutputCommand.MouseDown(b));
    }

    public void MouseUp(MouseButton b)
    {
        if (!_mouse.TryGetValue(b, out var n)) return;
        if (n <= 1)
        {
            _mouse.Remove(b);
            _pending.Add(OutputCommand.MouseUp(b));
        }
        else _mouse[b] = n - 1;
    }

    public void Add(OutputCommand c) => _pending.Add(c);

    public void ReleaseAll()
    {
        foreach (var k in _keys.Keys.ToList()) _pending.Add(OutputCommand.KeyUp(k));
        foreach (var b in _mouse.Keys.ToList()) _pending.Add(OutputCommand.MouseUp(b));
        _keys.Clear();
        _mouse.Clear();
    }

    /// <summary>溜まった命令を取り出す。</summary>
    public List<OutputCommand> Drain()
    {
        var list = new List<OutputCommand>(_pending);
        _pending.Clear();
        return list;
    }

    public bool HasPending => _pending.Count > 0;
}

/// <summary>マウス移動・スクロールの小数部を累積し、整数になった分だけ送る（ジャイロの細かい動きを失わないため）。</summary>
public sealed class MouseAccumulator
{
    private double _x, _y, _wheelV, _wheelH;

    public (int dx, int dy) AddMove(double dx, double dy)
    {
        _x += dx;
        _y += dy;
        int ix = (int)Math.Truncate(_x);
        int iy = (int)Math.Truncate(_y);
        _x -= ix;
        _y -= iy;
        return (ix, iy);
    }

    /// <summary>notches はノッチ数（1ノッチ = 120）。戻り値は WHEEL_DELTA 単位。</summary>
    public int AddScroll(double notches, bool horizontal)
    {
        if (horizontal)
        {
            _wheelH += notches * 120;
            int w = (int)Math.Truncate(_wheelH);
            _wheelH -= w;
            return w;
        }
        _wheelV += notches * 120;
        int v = (int)Math.Truncate(_wheelV);
        _wheelV -= v;
        return v;
    }

    public void Reset() => _x = _y = _wheelV = _wheelH = 0;
}
