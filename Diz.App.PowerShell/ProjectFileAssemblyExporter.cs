using Diz.Core;
using Diz.Core.model;
using Diz.Core.util;
using Diz.LogWriter;
using Diz.LogWriter.util;

namespace Diz.PowerShell;

public class ProjectFileAssemblyExporter : IProjectFileAssemblyExporter
{
    private readonly IDizLogger logger;
    private readonly IFilesystemService fs;
    private readonly IProjectFileOpener projectFileSource;

    public ProjectFileAssemblyExporter(IDizLogger logger, IProjectFileOpener projectFileSource, IFilesystemService fs)
    {
        this.logger = logger;
        this.projectFileSource = projectFileSource;
        this.fs = fs;
    }
    
    private Project? OpenProjectFile(string projectFileName)
    {
        var project = projectFileSource.ReadProjectFromFile(projectFileName);
        if (project == null)
        {
            logger.Error($"couldn't load project file: {projectFileName}");
            return null;
        }

        logger.Debug($"Loaded project, rom is: {project.AttachedRomFilename}");
        return project;
    }

    public bool ExportAssembly(string projectFileName)
    {
        var project = OpenProjectFile(projectFileName);
        return project != null && ExportAssembly(project);
    }

    public bool ExportAssembly(Project project)
    {
        var failReason = project.LogWriterSettings.Validate(fs);
        if (failReason != null)
        {
            logger.Error($"invalid assembly build settings {failReason}");
            return false;
        }

        var lc = new LogCreator
        {
            Settings = project.LogWriterSettings,
            Data = new LogCreatorByteSource(project.Data),
        };

        logger.Debug("Building....");
        var result = lc.CreateLog();

        if (!result.Success)
        {
            // FatalErrorMsg is where LogCreator.CreateLog() records the real failure
            // (LogOutput.cs). AssemblyOutputStr is only populated when Settings.OutputToString
            // is true, so logging it alone made every headless failure print a blank message.
            logger.Error($"Failed to build ({result.ErrorCount} error(s)): {result.FatalErrorMsg}");
            if (!string.IsNullOrWhiteSpace(result.ErrorsStr))
                logger.Error($"errors: {result.ErrorsStr}");
            if (!string.IsNullOrWhiteSpace(result.AssemblyOutputStr))
                logger.Error($"assembly output: {result.AssemblyOutputStr}");
            return false;
        }

        logger.Info("Successfully exported assembly output.");
        return true;
    }
}