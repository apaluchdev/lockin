using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LockIn.Core.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers <typeparamref name="T"/> as a singleton that is also started and stopped by the host.</summary>
    public static IServiceCollection AddHostedSingleton<T>(this IServiceCollection services)
        where T : class, IHostedService
    {
        services.AddSingleton<T>();
        services.AddHostedService(sp => sp.GetRequiredService<T>());
        return services;
    }
}
