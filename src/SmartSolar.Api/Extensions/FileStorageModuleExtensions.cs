using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSolar.Infrastructure.Features;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Storage;
using SmartSolar.Modules.Files;

namespace SmartSolar.Api.Extensions;

/// <summary>
/// Shared by the Installation Flow and the knowledge base: feature flags and S3 object references
/// (the frontend uploads; the backend only verifies). Options are validated at startup.
/// </summary>
public static class FileStorageModuleExtensions
{
    public static IServiceCollection AddFeatureFlagsAndFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(FeatureConfiguration.Read(configuration));
        services.TryAddSingleton(TimeProvider.System);

        var storage = ConfigurationBinding.Bind<StorageOptions>(configuration, StorageOptions.SectionName);
        if (storage.S3.Enabled && storage.Buckets().Count == 0)
            throw new InvalidOperationException("'Storage:DefaultBucket' or 'Storage:AllowedBuckets' is required when 'Storage:S3:Enabled' is true.");
        if (storage.S3.PresignedUrlTtlSeconds is < S3VerificationOptions.MinPresignedUrlTtlSeconds or > S3VerificationOptions.MaxPresignedUrlTtlSeconds)
            throw new InvalidOperationException(
                $"'Storage:S3:PresignedUrlTtlSeconds' must be between {S3VerificationOptions.MinPresignedUrlTtlSeconds} and {S3VerificationOptions.MaxPresignedUrlTtlSeconds}.");
        if (!string.IsNullOrWhiteSpace(storage.S3.ServiceUrl)
            && (!Uri.TryCreate(storage.S3.ServiceUrl.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http")))
            throw new InvalidOperationException("'Storage:S3:ServiceUrl' must be an absolute https:// (or, for a local emulator, http://) URL.");
        services.AddSingleton(storage);
        if (storage.S3.Enabled)
        {
            services.AddSingleton<IStorageObjectVerifier, S3StorageObjectVerifier>();
            services.AddSingleton<IStorageUrlSigner, S3StorageUrlSigner>();
        }
        else
        {
            services.AddSingleton<IStorageObjectVerifier, DisabledStorageObjectVerifier>();
            services.AddSingleton<IStorageUrlSigner, DisabledStorageUrlSigner>();
        }
        services.AddScoped<IFileStore, FileStore>();
        services.AddScoped<FileService>();
        services.AddScoped<FileReadUrlService>();
        return services;
    }
}

internal static class ConfigurationBinding
{
    internal static T Bind<T>(IConfiguration configuration, string section) where T : new()
    {
        var value = new T();
        configuration.GetSection(section).Bind(value);
        return value;
    }
}
