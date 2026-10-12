// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using Avalonia.Controls;
using Avalonia.Interactivity;
using CaptureSpike.Core;

namespace CaptureSpike.App;

public partial class AddSourceWindow : Window
{
    private bool _labelEdited;
    private bool _suppressLabelChanged;

    public AddSourceWindow()
    {
        InitializeComponent();
        KindBox.ItemsSource = Enum.GetValues<SourceKind>();
        KindBox.SelectedIndex = 0;
        KindBox.SelectionChanged += OnKindChanged;
        LabelBox.TextChanged += OnLabelChanged;
        OnKindChanged(null, null);
    }

    private void OnKindChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (_labelEdited || KindBox.SelectedItem is not SourceKind kind) return;
        _suppressLabelChanged = true;
        LabelBox.Text = kind.DefaultLabel();
        _suppressLabelChanged = false;
    }

    private void OnLabelChanged(object? sender, TextChangedEventArgs? e)
    {
        if (!_suppressLabelChanged) _labelEdited = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var kind = KindBox.SelectedItem as SourceKind? ?? SourceKind.Screen;
        var label = LabelBox.Text?.Trim();
        if (string.IsNullOrEmpty(label)) label = kind.DefaultLabel();
        var device = DeviceBox.Text?.Trim();
        Close(new ProjectSource
        {
            Kind = kind,
            Label = label,
            Device = string.IsNullOrEmpty(device) ? null : device,
        });
    }
}
