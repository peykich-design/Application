namespace PadMapper.Core.Model;

/// <summary>
/// コントローラーの入力を機種に関係なく表す論理名。
/// LT/RT はアナログ値をしきい値で判定したデジタル入力、LT2/RT2 は2段階目（深押し）。
/// LSUp などはスティックを倒した方向を「ボタン」として扱うための仮想入力（十字キー化に使う）。
/// </summary>
public enum LogicalInput
{
    A, B, X, Y,
    LB, RB, LT, RT,
    L3, R3,
    Back, Start, Guide,
    DPadUp, DPadDown, DPadLeft, DPadRight,
    M1, M2, M3, M4,
    C, Z,
    Plus,
    LT2, RT2,
    LSUp, LSDown, LSLeft, LSRight,
    RSUp, RSDown, RSLeft, RSRight,
}

public static class LogicalInputs
{
    public static readonly LogicalInput[] All = Enum.GetValues<LogicalInput>();

    /// <summary>実際のボタン（仮想入力を除く）。診断ウィザードやコントローラー図で使う。</summary>
    public static readonly LogicalInput[] PhysicalButtons =
    {
        LogicalInput.A, LogicalInput.B, LogicalInput.X, LogicalInput.Y,
        LogicalInput.LB, LogicalInput.RB, LogicalInput.L3, LogicalInput.R3,
        LogicalInput.Back, LogicalInput.Start, LogicalInput.Guide, LogicalInput.Plus,
        LogicalInput.DPadUp, LogicalInput.DPadDown, LogicalInput.DPadLeft, LogicalInput.DPadRight,
        LogicalInput.M1, LogicalInput.M2, LogicalInput.M3, LogicalInput.M4,
        LogicalInput.C, LogicalInput.Z,
    };

    /// <summary>背面ボタンなど、ゲームで使われにくいボタン。ゲーム型プロファイルで二重入力の警告を出さない。</summary>
    public static bool IsExtraButton(LogicalInput i) =>
        i is LogicalInput.M1 or LogicalInput.M2 or LogicalInput.M3 or LogicalInput.M4
          or LogicalInput.C or LogicalInput.Z or LogicalInput.Plus;

    public static bool IsVirtual(LogicalInput i) => i >= LogicalInput.LSUp;

    public static bool TryParse(string? name, out LogicalInput input)
    {
        input = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Enum.TryParse(name.Trim(), ignoreCase: true, out input) && Enum.IsDefined(input);
    }
}

/// <summary>押されている入力の集合（64ビットのビットセット）。</summary>
public readonly record struct InputSet(ulong Bits)
{
    public static readonly InputSet Empty = new(0);

    public bool Has(LogicalInput i) => (Bits & (1UL << (int)i)) != 0;
    public InputSet With(LogicalInput i) => new(Bits | (1UL << (int)i));
    public InputSet Without(LogicalInput i) => new(Bits & ~(1UL << (int)i));
    public InputSet Set(LogicalInput i, bool on) => on ? With(i) : Without(i);
    public bool IsEmpty => Bits == 0;

    public static InputSet operator &(InputSet a, InputSet b) => new(a.Bits & b.Bits);
    public static InputSet operator |(InputSet a, InputSet b) => new(a.Bits | b.Bits);
    public static InputSet operator ~(InputSet a) => new(~a.Bits);

    public static InputSet Of(params LogicalInput[] inputs)
    {
        ulong b = 0;
        foreach (var i in inputs) b |= 1UL << (int)i;
        return new InputSet(b);
    }

    public IEnumerable<LogicalInput> Items()
    {
        foreach (var i in LogicalInputs.All)
            if (Has(i)) yield return i;
    }

    public override string ToString() => string.Join("+", Items());
}
