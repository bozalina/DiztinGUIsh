using JetBrains.Annotations;
using LightInject;

namespace Diz.App.Api;

[UsedImplicitly]
public class DizApiCompositionRoot : ICompositionRoot
{
    public void Compose(IServiceRegistry r)
    {
        r.RegisterSingleton<ICurrentProjectProvider, ProjectsManagerAdapter>();
        r.RegisterSingleton<DizApiService>();
        r.RegisterSingleton<DizApiServer>();
        // ISelectionProvider is intentionally NOT registered here
        // Each host app registers its own implementation
    }
}
