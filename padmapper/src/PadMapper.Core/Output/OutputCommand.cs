using PadMapper.Core.Profiles;

namespace PadMapper.Core.Output;

public enum OutputKind { KeyDown, KeyUp, MouseDown, MouseUp, MouseMove, Scroll }

/// <summary>出力層に渡す命令。キーは KeyCatalog の正式名。</summary>
public readonly record struct OutputCommand(OutputKind Kind, string? Key = null, MouseButton Button = MouseButton.Left,
    int Dx = 0, int Dy = 0, int Wheel = 0, bool Horizontal = false)
{
    public static OutputCommand KeyDown(string key) => new(OutputKind.KeyDown, key);
    public static OutputCommand KeyUp(string key) => new(OutputKind.KeyUp, key);
    public static OutputCommand MouseDown(MouseButton b) => new(OutputKind.MouseDown, Button: b);
    public static OutputCommand MouseUp(MouseButton b) => new(OutputKind.MouseUp, Button: b);
    public static OutputCommand Move(int dx, int dy) => new(OutputKind.MouseMove, Dx: dx, Dy: dy);
    /// <summary>wheel は WHEEL_DELTA（1ノッチ=120）単位。</summary>
    public static OutputCommand ScrollBy(int wheel, bool horizontal) => new(OutputKind.Scroll, Wheel: wheel, Horizontal: horizontal);

    public override string ToString() => Kind switch
    {
        OutputKind.KeyDown or OutputKind.KeyUp => $"{Kind}({Key})",
        OutputKind.MouseDown or OutputKind.MouseUp => $"{Kind}({Button})",
        OutputKind.MouseMove => $"Move({Dx},{Dy})",
        _ => $"Scroll({Wheel},{(Horizontal ? "H" : "V")})",
    };
}

/// <summary>
/// 出力先。現在の実装は SendInput（キーボード/マウス）のみ。
/// 将来の仮想パッド出力（Phase 7）は、このインターフェースの実装を追加するだけで足せるようにしている。
/// </summary>
public interface IOutputSink
{
    void Send(IReadOnlyList<OutputCommand> commands);
}

/// <summary>複数の出力先に同じ命令を送る。</summary>
public sealed class CompositeSink : IOutputSink
{
    private readonly IOutputSink[] _sinks;
    public CompositeSink(params IOutputSink[] sinks) => _sinks = sinks;
    public void Send(IReadOnlyList<OutputCommand> commands)
    {
        foreach (var s in _sinks) s.Send(commands);
    }
}

/// <summary>テストや診断用：送った命令を記録するだけ。</summary>
public sealed class RecordingSink : IOutputSink
{
    private readonly object _lock = new();
    public List<OutputCommand> Commands { get; } = new();
    public void Send(IReadOnlyList<OutputCommand> commands)
    {
        lock (_lock) Commands.AddRange(commands);
    }
    public void Clear()
    {
        lock (_lock) Commands.Clear();
    }
}
