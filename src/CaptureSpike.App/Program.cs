// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using Avalonia;

namespace CaptureSpike.App;

internal static class Program
{
    // Held for the process lifetime. Released on exit.
    private static SingleInstance? _instance;

    [STAThread]
    public static void Main(string[] args)
    {
        // Refuse to run alongside another instance rather than let the
        // two overwrite each other's project.json / palette.json. See
        // SingleInstance for why this is a real corruption risk and not
        // just an untidiness.
        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            Console.Error.WriteLine(
                "CaptureSpike is already running. Close the other window first.");
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
