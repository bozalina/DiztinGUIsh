#nullable enable

using System;
using System.Windows.Forms;
using Diz.App.Api;
using Diz.Ui.Winforms.window;

namespace Diz.App.Winforms.ApiIntegration;

public class WinformsViewRefreshRequester : IViewRefreshRequester
{
    private readonly Lazy<MainWindow> _window;

    public WinformsViewRefreshRequester(Lazy<MainWindow> window)
    {
        _window = window;
    }

    public void RequestRefresh()
    {
        // The main grid is a virtual-mode DataGridView (CellValueNeeded), so invalidating
        // the window re-pulls visible cell values — the same effect as the app's own
        // InvalidateTable(). RequestRefresh is always called from inside
        // IUiThreadDispatcher.InvokeAsync, so we're already on the UI thread here.
        ((Control)_window.Value).Invalidate(true);
    }
}
