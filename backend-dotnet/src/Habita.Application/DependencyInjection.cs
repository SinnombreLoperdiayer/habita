using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Habita.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddHabitaApplication(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ProcessingOptions>(config.GetSection(ProcessingOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RecordingAi>();
        services.AddSingleton<RecordingService>();
        services.AddSingleton<AccessService>();
        return services;
    }
}
