using Npgsql;

namespace DomainCopilot.Infrastructure.Persistence;

/// <summary>
/// Explicit administrative migration operations. This service is never invoked automatically at startup.
/// Existing SQL migration files remain the schema source of truth.
/// </summary>
public sealed class MigrationRunner(IPostgresConnections connections, InfrastructureOptions options)
{
    private const string ApplicationRole = "copilot_app";
    private const long MigrationLockId = 0x44434F50494C4F54;

    public void EnsureApplicationRole()
    {
        RequireAdmin();
        if (string.IsNullOrEmpty(options.AppDbPassword))
            throw new InvalidOperationException("APP_DB_PASSWORD is required to provision the application role.");

        using var connection = connections.Administrative.OpenConnection();
        using var transaction = connection.BeginTransaction();
        bool exists;
        using (var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=@name)", connection, transaction))
        {
            check.Parameters.AddWithValue("name", ApplicationRole);
            exists = (bool)check.ExecuteScalar()!;
        }

        if (!exists)
        {
            using var create = new NpgsqlCommand($"CREATE ROLE {new NpgsqlCommandBuilder().QuoteIdentifier(ApplicationRole)} LOGIN NOSUPERUSER NOBYPASSRLS", connection, transaction);
            create.ExecuteNonQuery();
        }

        string passwordLiteral;
        using (var quote = new NpgsqlCommand("SELECT quote_literal(@password)", connection, transaction))
        {
            quote.Parameters.AddWithValue("password", options.AppDbPassword);
            passwordLiteral = (string)quote.ExecuteScalar()!;
        }
        using (var alter = new NpgsqlCommand(
            $"ALTER ROLE {new NpgsqlCommandBuilder().QuoteIdentifier(ApplicationRole)} WITH LOGIN PASSWORD {passwordLiteral} NOSUPERUSER NOBYPASSRLS",
            connection, transaction))
        {
            alter.ExecuteNonQuery();
        }
        using (var privilegeCheck = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_roles r
                WHERE (r.rolsuper OR r.rolbypassrls)
                  AND pg_has_role('copilot_app', r.oid, 'MEMBER')
            )
            """, connection, transaction))
        {
            if ((bool)privilegeCheck.ExecuteScalar()!)
                throw new InvalidOperationException("copilot_app inherits a privileged role; remove the membership before provisioning it.");
        }
        transaction.Commit();
    }

    public IReadOnlyList<string> ProvisionAndApplyMigrations()
    {
        EnsureApplicationRole();
        return ApplyMigrations();
    }

    public IReadOnlyList<string> ApplyMigrations()
    {
        RequireAdmin();
        var files = MigrationFiles();
        using var connection = connections.Administrative.OpenConnection();
        using (var createHistory = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS schema_migrations
                (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())
            """, connection))
            createHistory.ExecuteNonQuery();

        var appliedNow = new List<string>();
        foreach (var file in files)
        {
            using var transaction = connection.BeginTransaction();
            using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@lock_id)", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("lock_id", MigrationLockId);
                lockCommand.ExecuteNonQuery();
            }

            using (var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE name=@name)", connection, transaction))
            {
                check.Parameters.AddWithValue("name", Path.GetFileName(file));
                if ((bool)check.ExecuteScalar()!)
                {
                    transaction.Commit();
                    continue;
                }
            }

            var sql = File.ReadAllText(file);
            using (var migration = new NpgsqlCommand(sql, connection, transaction))
                migration.ExecuteNonQuery();
            using (var record = new NpgsqlCommand("INSERT INTO schema_migrations(name) VALUES(@name)", connection, transaction))
            {
                record.Parameters.AddWithValue("name", Path.GetFileName(file));
                record.ExecuteNonQuery();
            }
            transaction.Commit();
            appliedNow.Add(Path.GetFileName(file));
        }
        return appliedNow;
    }

    private void RequireAdmin()
    {
        if (string.IsNullOrWhiteSpace(options.AdminDatabaseUrl))
            throw new InvalidOperationException("ADMIN_DATABASE_URL is required for administrative database operations.");
    }

    private static string[] MigrationFiles()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "migrations");
        if (Directory.Exists(outputPath))
            return Directory.GetFiles(outputPath, "*.sql").Order(StringComparer.Ordinal).ToArray();

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "migrations");
            if (Directory.Exists(candidate))
                return Directory.GetFiles(candidate, "*.sql").Order(StringComparer.Ordinal).ToArray();
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The migration SQL files were not found.");
    }
}

