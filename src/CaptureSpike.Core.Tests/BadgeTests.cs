// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core;
using Xunit;

public sealed class BadgeTests
{
    [Fact]
    public void EmptyBadge_FallsBackToKindBadge()
    {
        var mic = new ProjectSource { Kind = SourceKind.Microphone };
        Assert.Equal("MI", mic.EffectiveBadge());
    }

    [Fact]
    public void CustomBadge_IsUsedVerbatim_UpperCased()
    {
        var mic = new ProjectSource { Kind = SourceKind.Microphone, Badge = "mk" };
        Assert.Equal("MK", mic.EffectiveBadge());
    }

    [Fact]
    public void LegacyProjectJson_WithoutBadge_DeserializesToEmpty()
    {
        // Pre-badge project.json files have no "badge" key: the kind badge must survive.
        var json = """{"id":"abc","kind":"Microphone","label":"Mic","device":null}""";
        var src = System.Text.Json.JsonSerializer.Deserialize<ProjectSource>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.NotNull(src);
        Assert.Equal("", src!.Badge);
        Assert.Equal("MI", src.EffectiveBadge());
    }
}
