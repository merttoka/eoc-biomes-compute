from __future__ import annotations
import logging
import sqlite3
from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import ThreadingOSCUDPServer
from threading import Thread

log = logging.getLogger(__name__)


def build_dispatcher(conn: sqlite3.Connection) -> Dispatcher:
    d = Dispatcher()

    def ping(addr, *args):
        log.info("ping from %s args=%s", addr, args)

    def count(addr, *args):
        n = conn.execute("SELECT COUNT(*) FROM param_snapshots").fetchone()[0]
        log.info("count: %d snapshots", n)

    d.map("/memory/ping", ping)
    d.map("/memory/count", count)
    return d


def serve(host: str, port: int, conn: sqlite3.Connection) -> tuple[ThreadingOSCUDPServer, Thread]:
    server = ThreadingOSCUDPServer((host, port), build_dispatcher(conn))
    t = Thread(target=server.serve_forever, daemon=True, name="osc-server")
    t.start()
    log.info("OSC server listening on %s:%d", host, port)
    return server, t
