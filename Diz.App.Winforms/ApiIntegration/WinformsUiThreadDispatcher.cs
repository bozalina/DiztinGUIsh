#nullable enable

using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Diz.App.Api;
using Diz.Ui.Winforms.window;

namespace Diz.App.Winforms.ApiIntegration;

/// <summary>
/// Marshals API work onto the WinForms UI thread. The main window is a <see cref="Form"/>,
/// so we use its <see cref="Control.BeginInvoke(Delegate)"/> pump — the analogue of
/// Avalonia's <c>Dispatcher.UIThread.InvokeAsync</c>.
/// </summary>
public class WinformsUiThreadDispatcher : IUiThreadDispatcher
{
    private readonly Lazy<MainWindow> _window;

    public WinformsUiThreadDispatcher(Lazy<MainWindow> window)
    {
        _window = window;
    }

    private Control Target => _window.Value;

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var control = Target;
        if (!control.InvokeRequired)
            return Task.FromResult(func());

        var tcs = new TaskCompletionSource<T>();
        control.BeginInvoke(new Action(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }));
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        var control = Target;
        if (!control.InvokeRequired)
            return func();

        var tcs = new TaskCompletionSource<T>();
        control.BeginInvoke(new Action(async () =>
        {
            try { tcs.SetResult(await func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }));
        return tcs.Task;
    }

    public Task InvokeAsync(Action action)
    {
        var control = Target;
        if (!control.InvokeRequired)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        control.BeginInvoke(new Action(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }));
        return tcs.Task;
    }
}
