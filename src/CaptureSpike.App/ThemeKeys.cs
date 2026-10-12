// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using Avalonia.Styling;

namespace CaptureSpike.App;

/// <summary>
/// Theme keys shared between the XAML resource dictionaries in App.axaml
/// and the C# brush lookups in TiledPlaybackWindow. Keep these names in
/// sync with the x:Key values in App.axaml's ThemeDictionaries.
/// </summary>
public static class ThemeKeys
{
    public const string BadgeBackground = "BadgeBackgroundBrush";
    public const string BadgeForeground = "BadgeForegroundBrush";
    public const string Divider = "DividerBrush";
    public const string MutedTile = "MutedTileBrush";
    public const string MutedTileMuted = "MutedTileMutedBrush";
    public const string TileBorder = "TileBorderBrush";
    public const string TileBorderMuted = "TileBorderMutedBrush";

    /// <summary>Maps the persisted string ("Dark"/"Light"/"Default") to Avalonia's ThemeVariant.</summary>
    public static ThemeVariant ToVariant(string? choice) => choice switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        // "Default" and null both mean "follow the OS" — Avalonia's
        // ThemeVariant.Default does that.
        _ => ThemeVariant.Default,
    };

    /// <summary>Inverse of ToVariant, for displaying the current value in the Settings UI.</summary>
    public static string FromVariant(ThemeVariant variant) => variant.Key switch
    {
        var k when k == ThemeVariant.Light.Key => "Light",
        var k when k == ThemeVariant.Dark.Key => "Dark",
        _ => "Default",
    };
}