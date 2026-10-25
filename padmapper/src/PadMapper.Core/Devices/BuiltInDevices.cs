namespace PadMapper.Core.Devices;

/// <summary>
/// 組み込みのデバイス定義。
/// Vader 4 Pro の値は SDL（src/joystick/hidapi/SDL_hidapi_flydigi.c の V1 プロトコル）を読んで作ったもので、
/// <b>実機では未検証</b>。診断画面のウィザードで実機から取り直した定義があれば、そちらが優先される。
/// 詳細は docs/vader4pro-report.md。
/// </summary>
public static class BuiltInDevices
{
    public const int FlydigiVendorV1 = 0x04B4;
    public const int FlydigiVendorV2 = 0x37D7;
    public const int Vader4ProProductId = 0x2412;

    // SDL の換算（ピッチ・ヨーは int16 の全範囲が ±72000 deg/s、ロールは ±1200 deg/s。SDL 側でも推定値とされている）
    private const double PitchYawScale = 72000.0 / 32767.0;
    private const double RollScale = 1200.0 / 32767.0;
    private const double AccelScale = 1.0 / 256.0;

    public static DeviceDefinition Vader4ProV1() => new()
    {
        Id = "builtin-flydigi-v1",
        Name = "Flydigi Vader 4 Pro（V1 独自レポート・SDL 由来）",
        Source = "builtin-sdl",
        Verified = false,
        Match = new DeviceMatch { VendorId = FlydigiVendorV1, ProductId = Vader4ProProductId },
        ReportId = 0x04,
        MinLength = 31,
        Signature = { new ByteValue { Offset = 1, Value = 0xFE } },
        Buttons =
        {
            // data[7]: C, Z, 背面 M1〜M4
            B("C", 7, 0x01), B("Z", 7, 0x02),
            B("M1", 7, 0x04), B("M2", 7, 0x08), B("M3", 7, 0x10), B("M4", 7, 0x20),
            // data[8]: ホーム（Guide）、+（Flydigi の本体ジャイロ切替ボタン）
            B("Guide", 8, 0x08), B("Plus", 8, 0x01),
            // data[9]: 十字キー（下位4ビット）、A, B, Back, X
            B("DPadUp", 9, 0x01), B("DPadRight", 9, 0x02), B("DPadDown", 9, 0x04), B("DPadLeft", 9, 0x08),
            B("A", 9, 0x10), B("B", 9, 0x20), B("Back", 9, 0x40), B("X", 9, 0x80),
            // data[10]: Y, Start, LB, RB, L3, R3
            B("Y", 10, 0x01), B("Start", 10, 0x02), B("LB", 10, 0x04), B("RB", 10, 0x08),
            B("L3", 10, 0x40), B("R3", 10, 0x80),
        },
        Axes =
        {
            new AxisMap { Axis = "LX", Offset = 17, Format = "u8", Center = 0x7F, Min = 0, Max = 255 },
            new AxisMap { Axis = "LY", Offset = 19, Format = "u8", Center = 0x7F, Min = 0, Max = 255 },
            new AxisMap { Axis = "RX", Offset = 21, Format = "u8", Center = 0x7F, Min = 0, Max = 255 },
            new AxisMap { Axis = "RY", Offset = 22, Format = "u8", Center = 0x7F, Min = 0, Max = 255 },
            new AxisMap { Axis = "LT", Offset = 23, Format = "u8", Min = 0, Max = 255 },
            new AxisMap { Axis = "RT", Offset = 24, Format = "u8", Min = 0, Max = 255 },
        },
        Gyro =
        {
            new SensorMap { Axis = "pitch", Offset = 26, OffsetHi = 27, Scale = -PitchYawScale },
            new SensorMap { Axis = "yaw", Offset = 18, OffsetHi = 20, Scale = -PitchYawScale },
            new SensorMap { Axis = "roll", Offset = 29, OffsetHi = 30, Scale = -RollScale },
        },
        Accel =
        {
            new SensorMap { Axis = "x", Offset = 11, OffsetHi = 12, Scale = -AccelScale },
            new SensorMap { Axis = "y", Offset = 15, OffsetHi = 16, Scale = AccelScale },
            new SensorMap { Axis = "z", Offset = 13, OffsetHi = 14, Scale = AccelScale },
        },
        // SDL は接続時に情報取得コマンド（レポート 0x05, 0xEC）を送っている。入力に必要かは未確認のため既定では送らない。
        InitCommandHex = "05EC",
        Notes = "SDL の V1 プロトコル（Vader 4 Pro は独自プロトコルをインターフェース 2 に持つ）から作成。実機未検証。",
    };

    private static ButtonMap B(string input, int offset, int mask) => new() { Input = input, Offset = offset, Mask = mask };

    public static IReadOnlyList<DeviceDefinition> All() => new[] { Vader4ProV1() };
}
