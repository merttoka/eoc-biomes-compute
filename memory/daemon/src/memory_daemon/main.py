from __future__ import annotations
import argparse
import logging
import signal
import sys
from pathlib import Path

from .config import Config
from . import store, watcher, osc_server


def main() -> int:
    p = argparse.ArgumentParser(prog="memory-daemon")
    p.add_argument("--snapshot-dir", type=Path, help="Folder to watch for .asset snapshots")
    p.add_argument("--db", type=Path, help="SQLite path (default: ./memory.db)")
    p.add_argument("--osc-port", type=int, help="OSC UDP port (default: 9100)")
    p.add_argument("--installation-id", type=int, help="Installation id for new snapshots")
    p.add_argument("-v", "--verbose", action="store_true")
    args = p.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )

    cfg = Config.from_env(snapshot_dir=args.snapshot_dir)
    if args.db: cfg.db_path = args.db.expanduser().resolve()
    if args.osc_port: cfg.osc_port = args.osc_port
    if args.installation_id: cfg.installation_id = args.installation_id

    log = logging.getLogger("memory")
    log.info("snapshot_dir=%s db=%s osc=%s:%d", cfg.snapshot_dir, cfg.db_path, cfg.osc_host, cfg.osc_port)

    conn = store.connect(cfg.db_path)
    indexed = watcher.replay_existing(conn, cfg.snapshot_dir, cfg.installation_id)
    log.info("replayed %d existing snapshots", indexed)

    obs = watcher.start(conn, cfg.snapshot_dir, cfg.installation_id)
    server, _ = osc_server.serve(cfg.osc_host, cfg.osc_port, conn)

    stop = False
    def handler(*_):
        nonlocal stop
        stop = True
    signal.signal(signal.SIGINT, handler)
    signal.signal(signal.SIGTERM, handler)

    log.info("daemon running. ctrl-c to stop.")
    signal.pause() if sys.platform != "win32" else None

    log.info("shutting down")
    obs.stop(); obs.join()
    server.shutdown()
    conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
