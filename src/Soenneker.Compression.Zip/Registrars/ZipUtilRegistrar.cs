using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Compression.Zip.Abstract;
using Soenneker.Utils.Directory.Registrars;
using Soenneker.Utils.File.Registrars;

namespace Soenneker.Compression.Zip.Registrars;

/// <summary>
/// Registration extensions for ZIP compression and extraction services.
/// </summary>
public static class ZipUtilRegistrar
{
    /// <summary>
    /// Adds <see cref="IZipUtil"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddZipUtilAsSingleton(this IServiceCollection services)
    {
        services.AddDirectoryUtilAsSingleton()
                .AddFileUtilAsSingleton()
                .TryAddSingleton<IZipUtil, ZipUtil>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="IZipUtil"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddZipUtilAsScoped(this IServiceCollection services)
    {
        services.AddDirectoryUtilAsScoped()
                .AddFileUtilAsScoped()
                .TryAddScoped<IZipUtil, ZipUtil>();

        return services;
    }
}
