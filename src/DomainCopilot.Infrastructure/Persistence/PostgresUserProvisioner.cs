using DomainCopilot.Application;
using DomainCopilot.Application.Security;
using Npgsql;

namespace DomainCopilot.Infrastructure.Persistence;

/// <summary>Explicit administrative path for idempotent initial user provisioning.</summary>
public sealed class PostgresUserProvisioner(IPostgresConnections connections, InfrastructureOptions options) : IUserProvisioner
{
    public bool CreateIfMissing(string tenantId, string username, string role, string password)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 128 || tenantId.Any(char.IsControl))
            throw new ArgumentException("tenant must contain 1 to 128 printable characters", nameof(tenantId));
        username = username?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) || username.Length > 128 || username.Any(char.IsControl))
            throw new ArgumentException("username must contain 1 to 128 printable characters", nameof(username));
        if (role is not ("technician" or "supervisor")) throw new ArgumentException("role must be technician or supervisor", nameof(role));
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("password must not be empty", nameof(password));
        if (string.IsNullOrWhiteSpace(options.AdminDatabaseUrl))
            throw new InvalidOperationException("ADMIN_DATABASE_URL is required for user provisioning.");
        var hash = ApplicationSecurity.HashPassword(password);
        using var connection = connections.Administrative.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var tenant = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM tenants WHERE id=@tenant_id)", connection, transaction))
        {
            tenant.Parameters.AddWithValue("tenant_id", tenantId);
            if (!(bool)tenant.ExecuteScalar()!) throw new InvalidOperationException("the requested tenant does not exist");
        }
        using var insert = new NpgsqlCommand("""
            INSERT INTO users (tenant_id,username,role,password_hash,active)
            VALUES (@tenant_id,@username,@role,@password_hash,true)
            ON CONFLICT (tenant_id,username) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("tenant_id", tenantId);
        insert.Parameters.AddWithValue("username", username.Trim());
        insert.Parameters.AddWithValue("role", role);
        insert.Parameters.AddWithValue("password_hash", hash);
        var created = insert.ExecuteNonQuery() == 1;
        transaction.Commit();
        return created;
    }
}





