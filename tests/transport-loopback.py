"""Windows-only integration test. Uses the actual app and system OpenSSH, on loopback.

Optional test dependencies: Paramiko 3.5.1, Git for Windows sh.exe, and .NET Framework csc.
Usage: python transport-loopback.py PATH/TO/StantsiyaDesk.exe [--deps TEMP/PYTHONLIBS]
"""
import argparse
import base64
import hashlib
import logging
import os
from pathlib import Path
import shlex
import socket
import subprocess
import sys
import tempfile
import threading
import time
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    parser.add_argument("--deps", type=Path)
    parser.add_argument("--shell", type=Path, default=Path(os.environ["ProgramFiles"]) / "Git/usr/bin/sh.exe")
    args = parser.parse_args()
    if args.deps:
        sys.path.insert(0, str(args.deps.resolve()))
    import paramiko
    logging.getLogger("paramiko").disabled = True
    logging.getLogger("paramiko.transport").disabled = True
    csc = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    app, shell = args.app.resolve(strict=True), args.shell.resolve(strict=True)
    sources = Path(__file__).resolve().parent
    temp_root = Path(tempfile.gettempdir()).resolve()
    with tempfile.TemporaryDirectory(prefix="StantsiyaDesk-loopback-", dir=temp_root) as name:
        tmp = Path(name).resolve()
        assert tmp.parent == temp_root and tmp.name.startswith("StantsiyaDesk-loopback-")
        if app.suffix.lower() == ".zip":
            with zipfile.ZipFile(app) as package:
                payload = package.read("StantsiyaDesk.exe")
            app = tmp / "StantsiyaDesk.exe"
            app.write_bytes(payload)
        for binary, src in [("Runner.exe", "transport-runner.cs"), ("FixtureAdb.exe", "transport-adb-fixture.cs")]:
            subprocess.run([str(csc), "/nologo", "/platform:x64", "/codepage:65001", "/out:"+str(tmp / binary), str(sources / src)], check=True)
        key1, key2 = (paramiko.ECDSAKey.generate() for _ in range(2))
        keygen = Path(os.environ["WINDIR"]) / "System32/OpenSSH/ssh-keygen.exe"
        for keyfile in ("client-allowed", "client-denied"):
            subprocess.run([str(keygen), "-q", "-t", "ecdsa", "-b", "256", "-N", "", "-f", str(tmp/keyfile)], check=True)
        client = paramiko.ECDSAKey.from_private_key_file(str(tmp / "client-allowed"))
        digest = base64.b64encode(hashlib.sha256(key1.asbytes()).digest()).decode().rstrip("=")
        (tmp / "fingerprint").write_text("ecdsa-sha2-nistp256 SHA256:"+digest, encoding="ascii")
        control = tmp / "mode"
        control.write_text("normal", encoding="ascii")
        posix_path = subprocess.check_output([str(shell.parent / "cygpath.exe"), "-u", str(tmp / "remote-sources")], text=True).strip()

        class Server(paramiko.ServerInterface):
            def __init__(self):
                self.ready = threading.Event()

            def get_allowed_auths(self, username):
                return "publickey"

            def check_auth_publickey(self, username, key):
                return paramiko.AUTH_SUCCESSFUL if username == "root" and key == client else paramiko.AUTH_FAILED

            def check_channel_request(self, kind, chanid):
                return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

            def check_channel_exec_request(self, channel, command):
                if command != b"sh -s":
                    return False
                self.ready.set()
                return True

        def serve_one(conn):
            with conn:
                transport = paramiko.Transport(conn)
                try:
                    mode = control.read_text(encoding="utf-8-sig").strip()
                    transport.add_server_key(key2 if mode == "rotate" else key1)
                    server = Server()
                    transport.start_server(server=server)
                    chan = transport.accept(5)
                    if chan is None or not server.ready.wait(4):
                        return
                    chunks = bytearray()
                    while len(chunks) < 524288:
                        data = chan.recv(65536)
                        if not data:
                            break
                        chunks.extend(data)
                    script = chunks.decode("utf-8")
                    if "printf 'HELLO'" in script:
                        if mode == "stall":
                            time.sleep(1.5)
                            return
                    elif "f=/data/opt/etc/yse/sources\n" in script:
                        # Only the hard-coded fixture file is remapped. The rest of the supplied
                        # production backup/restore shell script runs via Git for Windows sh.
                        script = script.replace("f=/data/opt/etc/yse/sources\n", "f="+shlex.quote(posix_path)+"\n")
                        if mode == "cut-read":
                            chan.sendall(b"644 0 0\npartial")
                            return
                        if mode == "cut-before":
                            # Simulate a transfer interrupted before the shell received the restore body.
                            return
                    else:
                        chan.send_exit_status(52)
                        return
                    # With MSYS on NTFS, stat synthesizes mode bits using the calling process's
                    # umask. Match the restore script's umask in all sessions on the stand.
                    proc = subprocess.run([str(shell), "-s"], input=("umask 077\n"+script).encode(), capture_output=True, timeout=12)
                    if mode == "cut-after" and b"mv -f" in chunks:
                        if not proc.stdout.rstrip().endswith(b"_0"):
                            print("Fixture restore did not commit:", proc.stdout[-120:], proc.stderr[-1800:], file=sys.stderr, flush=True)
                        return
                    if proc.stdout:
                        chan.sendall(proc.stdout)
                    if proc.stderr:
                        chan.sendall_stderr(proc.stderr)
                    chan.send_exit_status(proc.returncode)
                except (EOFError, OSError, paramiko.SSHException):
                    # Normal for host scanning, rejected pins and injected disconnects.
                    pass
                finally:
                    transport.close()

        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen(20)
            listener.settimeout(.2)
            stop = threading.Event()
            def accept():
                while not stop.is_set():
                    try:
                        conn, _ = listener.accept()
                    except socket.timeout:
                        continue
                    except OSError:
                        break
                    threading.Thread(target=serve_one, args=(conn,), daemon=True).start()
            worker = threading.Thread(target=accept, daemon=True)
            worker.start()
            try:
                subprocess.run([str(tmp / "Runner.exe"), str(app), str(tmp), str(listener.getsockname()[1])], check=True, timeout=150)
            finally:
                stop.set()
                worker.join(1)
                logging.shutdown()


if __name__ == "__main__":
    main()
