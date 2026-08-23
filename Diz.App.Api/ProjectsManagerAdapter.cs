using Diz.Controllers.interfaces;
using Diz.Core.model;

namespace Diz.App.Api;

public class ProjectsManagerAdapter : ICurrentProjectProvider
{
    public Project? CurrentProject { get; private set; }
    public event EventHandler<Project?>? ProjectChanged;

    public ProjectsManagerAdapter(IProjectController projectController)
    {
        CurrentProject = projectController.Project;
        projectController.ProjectChanged += OnProjectChanged;
    }

    private void OnProjectChanged(object sender, IProjectController.ProjectChangedEventArgs e)
    {
        if (e.ChangeType is IProjectController.ProjectChangedEventArgs.ProjectChangedType.Closing)
        {
            CurrentProject = null;
            ProjectChanged?.Invoke(this, null);
            return;
        }

        if (e.Project == null) return;
        CurrentProject = e.Project;
        ProjectChanged?.Invoke(this, e.Project);
    }
}
