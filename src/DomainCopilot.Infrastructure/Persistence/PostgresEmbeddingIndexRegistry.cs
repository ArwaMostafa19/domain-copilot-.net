using DomainCopilot.Application;
using DomainCopilot.Domain.Llm;
using Npgsql;

namespace DomainCopilot.Infrastructure.Persistence;

public sealed class PostgresEmbeddingIndexRegistry(IPostgresConnections connections) : IEmbeddingIndexRegistry
{
    public EmbeddingSpec? Get() => TenantDatabase.WithApplication(connections, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("SELECT model, dimensions FROM embedding_index_meta", connection, transaction);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new EmbeddingSpec(reader.GetString(0), reader.GetInt32(1)) : null;
    });

    public void Register(EmbeddingSpec spec) => TenantDatabase.WithApplication(connections, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("""
            INSERT INTO embedding_index_meta (model,dimensions) VALUES (@model,@dimensions)
            ON CONFLICT (id) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("model", spec.Model);
        command.Parameters.AddWithValue("dimensions", spec.Dimensions);
        command.ExecuteNonQuery();
    });
}
