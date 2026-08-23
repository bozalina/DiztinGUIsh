using Diz.Core.model;

namespace Diz.App.Api;

public interface ICurrentProjectProvider
{
    Project? CurrentProject { get; }
    event EventHandler<Project?> ProjectChanged;
}

public interface ISelectionProvider
{
    int SelectedPcOffset { get; set; }
    event EventHandler<int> SelectionChanged;
}

public interface IUiThreadDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> func);
    Task<T> InvokeAsync<T>(Func<Task<T>> func);
    Task InvokeAsync(Action action);
}

public interface IViewRefreshRequester
{
    void RequestRefresh();
}
