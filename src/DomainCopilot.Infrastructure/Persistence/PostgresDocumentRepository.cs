using DomainCopilot.Application;
using DomainCopilot.Domain.Documents;
using Npgsql;
using Pgvector;

namespace DomainCopilot.Infrastructure.Persistence;

public sealed class PostgresDocumentRepository(IPostgresConnections connections) : IDocumentRepository, IDocumentQueryRepository
{
    private const int MaxErrorLength = 500;

    public IReadOnlyList<DocumentSummaryRecord> List(string tenantId) => TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("""
            SELECT d.id,d.doc_id,d.title,d.equipment,d.revision,d.status,d.content_sha256,d.ingest_error,COUNT(c.id)
            FROM documents d LEFT JOIN chunks c ON c.document_id=d.id
            GROUP BY d.id ORDER BY d.doc_id
            """, connection, transaction);
        using var reader = command.ExecuteReader();
        var rows = new List<DocumentSummaryRecord>();
        while (reader.Read()) rows.Add(ReadSummary(reader));
        return (IReadOnlyList<DocumentSummaryRecord>)rows;
    });

    public DocumentSummaryRecord? Get(string tenantId, string idOrDocId) => TenantDatabase.WithTenant(connections, tenantId, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("""
            SELECT d.id,d.doc_id,d.title,d.equipment,d.revision,d.status,d.content_sha256,d.ingest_error,COUNT(c.id)
            FROM documents d LEFT JOIN chunks c ON c.document_id=d.id
            WHERE d.doc_id=@doc_id OR d.id::text=@doc_id
            GROUP BY d.id
            """, connection, transaction);
        command.Parameters.AddWithValue("doc_id", idOrDocId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSummary(reader) : null;
    });

    private static DocumentSummaryRecord ReadSummary(NpgsqlDataReader reader) => new(
        reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8));

    public string? StoredFingerprint(DocumentMeta meta) => TenantDatabase.WithTenant(connections, meta.TenantId, (connection, transaction) =>
    {
        using var command = new NpgsqlCommand("""
            SELECT content_sha256 FROM documents
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id AND source_format=@source_format
              AND ingest_status='ingested'
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", meta.TenantId);
        command.Parameters.AddWithValue("doc_id", meta.DocId);
        command.Parameters.AddWithValue("source_format", meta.SourceFormat);
        return command.ExecuteScalar() as string;
    });

    public void ReplaceDocument(DocumentMeta meta, IReadOnlyList<Chunk> chunks,
        IReadOnlyList<IReadOnlyList<double>> embeddings, IReadOnlyList<SafetyStep> steps, string embeddingModel)
    {
        if (chunks.Count != embeddings.Count) throw new ArgumentException("each chunk must have exactly one embedding");
        TenantDatabase.WithTenant(connections, meta.TenantId, (connection, transaction) =>
        {
            long documentId;
            using (var command = new NpgsqlCommand("""
                INSERT INTO documents
                    (tenant_id, doc_id, title, equipment, document_type, revision, status,
                     source_format, source_path, content_sha256, ingest_status, ingest_error)
                VALUES (@tenant_id,@doc_id,@title,@equipment,@document_type,@revision,@status,
                        @source_format,@source_path,@sha256,'ingested',NULL)
                ON CONFLICT (tenant_id,doc_id,source_format) DO UPDATE SET
                    title=EXCLUDED.title, equipment=EXCLUDED.equipment,
                    document_type=EXCLUDED.document_type, revision=EXCLUDED.revision,
                    status=EXCLUDED.status, source_path=EXCLUDED.source_path,
                    content_sha256=EXCLUDED.content_sha256, ingest_status='ingested', ingest_error=NULL
                RETURNING id
                """, connection, transaction))
            {
                AddMeta(command, meta);
                documentId = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            using (var deleteChunks = new NpgsqlCommand("DELETE FROM chunks WHERE document_id=@document_id", connection, transaction))
            using (var deleteSteps = new NpgsqlCommand("DELETE FROM safety_prerequisites WHERE document_id=@document_id", connection, transaction))
            {
                deleteChunks.Parameters.AddWithValue("document_id", documentId);
                deleteSteps.Parameters.AddWithValue("document_id", documentId);
                deleteChunks.ExecuteNonQuery();
                deleteSteps.ExecuteNonQuery();
            }

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                using var command = new NpgsqlCommand("""
                    INSERT INTO chunks
                        (tenant_id,document_id,ordinal,section,page,revision,context,content,embedding_model,embedding)
                    VALUES (@tenant_id,@document_id,@ordinal,@section,@page,@revision,@context,@content,@model,@embedding)
                    """, connection, transaction);
                command.Parameters.AddWithValue("tenant_id", meta.TenantId);
                command.Parameters.AddWithValue("document_id", documentId);
                command.Parameters.AddWithValue("ordinal", chunk.Ordinal);
                command.Parameters.AddWithValue("section", chunk.Section);
                command.Parameters.AddWithValue("page", (object?)chunk.Page ?? DBNull.Value);
                command.Parameters.AddWithValue("revision", chunk.Revision);
                command.Parameters.AddWithValue("context", chunk.Context);
                command.Parameters.AddWithValue("content", chunk.Text);
                command.Parameters.AddWithValue("model", embeddingModel);
                command.Parameters.AddWithValue("embedding", ToVector(embeddings[i]));
                command.ExecuteNonQuery();
            }

            foreach (var step in steps)
            {
                using var command = new NpgsqlCommand("""
                    INSERT INTO safety_prerequisites (tenant_id,document_id,step_no,step_text)
                    VALUES (@tenant_id,@document_id,@step_no,@step_text)
                    """, connection, transaction);
                command.Parameters.AddWithValue("tenant_id", meta.TenantId);
                command.Parameters.AddWithValue("document_id", documentId);
                command.Parameters.AddWithValue("step_no", step.StepNo);
                command.Parameters.AddWithValue("step_text", step.Text);
                command.ExecuteNonQuery();
            }
        });
    }

    public void MarkFailed(DocumentMeta meta, string error)
    {
        var message = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        TenantDatabase.WithTenant(connections, meta.TenantId, (connection, transaction) =>
        {
            using var update = new NpgsqlCommand("""
                UPDATE documents SET ingest_error=@error
                WHERE tenant_id=@tenant_id AND doc_id=@doc_id AND source_format=@source_format
                """, connection, transaction);
            update.Parameters.AddWithValue("error", message);
            update.Parameters.AddWithValue("tenant_id", meta.TenantId);
            update.Parameters.AddWithValue("doc_id", meta.DocId);
            update.Parameters.AddWithValue("source_format", meta.SourceFormat);
            if (update.ExecuteNonQuery() != 0) return;

            using var insert = new NpgsqlCommand("""
                INSERT INTO documents
                    (tenant_id,doc_id,title,equipment,document_type,revision,status,source_format,
                     source_path,content_sha256,ingest_status,ingest_error)
                VALUES (@tenant_id,@doc_id,@title,@equipment,@document_type,@revision,@status,
                        @source_format,@source_path,@sha256,'failed',@error)
                """, connection, transaction);
            AddMeta(insert, meta);
            insert.Parameters.AddWithValue("error", message);
            insert.ExecuteNonQuery();
        });
    }

    private static void AddMeta(NpgsqlCommand command, DocumentMeta meta)
    {
        command.Parameters.AddWithValue("tenant_id", meta.TenantId);
        command.Parameters.AddWithValue("doc_id", meta.DocId);
        command.Parameters.AddWithValue("title", meta.Title);
        command.Parameters.AddWithValue("equipment", (object?)meta.Equipment ?? DBNull.Value);
        command.Parameters.AddWithValue("document_type", (object?)meta.DocumentType ?? DBNull.Value);
        command.Parameters.AddWithValue("revision", meta.Revision);
        command.Parameters.AddWithValue("status", meta.Status);
        command.Parameters.AddWithValue("source_format", meta.SourceFormat);
        command.Parameters.AddWithValue("source_path", meta.SourcePath);
        command.Parameters.AddWithValue("sha256", meta.ContentSha256);
    }

    internal static Vector ToVector(IReadOnlyList<double> values) => new(values.Select(x => (float)x).ToArray());
}
