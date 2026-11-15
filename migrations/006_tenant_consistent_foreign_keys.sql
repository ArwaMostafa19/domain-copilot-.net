-- Add tenant-consistent composite foreign keys without replacing the original keys.
-- Existing rows that cross tenant boundaries intentionally prevent this migration
-- from applying until reviewed; the migration runner executes it transactionally.
ALTER TABLE documents
    ADD CONSTRAINT documents_tenant_id_id_key UNIQUE (tenant_id, id);
ALTER TABLE users
    ADD CONSTRAINT users_tenant_id_id_key UNIQUE (tenant_id, id);
ALTER TABLE runs
    ADD CONSTRAINT runs_tenant_id_id_key UNIQUE (tenant_id, id);

ALTER TABLE chunks
    ADD CONSTRAINT chunks_same_tenant_document_fk
    FOREIGN KEY (tenant_id, document_id) REFERENCES documents (tenant_id, id) ON DELETE CASCADE;
ALTER TABLE safety_prerequisites
    ADD CONSTRAINT safety_same_tenant_document_fk
    FOREIGN KEY (tenant_id, document_id) REFERENCES documents (tenant_id, id) ON DELETE CASCADE;
ALTER TABLE runs
    ADD CONSTRAINT runs_same_tenant_user_fk
    FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id);
ALTER TABLE run_steps
    ADD CONSTRAINT run_steps_same_tenant_run_fk
    FOREIGN KEY (tenant_id, run_id) REFERENCES runs (tenant_id, id) ON DELETE CASCADE;
ALTER TABLE work_orders
    ADD CONSTRAINT work_orders_same_tenant_run_fk
    FOREIGN KEY (tenant_id, run_id) REFERENCES runs (tenant_id, id) ON DELETE CASCADE;
ALTER TABLE audit_log
    ADD CONSTRAINT audit_same_tenant_actor_fk
    FOREIGN KEY (tenant_id, actor_user_id) REFERENCES users (tenant_id, id);
ALTER TABLE ask_log
    ADD CONSTRAINT ask_log_same_tenant_user_fk
    FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id);