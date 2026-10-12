// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Serialises tests in <see cref="GlobalPaletteTests"/> because they
/// share the CAPTURESPIKE_PALETTE_DIR process-global environment
/// variable. Without this, parallel test execution causes the tests
/// to clobber each other's palette directory.
/// </summary>
[CollectionDefinition(nameof(GlobalPaletteCollection), DisableParallelization = true)]
public sealed class GlobalPaletteCollection
{
    public static readonly object Sync = new();
}
