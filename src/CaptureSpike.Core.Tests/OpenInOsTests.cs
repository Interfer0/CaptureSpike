// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests for <see cref="OpenInOs"/>.
/// The "real" call would actually launch xdg-open, the macOS open
/// command, or Windows explorer with shell-execute — none of which we
/// want firing inside the test process. These tests assert that the
/// wrapper's contract (null guard, doesn't throw, returns a bool) holds
/// without any platform-specific assumptions.
/// </summary>
public sealed class OpenInOsTests
{
    [Fact]
    public void Open_ThrowsArgumentNullException_OnNullPath()
    {
        Assert.Throws<System.ArgumentNullException>(() => OpenInOs.Open(null!));
    }

    [Fact]
    public void Open_ReturnsABool_NeverThrows_ForAValidTempPath()
    {
        // The temp dir always exists on every CI/dev box. The OS handler
        // may or may not be installed; either is fine. What matters is
        // that the wrapper doesn't propagate exceptions.
        var ex = Record.Exception(() => OpenInOs.Open(Path.GetTempPath()));
        Assert.Null(ex);
    }
}
