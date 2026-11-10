using System.Windows.Media;
using FinanceOS.UI.Theming;
using Xunit;
using static FinanceOS.UI.Theming.ColorMath;

namespace FinanceOS.UI.Tests;

public sealed class ThemingTests
{
    [Fact]
    public void Contrast_matches_wcag_reference_values()
    {
        Assert.Equal(21.0, Contrast(Colors.Black, Colors.White), precision: 2);
        Assert.Equal(1.0, Contrast(Colors.White, Colors.White), precision: 2);
        // #767676 on white is the classic "just passes AA" gray (4.54:1).
        Assert.Equal(4.54, Contrast(FromHex("#767676"), Colors.White), precision: 2);
    }

    [Theory]
    [InlineData("#4F46E5")]
    [InlineData("#00000000")]
    [InlineData("#66101318")]
    public void Hex_round_trips(string hex) => Assert.Equal(hex, ToHex(FromHex(hex)));

    [Fact]
    public void Mix_returns_endpoints_and_clamps()
    {
        var a = FromHex("#000000");
        var b = FromHex("#FFFFFF");
        Assert.Equal(a, Mix(a, b, 0));
        Assert.Equal(b, Mix(a, b, 1));
        Assert.Equal(b, Mix(a, b, 3));
        Assert.Equal(FromHex("#808080"), Mix(a, b, 0.5));
    }

    [Fact]
    public void Accent_ids_are_unique_and_unknown_ids_fall_back_to_default()
    {
        Assert.Equal(Accents.All.Count, Accents.All.Select(a => a.Id).Distinct().Count());
        Assert.Equal(Accents.Default, Accents.Find("does-not-exist"));
        Assert.Equal(Accents.Default, Accents.Find(null));
        Assert.Equal("teal", Accents.Find("teal").Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Built_resources_expose_frozen_brush_and_color_for_every_token(bool dark)
    {
        var palette = dark ? ThemePalette.Dark : ThemePalette.Light;
        var accent = AccentColors.Derive(Accents.Default, palette);
        var resources = ThemeResources.Build(palette, accent);

        var names = ThemeResources.Enumerate(palette).Concat(ThemeResources.Enumerate(accent)).Select(t => t.Name).ToList();
        Assert.Contains("Surface", names);
        Assert.Contains("OnAccent", names);
        foreach (var name in names)
        {
            var brush = Assert.IsType<SolidColorBrush>(resources[$"Brush.{name}"]);
            Assert.True(brush.IsFrozen, $"Brush.{name} must be frozen");
            Assert.IsType<Color>(resources[$"Color.{name}"]);
        }

        Assert.NotNull(resources["Effect.Elevation1"]);
        Assert.NotNull(resources["Effect.Elevation3"]);
    }

    [Fact]
    public void Light_and_dark_palettes_define_the_same_tokens()
    {
        var light = ThemeResources.Enumerate(ThemePalette.Light).Select(t => t.Name);
        var dark = ThemeResources.Enumerate(ThemePalette.Dark).Select(t => t.Name);
        Assert.Equal(light, dark);
    }
}
