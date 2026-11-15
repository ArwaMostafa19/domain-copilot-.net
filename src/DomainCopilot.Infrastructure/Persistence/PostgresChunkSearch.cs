using DomainCopilot.Application;
using DomainCopilot.Domain.Rag;
using Npgsql;

namespace DomainCopilot.Infrastructure.Persistence;

public sealed class PostgresChunkSearch(IPostgresConnections connections) : IChunkSearch
{
    public IReadOnlyList<Evidence> DenseSearch(string tenantId, IReadOnlyList<double> vector, string model, bool includeSuperseded, int limit)
    {
        if (limit <= 0) return Array.Empty<Evidence>();
        return Search(tenantId, """
            SELECT c.id,d.doc_id,d.title,d.equipment,c.section,c.revision,d.status,c.page,
                   c.content,1-(c.embedding <=> @query) AS score
            FROM chunks c JOIN documents d ON d.id=c.document_id
            WHERE c.embedding_model=@model AND d.ingest_status='ingested'
              AND (@all OR d.status='current')
            ORDER BY c.embedding <=> @query LIMIT @limit
            """, command =>
        {
            command.Parameters.AddWithValue("query", PostgresDocumentRepository.ToVector(vector));
            command.Parameters.AddWithValue("model", model);
            command.Parameters.AddWithValue("all", includeSuperseded);
            command.Parameters.AddWithValue("limit", limit);
        }, dense: true);
    }

    public IReadOnlyList<Evidence> KeywordSearch(string tenantId, IReadOnlyList<string> terms, string model, bool includeSuperseded, int limit)
    {
        if (limit <= 0 || terms.Count == 0) return Array.Empty<Evidence>();
        var tsquery = string.Join(" | ", terms);
        return Search(tenantId, """
            SELECT c.id,d.doc_id,d.title,d.equipment,c.section,c.revision,d.status,c.page,
                   c.content,ts_rank_cd(c.tsv,query) AS score
            FROM chunks c JOIN documents d ON d.id=c.document_id,
                 to_tsquery('english',@tsquery) AS query
            WHERE c.embedding_model=@model AND d.ingest_status='ingested'
              AND (@all OR d.status='current') AND c.tsv @@ query
            ORDER BY score DESC LIMIT @limit
            """, command =>
        {
            command.Parameters.AddWithValue("tsquery", tsquery);
            command.Parameters.AddWithValue("model", model);
            command.Parameters.AddWithValue("all", includeSuperseded);
            command.Parameters.AddWithValue("limit", limit);
        }, dense: false);
    }

    private IReadOnlyList<Evidence> Search(string tenantId, string sql, Action<NpgsqlCommand> bind, bool dense) =>
        TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
        {
            using var command = new NpgsqlCommand(sql, connection, transaction);
            bind(command);
            using var reader = command.ExecuteReader();
            var results = new List<Evidence>();
            while (reader.Read())
            {
                var evidence = new Evidence(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.GetString(8),
                    DenseScore: dense ? reader.GetDouble(9) : 0,
                    KeywordScore: dense ? 0 : reader.GetDouble(9),
                    Equipment: reader.IsDBNull(3) ? null : reader.GetString(3));
                results.Add(evidence);
            }
            return results;
        });
}
