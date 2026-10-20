using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.AdministrativeUnits;
using SmartSolar.Modules.AdministrativeUnits.Import;

namespace SmartSolar.Api.Cli;

/// <summary>
/// Controlled import of the official administrative catalog — a command-line mode of the API host, never an HTTP endpoint and never
/// run at startup:
/// <code>dotnet SmartSolar.Api.dll admin-catalog import --dataset &lt;dir&gt; --confirm-host &lt;host&gt; --confirm-database &lt;db&gt; --confirm-user &lt;user&gt; [--apply] [--operator &lt;name&gt;]</code>
/// Dry run by default (validates the files and prints the diff against the database; writes nothing). <c>--apply</c> writes everything
/// in one transaction. The connection string is ConnectionStrings:DefaultConnection (appsettings + environment); the command refuses
/// to run unless <c>--confirm-host</c>, <c>--confirm-database</c> and <c>--confirm-user</c> all equal the resolved target (on a shared
/// pooler such as Supavisor only the user name <c>postgres.&lt;project-ref&gt;</c> tells projects apart), and prints only the host,
/// database and user name (never the password). Connection failures are reported as one sanitized line.
/// Exit codes: 0 success, 1 invalid dataset or refused import, 2 usage / configuration / host confirmation error.
/// </summary>
public static class AdministrativeCatalogCli
{
    public const string Command = "admin-catalog";
    public const string ConnectionStringName = "DefaultConnection";

    public sealed record Options(string DatasetDirectory, bool Apply, string ConfirmHost, string ConfirmDatabase, string ConfirmUser, string? Operator);

    public static bool IsCommand(string[] args) => args.Length > 0 && string.Equals(args[0], Command, StringComparison.Ordinal);

    public const string Usage =
        "Usage: dotnet SmartSolar.Api.dll admin-catalog import --dataset <dir> --confirm-host <host> --confirm-database <db> --confirm-user <user> [--apply] [--operator <name>]";

