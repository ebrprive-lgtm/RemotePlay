using Microsoft.Extensions.DependencyInjection;
using RemotePlay.Abstractions.Services;
using RemotePlay.Services;
using RemotePlay.ViewModels;

namespace RemotePlay.DependencyInjection;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRemotePlayApplication(this IServiceCollection services)
    {
        services.AddSingleton<IAppConfigService, AppConfigService>();
        services.AddSingleton<IAppConfigFactory, AppConfigFactory>();
        services.AddSingleton<ISettingsValidationService, SettingsValidationService>();
        services.AddSingleton<ISettingsApplyService, SettingsApplyService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton(sp => new MainWindow(
            sp.GetRequiredService<IAppConfigService>(),
            sp.GetRequiredService<IAppConfigFactory>(),
            sp.GetRequiredService<ISettingsValidationService>(),
            sp.GetRequiredService<ISettingsApplyService>(),
            sp.GetRequiredService<MainWindowViewModel>()));

        return services;
    }
}
