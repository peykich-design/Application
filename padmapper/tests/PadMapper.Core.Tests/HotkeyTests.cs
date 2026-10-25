using PadMapper.Core.Output;

namespace PadMapper.Core.Tests;

public class HotkeyTests
{
    [Fact]
    public void Parses_ModifiersAndKey()
    {
        Assert.True(Hotkey.TryParse("ctrl+alt+shift+f12", out var hk));
        Assert.True(hk.Ctrl && hk.Alt && hk.Shift && !hk.Win);
        Assert.Equal("F12", hk.Key);
        Assert.Equal("Ctrl+Alt+Shift+F12", hk.ToString());
    }

    [Theory]
    [InlineData("F12")]          // 修飾キーなし
    [InlineData("Ctrl+")]        // キーなし
    [InlineData("Ctrl+A+B")]     // キーが2つ
    [InlineData("Ctrl+Nope")]    // 不明なキー
    [InlineData("")]
    public void Rejects_Invalid(string text) => Assert.False(Hotkey.TryParse(text, out _));
}