    public static (Options? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0] != Command || args[1] != "import") return (null, Usage);
        string? dataset = null, host = null, database = null, user = null, @operator = null;
        var apply = false;
        for (var i = 2; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--apply":
                    apply = true;
                    break;
                case "--dataset" or "--confirm-host" or "--confirm-database" or "--confirm-user" or "--operator"
                    when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    var value = args[++i];
                    switch (args[i - 1])
                    {
                        case "--dataset": dataset = value; break;
                        case "--confirm-host": host = value; break;
                        case "--confirm-database": database = value; break;
                        case "--confirm-user": user = value; break;
                        default: @operator = value; break;
                    }
                    break;
                default:
                    return (null, $"Unknown or incomplete argument '{args[i]}'. {Usage}");
            }
        }
        if (string.IsNullOrWhiteSpace(dataset)) return (null, $"--dataset is required. {Usage}");
        if (string.IsNullOrWhiteSpace(host)) return (null, $"--confirm-host is required (the database host you intend to change). {Usage}");
        if (string.IsNullOrWhiteSpace(database)) return (null, $"--confirm-database is required (the database you intend to change). {Usage}");
        if (string.IsNullOrWhiteSpace(user)) return (null, $"--confirm-user is required (the user name, e.g. postgres.<project-ref> on Supabase). {Usage}");
        return (new Options(dataset, apply, host.Trim(), database.Trim(), user.Trim(), string.IsNullOrWhiteSpace(@operator) ? null : @operator.Trim()), null);
    }

    /// <summary>Entry point used by Program.cs: configuration from appsettings(.{environment}).json and environment variables.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var (options, error) = Parse(args);
        if (options is null)
        {
            await output.WriteLineAsync(error);
            return 2;
        }
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        return await RunAsync(options, configuration.GetConnectionString(ConnectionStringName), output, TimeProvider.System, cancellationToken);
    }

    public static async Task<int> RunAsync(Options options, string? connectionString, TextWriter output, TimeProvider time, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await output.WriteLineAsync($"ConnectionStrings:{ConnectionStringName} is not configured.");
            return 2;
        }
        NpgsqlConnectionStringBuilder target;
        try
        {
            target = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            await output.WriteLineAsync("The connection string is not a valid PostgreSQL connection string.");
            return 2;
        }
        await output.WriteLineAsync($"Target database: host={target.Host} database={target.Database} user={target.Username}");
        if (!string.Equals(target.Host, options.ConfirmHost, StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync($"Refusing to run: --confirm-host '{options.ConfirmHost}' does not match the resolved host '{target.Host}'.");
            return 2;
        }
        if (!string.Equals(target.Database, options.ConfirmDatabase, StringComparison.Ordinal))
        {
            await output.WriteLineAsync($"Refusing to run: --confirm-database '{options.ConfirmDatabase}' does not match the resolved database '{target.Database}'.");
            return 2;
        }
        if (!string.Equals(target.Username, options.ConfirmUser, StringComparison.Ordinal))
        {
            await output.WriteLineAsync($"Refusing to run: --confirm-user '{options.ConfirmUser}' does not match the resolved user '{target.Username}'.");
            return 2;
        }
        if (!Directory.Exists(options.DatasetDirectory))
        {
            await output.WriteLineAsync($"Dataset directory '{options.DatasetDirectory}' does not exist.");
            return 2;
        }

        CatalogDatasetFiles files;
        try
        {
            files = await CatalogDatasetFiles.LoadAsync(options.DatasetDirectory, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 1;
        }

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        CatalogImportResult result;
        try
        {
            result = await new AdministrativeCatalogImporter(db, time).RunAsync(files, options.Apply, options.Operator ?? Environment.UserName, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or DbUpdateException or InvalidOperationException { InnerException: System.Data.Common.DbException })
        {
            // One sanitized line: never the connection string, never a stack trace. A failed apply wrote nothing (single transaction).
            var state = (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;
            await output.WriteLineAsync($"Database error ({ex.GetType().Name}{(state is null ? "" : $", SQLSTATE {state}")}): nothing was applied.");
            return 2;
        }
        await WriteReportAsync(result, options.Apply, output);
        return result.Succeeded ? 0 : 1;
    }

    private static async Task WriteReportAsync(CatalogImportResult result, bool apply, TextWriter output)
    {
        var manifest = result.Report.Manifest;
        await output.WriteLineAsync($"Dataset: {manifest?.DatasetKey} (province {manifest?.ProvinceCode} {manifest?.ProvinceName}, effective {manifest?.EffectiveFrom:yyyy-MM-dd})");
        foreach (var (file, sha) in result.Report.ComputedSha256.OrderBy(p => p.Key, StringComparer.Ordinal))
            await output.WriteLineAsync($"  {file} sha256 {sha}");
        if (!result.Report.IsValid)
        {
            await output.WriteLineAsync($"INVALID dataset ({result.Report.Errors.Count} errors):");
            foreach (var error in result.Report.Errors.Take(200)) await output.WriteLineAsync($"  - {error}");
            if (apply) await output.WriteLineAsync("Nothing was applied.");
            return;
        }
        await output.WriteLineAsync($"Valid: {result.Report.Communes.Count} communes, {result.Report.Legacy.Count} legacy mappings.");
        if (result.Diff is { } diff)
        {
            await output.WriteLineAsync("Diff against the database:");
            await output.WriteLineAsync($"  province: {(diff.ProvinceIsNew ? "new" : diff.ProvinceChanges.Count == 0 ? "unchanged" : string.Join("; ", diff.ProvinceChanges.Select(c => $"{c.Field}: {c.Before} -> {c.After}")))}");
            await output.WriteLineAsync($"  communes: {diff.NewCommunes.Count} new, {diff.ChangedCommunes.Select(c => c.Code).Distinct().Count()} changed, {diff.ClosedCommunes.Count} closed, {diff.ReopenedCommunes.Count} reopened");
            foreach (var change in diff.ChangedCommunes.Take(50)) await output.WriteLineAsync($"    changed {change.Code} {change.Field}: {change.Before} -> {change.After}");
            if (diff.ClosedCommunes.Count > 0) await output.WriteLineAsync($"    closed (valid_to = effective date): {string.Join(", ", diff.ClosedCommunes)}");
            await output.WriteLineAsync($"  legacy map: {diff.NewLegacyRows} new, {diff.ChangedLegacyRows} changed, {diff.UnchangedLegacyRows} unchanged, {diff.RetainedLegacyRows} retained (not in this dataset, never deleted)");
        }
        if (result.AlreadyApplied) await output.WriteLineAsync("This dataset key is already applied with the same checksums (re-applying changes nothing).");
        if (result.Refusals.Count > 0)
        {
            await output.WriteLineAsync("REFUSED:");
            foreach (var refusal in result.Refusals) await output.WriteLineAsync($"  - {refusal}");
            return;
        }
        await output.WriteLineAsync(result.Applied ? "APPLIED in one transaction." : "Dry run: nothing was written. Re-run with --apply to apply.");
    }
}
