CREATE TABLE IF NOT EXISTS installations (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  location TEXT,
  started_at TEXT NOT NULL,
  ended_at TEXT,
  hardware_notes TEXT
);

CREATE TABLE IF NOT EXISTS param_snapshots (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  installation_id INTEGER REFERENCES installations(id),
  ts TEXT NOT NULL,
  source TEXT NOT NULL,
  filename TEXT NOT NULL,
  content_hash TEXT NOT NULL UNIQUE,
  param_blob TEXT,
  embedding_id TEXT,
  label TEXT
);

CREATE TABLE IF NOT EXISTS symbolic_tags (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  target_kind TEXT NOT NULL,
  target_id INTEGER NOT NULL,
  tag TEXT NOT NULL,
  note TEXT,
  author TEXT,
  ts TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_snapshots_installation ON param_snapshots(installation_id);
CREATE INDEX IF NOT EXISTS idx_snapshots_ts ON param_snapshots(ts);
CREATE INDEX IF NOT EXISTS idx_tags_target ON symbolic_tags(target_kind, target_id);
