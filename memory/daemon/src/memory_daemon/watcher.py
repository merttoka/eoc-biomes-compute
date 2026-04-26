from __future__ import annotations
import logging
import sqlite3
from pathlib import Path
from watchdog.events import FileSystemEventHandler, FileCreatedEvent
from watchdog.observers import Observer

from . import store

log = logging.getLogger(__name__)


class SnapshotHandler(FileSystemEventHandler):
    def __init__(self, conn: sqlite3.Connection, installation_id: int):
        self.conn = conn
        self.installation_id = installation_id

    def on_created(self, event):
        if event.is_directory or not event.src_path.endswith(".asset"):
            return
        path = Path(event.src_path)
        try:
            row_id = store.ingest_snapshot(self.conn, path, self.installation_id)
            if row_id:
                log.info("ingested snapshot id=%s file=%s", row_id, path.name)
            else:
                log.debug("duplicate snapshot skipped: %s", path.name)
        except Exception:
            log.exception("ingest failed for %s", path)


def replay_existing(conn: sqlite3.Connection, snapshot_dir: Path, installation_id: int) -> int:
    """Bootstrap: walk folder once and ingest anything not already in DB."""
    count = 0
    for p in sorted(snapshot_dir.glob("*.asset")):
        if store.ingest_snapshot(conn, p, installation_id):
            count += 1
    return count


def start(conn: sqlite3.Connection, snapshot_dir: Path, installation_id: int) -> Observer:
    snapshot_dir.mkdir(parents=True, exist_ok=True)
    handler = SnapshotHandler(conn, installation_id)
    obs = Observer()
    obs.schedule(handler, str(snapshot_dir), recursive=False)
    obs.start()
    return obs
