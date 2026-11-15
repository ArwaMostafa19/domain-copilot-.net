using DomainCopilot.Application;
using DomainCopilot.Infrastructure;
using DomainCopilot.Infrastructure.Persistence;
using DomainCopilot.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector.Npgsql;

internal static class PostgresOptInIntegrationTests
{
    private const string OptInVariable = "DOMAINCOPILOT_RUN_DISPOSABLE_DB_TESTS";
    private const string AdminUrlVariable = "DOMAINCOPILOT_DISPOSABLE_ADMIN_DATABASE_URL";
    private const string PasswordVariable = "DOMAINCOPILOT_DISPOSABLE_APP_DB_PASSWORD";
    private const string DatabaseName = "domaincopilot_phase4_disposable";

    public static string RunIfEnabled()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
            return $"SKIPPED (set {OptInVariable}=1 only for a disposable local PostgreSQL 16 + pgvector cluster)";

        var adminUrl = Environment.GetEnvironmentVariable(AdminUrlVariable);
        var appPassword = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrWhiteSpace(adminUrl) || string.IsNullOrEmpty(appPassword))
            throw new InvalidOperationException($"{AdminUrlVariable} and {PasswordVariable} are required when opt-in database tests are enabled.");

        var adminSettings = new NpgsqlConnectionStringBuilder(adminUrl);
        if (!IsLoopback(adminSettings.Host) || adminSettings.Database != DatabaseName ||
            string.Equals(adminSettings.Username, "copilot_app", StringComparison.Ordinal))
            throw new InvalidOperationException($"Opt-in PostgreSQL tests require a loopback admin connection to the explicitly disposable database '{DatabaseName}'.");

        var appSettings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString)
        {
            Username = "copilot_app",
            Password = appPassword,
            MaxPoolSize = 1,
            MinPoolSize = 0
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Copilot:Legacy:DATABASE_URL"] = appSettings.ConnectionString,
            ["Copilot:Legacy:ADMIN_DATABASE_URL"] = adminSettings.ConnectionString,
            ["Copilot:Legacy:APP_DB_PASSWORD"] = appPassword
        }).Build();
        var services = new ServiceCollection();
        services.AddDomainCopilotInfrastructure(config);
        using var provider = services.BuildServiceProvider();
        var connections = provider.GetRequiredService<IPostgresConnections>();
        var migrations = provider.GetRequiredService<MigrationRunner>();

        VerifyDisposableDatabase(adminSettings.ConnectionString);
        migrations.EnsureApplicationRole();
        ApplyBaselineMigrationsForRollbackTest(connections.Administrative);
        var mismatchedRunId = InsertCrossTenantRun(connections.Administrative);

        try
        {
            migrations.ApplyMigrations();
            throw new InvalidOperationException("Migration 006 unexpectedly accepted a cross-tenant run/user reference.");
        }
        catch (PostgresException error) when (error.SqlState == "23503")
        {
            VerifyFailedMigrationWasRolledBack(connections.Administrative);
        }

        using (var connection = connections.Administrative.OpenConnection())
        using (var command = new NpgsqlCommand("DELETE FROM runs WHERE id=@id", connection))
        {
            command.Parameters.AddWithValue("id", mismatchedRunId);
            command.ExecuteNonQuery();
        }
        var applied = migrations.ApplyMigrations();
        if (!applied.SequenceEqual(["006_tenant_consistent_foreign_keys.sql"], StringComparer.Ordinal))
            throw new InvalidOperationException("Migration runner did not apply the pending migration 006 in filename order.");

        VerifyMigrationHistory(connections.Administrative);
        VerifyRoleSeparation(connections.Administrative, connections.Application);
        VerifyTenantIsolationAndPoolReset(connections);
        VerifyDocumentAndUsageQueries(connections);
        VerifyConcurrentDraftVersions(connections, appSettings.ConnectionString);
        return "PASSED (local disposable PostgreSQL 16 + pgvector)";
    }

    private static bool IsLoopback(string? host) => host is not null &&
        (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         host.Equals("127.0.0.1", StringComparison.Ordinal) || host.Equals("::1", StringComparison.Ordinal));

    private static void VerifyDisposableDatabase(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT current_setting('server_version_num')::integer,
                   EXISTS (SELECT 1 FROM pg_available_extensions WHERE name='vector'),
                   (SELECT count(*) FROM pg_tables WHERE schemaname='public')
            """, connection);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) / 10000 != 16 || !reader.GetBoolean(1) || reader.GetInt64(2) != 0)
            throw new InvalidOperationException($"The opted-in database must be an empty PostgreSQL 16 database with pgvector available; tests refuse to reset or clean databases.");
    }

    private static void ApplyBaselineMigrationsForRollbackTest(NpgsqlDataSource admin)
    {
        using var connection = admin.OpenConnection();
        using (var create = new NpgsqlCommand("CREATE TABLE schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection))
            create.ExecuteNonQuery();

        var migrationDirectory = Path.Combine(AppContext.BaseDirectory, "migrations");
        var files = Directory.GetFiles(migrationDirectory, "*.sql")
            .Where(path => Path.GetFileName(path).StartsWith("001_", StringComparison.Ordinal) ||
                          Path.GetFileName(path).StartsWith("002_", StringComparison.Ordinal) ||
                          Path.GetFileName(path).StartsWith("003_", StringComparison.Ordinal) ||
                          Path.GetFileName(path).StartsWith("004_", StringComparison.Ordinal) ||
                          Path.GetFileName(path).StartsWith("005_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var transaction = connection.BeginTransaction();
            using (var migration = new NpgsqlCommand(File.ReadAllText(file), connection, transaction))
                migration.ExecuteNonQuery();
            using (var record = new NpgsqlCommand("INSERT INTO schema_migrations(name) VALUES(@name)", connection, transaction))
            {
                record.Parameters.AddWithValue("name", Path.GetFileName(file));
                record.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private static Guid InsertCrossTenantRun(NpgsqlDataSource admin)
    {
        using var connection = admin.OpenConnection();
        using var createUser = new NpgsqlCommand("""
            INSERT INTO users (tenant_id,username,role,password_hash)
            VALUES ('tenant-alpha','phase4-rollback-user','technician','test-only') RETURNING id
            """, connection);
        var userId = (long)createUser.ExecuteScalar()!;
        using var createRun = new NpgsqlCommand("""
            INSERT INTO runs (tenant_id,user_id,question,status)
            VALUES ('tenant-beta',@user_id,'migration rollback fixture','running') RETURNING id
            """, connection);
        createRun.Parameters.AddWithValue("user_id", userId);
        return (Guid)createRun.ExecuteScalar()!;
    }

    private static void VerifyFailedMigrationWasRolledBack(NpgsqlDataSource admin)
    {
        using var connection = admin.OpenConnection();
        using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS (SELECT 1 FROM schema_migrations WHERE name='006_tenant_consistent_foreign_keys.sql')
               AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='documents_tenant_id_id_key')
               AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='runs_same_tenant_user_fk')
            """, connection);
        if (!(bool)command.ExecuteScalar()!)
            throw new InvalidOperationException("Failed migration 006 left constraints or a migration-history row behind.");
    }

    private static void VerifyMigrationHistory(NpgsqlDataSource admin)
    {
        using var connection = admin.OpenConnection();
        using var command = new NpgsqlCommand("SELECT name FROM schema_migrations ORDER BY name", connection);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        var expected = Enumerable.Range(1, 6).Select(number =>
            Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "migrations"), $"{number:000}_*.sql").Single())
            .Select(Path.GetFileName).ToArray();
        if (!names.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("Applied migration filenames are missing, duplicated, or out of order.");
    }

    private static void VerifyRoleSeparation(NpgsqlDataSource admin, NpgsqlDataSource application)
    {
        using (var connection = admin.OpenConnection())
        using (var command = new NpgsqlCommand("""
            SELECT rolsuper, rolbypassrls,
                   EXISTS (SELECT 1 FROM pg_roles r WHERE (r.rolsuper OR r.rolbypassrls)
                           AND pg_has_role('copilot_app',r.oid,'MEMBER'))
            FROM pg_roles WHERE rolname='copilot_app'
            """, connection))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2))
                throw new InvalidOperationException("copilot_app is superuser, bypasses RLS, or inherits a privileged role.");
        }

        using (var connection = admin.OpenConnection())
        using (var command = new NpgsqlCommand("SELECT current_user, rolsuper, rolcreaterole FROM pg_roles WHERE rolname=current_user", connection))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) == "copilot_app" || (!reader.GetBoolean(1) && !reader.GetBoolean(2)))
                throw new InvalidOperationException("Administrative connections must use a distinct role with role-provisioning privileges.");
        }

        using (var connection = application.OpenConnection())
        {
            if (connection.UserName() != "copilot_app")
                throw new InvalidOperationException("Application connections did not authenticate as copilot_app.");
            try
            {
                using var forbidden = new NpgsqlCommand("CREATE TABLE public.phase4_privilege_probe(id integer)", connection);
                forbidden.ExecuteNonQuery();
                throw new InvalidOperationException("copilot_app unexpectedly created a table.");
            }
            catch (PostgresException error) when (error.SqlState == "42501") { }
        }
    }

    private static void VerifyTenantIsolationAndPoolReset(IPostgresConnections connections)
    {
        long firstUserId;
        using (var connection = connections.Administrative.OpenConnection())
        {
            using var command = new NpgsqlCommand("""
                INSERT INTO users (tenant_id,username,role,password_hash)
                VALUES ('tenant-alpha','phase4-tenant-user','technician','test-only') RETURNING id
                """, connection);
            firstUserId = (long)command.ExecuteScalar()!;
        }

        var users = new PostgresUserRepository(connections);
        if (users.Get("tenant-alpha", firstUserId) is null || users.Get("tenant-beta", firstUserId) is not null)
            throw new InvalidOperationException("RLS did not isolate the same user id by transaction-local tenant context.");

        using (var connection = connections.Application.OpenConnection())
        using (var command = new NpgsqlCommand("SELECT current_setting('app.tenant_id',true)", connection))
        {
            var tenant = command.ExecuteScalar() as string;
            if (!string.IsNullOrEmpty(tenant))
                throw new InvalidOperationException("Tenant context leaked when a pooled connection was reused.");
        }

        using var writeConnection = connections.Application.OpenConnection();
        using var transaction = writeConnection.BeginTransaction();
        using (var setTenant = new NpgsqlCommand("SELECT set_config('app.tenant_id','tenant-alpha',true)", writeConnection, transaction))
            setTenant.ExecuteScalar();
        try
        {
            using var forbidden = new NpgsqlCommand("""
                INSERT INTO users (tenant_id,username,role,password_hash)
                VALUES ('tenant-beta','phase4-cross-tenant-write','technician','test-only')
                """, writeConnection, transaction);
            forbidden.ExecuteNonQuery();
            throw new InvalidOperationException("RLS accepted a cross-tenant insert.");
        }
        catch (PostgresException error) when (error.SqlState == "42501") { }
        transaction.Rollback();
    }

    private static void VerifyConcurrentDraftVersions(IPostgresConnections connections, string applicationConnectionString)
    {
        long userId;
        using (var connection = connections.Administrative.OpenConnection())
        using (var command = new NpgsqlCommand("SELECT id FROM users WHERE tenant_id='tenant-alpha' AND username='phase4-tenant-user'", connection))
            userId = (long)command.ExecuteScalar()!;

        var runs = new PostgresRunRepository(connections);
        var runId = runs.Create("tenant-alpha", userId, "concurrent draft fixture");
        const int writers = 12;
        var poolSettings = new NpgsqlConnectionStringBuilder(applicationConnectionString)
        { MaxPoolSize = writers, MinPoolSize = 0 };
        using var concurrentPool = new NpgsqlDataSourceBuilder(poolSettings.ConnectionString).Build();
        var concurrentConnections = new ApplicationOnlyConnections(concurrentPool);
        var workOrders = new PostgresWorkOrderRepository(concurrentConnections);
        Parallel.For(0, writers, index => workOrders.CreateDraft("tenant-alpha", runId,
            new Dictionary<string, object?> { ["writer"] = index }));

        using var verifyConnection = connections.Administrative.OpenConnection();
        using var verify = new NpgsqlCommand("""
            SELECT count(*), min(version), max(version) FROM work_orders WHERE run_id=@run_id
            """, verifyConnection);
        verify.Parameters.AddWithValue("run_id", Guid.Parse(runId));
        using var reader = verify.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != writers || reader.GetInt32(1) != 1 || reader.GetInt32(2) != writers)
            throw new InvalidOperationException("Concurrent draft creation did not produce a unique, contiguous version sequence.");
    }

    private static void VerifyDocumentAndUsageQueries(IPostgresConnections connections)
    {
        long alphaUserId;
        long alphaOtherUserId;
        long betaUserId;
        long alphaDocumentId;
        using (var connection = connections.Administrative.OpenConnection())
        {
            alphaUserId = InsertUser(connection, "tenant-alpha", "phase5a-query-alpha-tech");
            alphaOtherUserId = InsertUser(connection, "tenant-alpha", "phase5a-query-alpha-other");
            betaUserId = InsertUser(connection, "tenant-beta", "phase5a-query-beta-tech");
            alphaDocumentId = InsertDocument(connection, "tenant-alpha", "phase5a-alpha-doc", "Alpha query fixture", withChunk: true);
            InsertDocument(connection, "tenant-beta", "phase5a-beta-doc", "Beta query fixture", withChunk: false);

            InsertAsk(connection, "tenant-alpha", alphaUserId, "alpha ask", 2, 3);
            InsertAsk(connection, "tenant-alpha", alphaOtherUserId, "alpha other ask", 13, 17);
            InsertAsk(connection, "tenant-beta", betaUserId, "beta ask", 101, 103);
            InsertRun(connection, "tenant-alpha", alphaUserId, "alpha run", 4);
            InsertRun(connection, "tenant-alpha", alphaOtherUserId, "alpha other run", 30);
            InsertRun(connection, "tenant-beta", betaUserId, "beta run", 201);
        }

        var documents = new PostgresDocumentRepository(connections);
        var alphaDocuments = documents.List("tenant-alpha");
        if (alphaDocuments.Count != 1 || alphaDocuments[0].DocId != "phase5a-alpha-doc" || alphaDocuments[0].ChunkCount != 1)
            throw new InvalidOperationException("Document query did not return the tenant-scoped document and chunk count.");
        if (documents.Get("tenant-alpha", "phase5a-alpha-doc")?.Id != alphaDocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Document detail query did not resolve the tenant's doc_id.");
        if (documents.Get("tenant-alpha", "phase5a-beta-doc") is not null)
            throw new InvalidOperationException("Document detail query exposed a document belonging to another tenant.");
        if (documents.List("tenant-beta").Single().DocId != "phase5a-beta-doc")
            throw new InvalidOperationException("Document list query returned the wrong tenant's data.");

        var usage = new PostgresUsageRepository(connections);
        var alphaUserUsage = usage.ForUser("tenant-alpha", alphaUserId);
        if (alphaUserUsage.DailyAsks.Sum(row => row.PromptTokens) != 2 ||
            alphaUserUsage.DailyAsks.Sum(row => row.CompletionTokens) != 3 ||
            alphaUserUsage.DailyAsks.Sum(row => row.AskCount) != 1 ||
            alphaUserUsage.DailyRuns.Sum(row => row.TotalTokens) != 4 ||
            alphaUserUsage.DailyRuns.Sum(row => row.RunCount) != 1)
            throw new InvalidOperationException("User usage query did not apply its user and tenant filters.");

        var alphaTenantUsage = usage.ForTenant("tenant-alpha");
        if (alphaTenantUsage.DailyAsks.Sum(row => row.PromptTokens) != 15 ||
            alphaTenantUsage.DailyAsks.Sum(row => row.CompletionTokens) != 20 ||
            alphaTenantUsage.DailyAsks.Sum(row => row.AskCount) != 2 ||
            alphaTenantUsage.DailyRuns.Sum(row => row.TotalTokens) != 34 ||
            alphaTenantUsage.DailyRuns.Sum(row => row.RunCount) != 2)
            throw new InvalidOperationException("Tenant usage query returned incorrect aggregates or included another tenant.");

        var betaUsage = usage.ForTenant("tenant-beta");
        if (betaUsage.DailyAsks.Sum(row => row.PromptTokens) != 101 || betaUsage.DailyAsks.Sum(row => row.AskCount) != 1 ||
            betaUsage.DailyRuns.Sum(row => row.TotalTokens) != 201 || betaUsage.DailyRuns.Sum(row => row.RunCount) != 1)
            throw new InvalidOperationException("Usage query did not isolate the second tenant.");
    }

    private static long InsertUser(NpgsqlConnection connection, string tenantId, string username)
    {
        using var command = new NpgsqlCommand("INSERT INTO users(tenant_id,username,role,password_hash) VALUES(@tenant,@username,'technician','test-only') RETURNING id", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("username", username);
        return (long)command.ExecuteScalar()!;
    }

    private static long InsertDocument(NpgsqlConnection connection, string tenantId, string docId, string title, bool withChunk)
    {
        long id;
        using (var command = new NpgsqlCommand("""
            INSERT INTO documents(tenant_id,doc_id,title,revision,status,source_format,source_path,content_sha256,ingest_status)
            VALUES(@tenant,@doc_id,@title,'A','current','md',@path,'test-hash','ingested') RETURNING id
            """, connection))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("doc_id", docId);
            command.Parameters.AddWithValue("title", title);
            command.Parameters.AddWithValue("path", docId + ".md");
            id = (long)command.ExecuteScalar()!;
        }
        if (withChunk)
        {
            using var command = new NpgsqlCommand("""
                INSERT INTO chunks(tenant_id,document_id,ordinal,section,revision,context,content,embedding_model,embedding)
                VALUES(@tenant,@document_id,0,'Test section','A','fixture','fixture content','fixture-model',@embedding::vector)
                """, connection);
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("document_id", id);
            command.Parameters.AddWithValue("embedding", "[" + string.Join(',', Enumerable.Repeat("0", 768)) + "]");
            command.ExecuteNonQuery();
        }
        return id;
    }

    private static void InsertAsk(NpgsqlConnection connection, string tenantId, long userId, string question, int promptTokens, int completionTokens)
    {
        using var command = new NpgsqlCommand("""
            INSERT INTO ask_log(tenant_id,user_id,question,answer_text,prompt_tokens,completion_tokens,created_at)
            VALUES(@tenant,@user_id,@question,'test answer',@prompt,@completion,date_trunc('day',now())+interval '12 hours')
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("question", question);
        command.Parameters.AddWithValue("prompt", promptTokens);
        command.Parameters.AddWithValue("completion", completionTokens);
        command.ExecuteNonQuery();
    }

    private static void InsertRun(NpgsqlConnection connection, string tenantId, long userId, string question, int tokens)
    {
        using var command = new NpgsqlCommand("""
            INSERT INTO runs(tenant_id,user_id,question,status,total_tokens,created_at)
            VALUES(@tenant,@user_id,@question,'completed',@tokens,date_trunc('day',now())+interval '12 hours')
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("question", question);
        command.Parameters.AddWithValue("tokens", tokens);
        command.ExecuteNonQuery();
    }

    private static string UserName(this NpgsqlConnection connection)
    {
        using var command = new NpgsqlCommand("SELECT current_user", connection);
        return (string)command.ExecuteScalar()!;
    }

    private sealed class ApplicationOnlyConnections : IPostgresConnections
    {
        public ApplicationOnlyConnections(NpgsqlDataSource application) => Application = application;
        public NpgsqlDataSource Application { get; }
        public NpgsqlDataSource Administrative => Application;
    }
}
