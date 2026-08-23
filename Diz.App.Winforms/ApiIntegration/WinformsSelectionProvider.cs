#nullable enable

using System;
using Diz.App.Api;
using Diz.Controllers.controllers;
using Diz.Ui.Winforms.window;

namespace Diz.App.Winforms.ApiIntegration;

public class WinformsSelectionProvider : ISelectionProvider
{
    private readonly Lazy<MainWindow> _window;

    // Declared by the interface but not consumed by DizApiService. WinForms exposes no
    // public selection-changed event to bridge without modifying the Diz.Ui.Winforms
    // submodule, so this stays unraised for now.
#pragma warning disable CS0067
    public event EventHandler<int>? SelectionChanged;
#pragma warning restore CS0067

    public WinformsSelectionProvider(Lazy<MainWindow> window)
    {
        _window = window;
    }

    public int SelectedPcOffset
    {
        get => _window.Value.SelectedOffset;
        // Call through ISnesNavigation: MainWindow has two SelectOffset overloads that both
        // accept a single int, so the interface method is the unambiguous one.
        set => ((ISnesNavigation)_window.Value).SelectOffset(value);
    }
}
