from __future__ import annotations
import hashlib
import sqlite3
from datetime import datetime, timezone
from pathlib import Path

SCHEMA_PATH = Path(__file__).parent / "schema.sql"


def connect(db_path: Path) -> sqlite3.Connection:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(db_path, isolation_level=None)
    conn.row_factory = sqlite3.Row
    conn.executescript(SCHEMA_PATH.read_text())
    return conn


def hash_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def ingest_snapshot(conn: sqlite3.Connection, path: Path, installation_id: int, source: str = "unity:11.0_Biomes") -> int | None:
    """Insert a snapshot row. Idempotent on content_hash. Returns row id if inserted, None if duplicate."""
    digest = hash_file(path)
    ts = datetime.now(timezone.utc).isoformat()
    cur = conn.execute(
        """INSERT OR IGNORE INTO param_snapshots
           (installation_id, ts, source, filename, content_hash)
           VALUES (?, ?, ?, ?, ?)""",
        (installation_id, ts, source, path.name, digest),
    )
    return cur.lastrowid if cur.rowcount else None
