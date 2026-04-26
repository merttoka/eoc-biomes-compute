from __future__ import annotations
import os
from dataclasses import dataclass
from pathlib import Path


@dataclass
class Config:
    snapshot_dir: Path
    db_path: Path
    osc_host: str = "127.0.0.1"
    osc_port: int = 9100
    installation_id: int = 1

    @classmethod
    def from_env(cls, snapshot_dir: Path | None = None) -> "Config":
        return cls(
            snapshot_dir=Path(snapshot_dir or os.environ.get("MEMORY_SNAPSHOT_DIR", "./snapshots")).expanduser().resolve(),
            db_path=Path(os.environ.get("MEMORY_DB_PATH", "./memory.db")).expanduser().resolve(),
            osc_host=os.environ.get("MEMORY_OSC_HOST", "127.0.0.1"),
            osc_port=int(os.environ.get("MEMORY_OSC_PORT", "9100")),
            installation_id=int(os.environ.get("MEMORY_INSTALLATION_ID", "1")),
        )
