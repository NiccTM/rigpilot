CREATE TABLE IF NOT EXISTS compatibility_reports (
    report_id TEXT PRIMARY KEY,
    object_key TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    raw_expires_utc TEXT NOT NULL,
    summary_expires_utc TEXT NOT NULL,
    delete_token_hash TEXT NOT NULL,
    app_version TEXT NOT NULL,
    os_build TEXT,
    cpu_name TEXT,
    gpu_names_json TEXT NOT NULL,
    motherboard_name TEXT,
    capability_counts_json TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_compatibility_reports_summary_expiry
ON compatibility_reports(summary_expires_utc);
