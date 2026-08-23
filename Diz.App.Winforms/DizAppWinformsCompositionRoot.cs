using Diz.App.Api;
using Diz.App.Common;
using Diz.App.Winforms.ApiIntegration;
using Diz.Controllers.controllers;
using Diz.Controllers.interfaces;
using Diz.Core.Interfaces;
using Diz.Ui.Winforms;
using Diz.Ui.Winforms.dialogs;
using Diz.Ui.Winforms.window;
using JetBrains.Annotations;
using LightInject;

namespace Diz.App.Winforms;

[UsedImplicitly] public class DizAppWinformsCompositionRoot : ICompositionRoot
{
    public void Compose(IServiceRegistry serviceRegistry)
    {
        serviceRegistry.RegisterFrom<DizAppCommonCompositionRoot>();
        serviceRegistry.RegisterFrom<DizApiCompositionRoot>();

        serviceRegistry.Register<IDizApp, DizWinformsApp>();
        serviceRegistry.Register<ICommonGui, WinFormsCommonGui>();
        serviceRegistry.Register<IAppVersionInfo, AppVersionInfo>();

        // The embedded HTTP API needs the live main window. Mirror the old fork's wiring:
        // register MainWindow as a singleton and forward the IMainGridWindowView "MainGridWindowView"
        // registration to it (overriding DizUiWinformsCompositionRoot's transient registration of the
        // same service+name, since RegisterWinformsServices.RegisterDizUiServices registers this
        // composition root AFTER DizUiWinformsCompositionRoot -- last registration wins in LightInject)
        // so the running window and the API adapters share one instance.
        serviceRegistry.RegisterSingleton<MainWindow>();
        serviceRegistry.Register<IMainGridWindowView>(
            factory => factory.GetInstance<MainWindow>(), "MainGridWindowView");

        serviceRegistry.RegisterSingleton<ISelectionProvider, WinformsSelectionProvider>();
        serviceRegistry.RegisterSingleton<IUiThreadDispatcher, WinformsUiThreadDispatcher>();
        serviceRegistry.RegisterSingleton<IViewRefreshRequester, WinformsViewRefreshRequester>();
    }
}