using Npgsql;
using Pgvector.Npgsql;

namespace DomainCopilot.Infrastructure.Persistence;

public interface IPostgresConnections
{
    NpgsqlDataSource Application { get; }
    NpgsqlDataSource Administrative { get; }
}

internal sealed class PostgresConnections : IPostgresConnections, IDisposable
{
    public PostgresConnections(InfrastructureOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DatabaseUrl))
            throw new InvalidOperationException("DATABASE_URL is required for PostgreSQL infrastructure.");
        var applicationSettings = new NpgsqlConnectionStringBuilder(PostgresConnectionString.ToNpgsql(options.DatabaseUrl));
        if (!string.Equals(applicationSettings.Username, "copilot_app", StringComparison.Ordinal))
            throw new InvalidOperationException("DATABASE_URL must authenticate as the restricted copilot_app role.");
        Application = Create(applicationSettings.ConnectionString);

        if (string.IsNullOrWhiteSpace(options.AdminDatabaseUrl))
        {
            Administrative = Application;
        }
        else
        {
            var administrativeSettings = new NpgsqlConnectionStringBuilder(PostgresConnectionString.ToNpgsql(options.AdminDatabaseUrl));
            if (string.Equals(administrativeSettings.Username, applicationSettings.Username, StringComparison.Ordinal))
                throw new InvalidOperationException("ADMIN_DATABASE_URL must use a separate administrative role.");
            Administrative = Create(administrativeSettings.ConnectionString);
        }
    }

    public NpgsqlDataSource Application { get; }
    public NpgsqlDataSource Administrative { get; }

    private static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return builder.Build();
    }

    public void Dispose()
    {
        Application.Dispose();
        if (!ReferenceEquals(Application, Administrative)) Administrative.Dispose();
    }
}

internal static class TenantDatabase
{
    public static T WithTenant<T>(IPostgresConnections sources, string tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, T> action)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        using var connection = sources.Application.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant_id, true)", connection, transaction))
        {
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.ExecuteScalar();
        }
        var value = action(connection, transaction);
        transaction.Commit();
        return value;
    }

    public static void WithTenant(IPostgresConnections sources, string tenantId,
        Action<NpgsqlConnection, NpgsqlTransaction> action) =>
        WithTenant(sources, tenantId, (connection, transaction) => { action(connection, transaction); return true; });

    public static T WithApplication<T>(IPostgresConnections sources,
        Func<NpgsqlConnection, NpgsqlTransaction, T> action)
    {
        using var connection = sources.Application.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var value = action(connection, transaction);
        transaction.Commit();
        return value;
    }

    public static void WithApplication(IPostgresConnections sources,
        Action<NpgsqlConnection, NpgsqlTransaction> action) =>
        WithApplication(sources, (connection, transaction) => { action(connection, transaction); return true; });
}

public sealed class PostgresReadinessProbe(IPostgresConnections connections) : DomainCopilot.Application.IDatabaseReadiness
{
    public async ValueTask CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.Application.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}

