using PadMapper.Core.Devices;
using PadMapper.Core.Model;

namespace PadMapper.Core.Engine;

public sealed record RawReport(string DevicePath, byte[] Data, double TimestampMs);

public sealed record ConnectionChange(bool Connected, HidDeviceInfo? Device, string? DefinitionName);

/// <summary>
/// コントローラーの入力元。ハードウェア依存の部分（Raw Input / HID の直接読み取り）はこの裏に隠し、テストではモックを使う。
/// </summary>
public interface IControllerSource : IDisposable
{
    /// <summary>解析済みの状態（入力元のスレッドから呼ばれる）。</summary>
    event Action<ControllerState>? StateReceived;
    /// <summary>主コントローラーの接続・切断。</summary>
    event Action<ConnectionChange>? ConnectionChanged;
    /// <summary>診断用の生レポート（購読しているときだけ通知）。</summary>
    event Action<RawReport>? RawReportReceived;

    void Start();
    void Stop();
}
