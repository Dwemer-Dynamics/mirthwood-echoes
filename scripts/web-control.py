"""Local Web UI and switches for the fixed Echoes systemd unit."""
from http.client import HTTPConnection
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hashlib
import json
import os
from pathlib import Path
import select
import subprocess
import sys
import threading
import time
from urllib.parse import urlsplit

APP = Path(os.environ.get("MIRTHWOOD_ECHOES_APP",Path(__file__).resolve().parent))
GAME = Path(os.environ["MIRTHWOOD_ECHOES_GAME"])
UNIT = "mirthwood-echoes.service"
SYSTEMCTL = "/usr/bin/systemctl"
BACKEND = 17842
SWITCH = threading.Lock()
STATIC = {"": ("index.html","text/html"), "index.html": ("index.html","text/html"), "app.js": ("app.js","text/javascript"),
          "journal.js": ("journal.js","text/javascript"), "style.css": ("style.css","text/css"), "valley.webp": ("valley.webp","image/webp")}

def running():
    return subprocess.run([SYSTEMCTL,"is-active","--quiet",UNIT],check=False).returncode == 0

def switch(operation):
    if operation=="start" and window.closing.is_set():raise OSError("The companion is closing.")
    subprocess.run(["/usr/bin/sudo","-n",SYSTEMCTL,operation,UNIT],check=True,timeout=40,capture_output=True)
    window.notify("backend-on" if operation=="start" else "backend-off")

def backend_ready():
    for _ in range(60):
        if window.closing.is_set():raise OSError("The companion is closing.")
        conn=HTTPConnection("127.0.0.1",BACKEND,timeout=1)
        try:
            conn.request("GET","/api/health")
            response=conn.getresponse()
            if response.status==200 and json.load(response).get("application")=="mirthwood-echoes":
                return
        except (OSError,ValueError):
            pass
        finally:
            conn.close()
        time.sleep(.2)
    raise TimeoutError("Echoes has not finished starting.")

class DesktopWindow:
    def __init__(self):
        self.closing=threading.Event()
        self.lock=threading.Lock()
        self.playing=False
        self.pending_until=0.
        self.error=""

    def notify(self,line):
        try:print(line,flush=True)
        except (BrokenPipeError,OSError):self.closing.set()

    def game(self):
        with self.lock:
            if self.pending_until and time.monotonic()>=self.pending_until:
                self.pending_until=0.
                self.error="Steam has not opened Mirthwood. Check Steam, then try Start Game again."
            state="running" if self.playing else "starting" if self.pending_until else "idle"
            return {"state":state,"error":self.error}

    def launch(self):
        with self.lock:
            if self.playing or self.pending_until>time.monotonic():return
            self.pending_until=time.monotonic()+120
            self.error=""
            self.notify("play")

    def watch(self,server):
        # stdin belongs exclusively to the visible Windows host. EOF also covers an abrupt host exit.
        buf=b""
        try:
            while not self.closing.is_set():
                if not select.select([sys.stdin],[],[],10)[0]:break
                chunk=os.read(sys.stdin.fileno(),4096)
                if not chunk:break
                buf+=chunk
                while b"\n" in buf:
                    raw,buf=buf.split(b"\n",1)
                    line=raw.strip().decode("ascii",errors="ignore")
                    if line in ("playing","idle","launch-failed"):
                        with self.lock:
                            self.playing=line=="playing"
                            if self.playing:self.pending_until=0.;self.error=""
                            if line=="launch-failed":
                                self.pending_until=0.;self.error="Steam could not be opened. Check its installation and try again."
        finally:
            self.closing.set();server.shutdown()

window=DesktopWindow()

def check_module():
    backend_ready()
    conn=HTTPConnection("127.0.0.1",BACKEND,timeout=35)
    try:
        conn.request("POST","/api/module/check",b"{}",{"Content-Type":"application/json"})
        response=conn.getresponse()
        if response.status!=200:raise OSError("Module check failed.")
        return json.load(response)
    finally:conn.close()

def module_status():
    manifest=json.loads((APP/"bundle/manifest.json").read_text())
    def result(state):
        return {"state":state,"version":manifest["version"]}
    if not (GAME/"Mirthwood.exe").is_file():return result("game_missing")
    module=next(f for f in manifest["files"] if f["kind"]=="module")
    module_path=GAME/module["path"]
    if not module_path.is_file():return result("update" if (GAME/"BepInEx/plugins/MirthwoodAI/MirthwoodAI.dll").is_file() else "missing")
    for item in manifest["files"]:
        path=GAME/item["path"]
        if not path.is_file():return result("update")
        if item["kind"]!="default":
            with path.open("rb") as f:
                if hashlib.file_digest(f,"sha256").hexdigest().lower()!=item["sha256"].lower():return result("update")
    return result("current")

class Handler(BaseHTTPRequestHandler):
    protocol_version="HTTP/1.1"
    def log_message(self,*args): pass
    def headers_out(self,status,mime,cache="no-store"):
        self.send_response(status)
        self.send_header("Content-Type",mime)
        self.send_header("Cache-Control",cache)
        self.send_header("X-Content-Type-Options","nosniff")
        self.send_header("Content-Security-Policy","default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
    def send_bytes(self,status,mime,body):
        self.headers_out(status,mime)
        self.send_header("Content-Length",str(len(body)));self.end_headers();self.wfile.write(body)
    def reply(self,status,obj):
        self.send_bytes(status,"application/json; charset=utf-8",json.dumps(obj,ensure_ascii=False).encode())
    def proxy(self,body):
        conn=HTTPConnection("127.0.0.1",BACKEND,timeout=120)
        try:
            headers={k:self.headers[k] for k in ("Host","Content-Type","Origin","Accept","Range","If-Range") if k in self.headers}
            conn.request(self.command,self.path,body=body,headers=headers)
            r=conn.getresponse()
        except OSError:
            conn.close();self.reply(503,{"error":"The Echoes server is off or starting."});return
        try:
            self.headers_out(r.status,r.getheader("Content-Type","application/octet-stream"))
            if r.status in (204,304):
                self.end_headers();return
            for key in ("Accept-Ranges","Content-Range"):
                if value:=r.getheader(key):self.send_header(key,value)
            if r.getheader("Content-Length") is not None:
                self.send_header("Content-Length",r.getheader("Content-Length"));self.end_headers()
                while chunk:=r.read1(32768):self.wfile.write(chunk)
                return
            self.send_header("Transfer-Encoding","chunked");self.end_headers()
            while chunk:=r.read1(32768):
                self.wfile.write(("%x\r\n"%len(chunk)).encode()+chunk+b"\r\n");self.wfile.flush()
            self.wfile.write(b"0\r\n\r\n")
        except (ConnectionError,TimeoutError):
            self.close_connection=True
        finally:conn.close()
    def handle_request(self):
        try:
            host=self.headers.get("Host","")
            if urlsplit("//"+host).hostname not in ("localhost","127.0.0.1","::1") or self.headers.get("Origin","http://"+host)!="http://"+host:
                self.reply(403,{"error":"Local requests only."});return
            path=urlsplit(self.path).path;body=None
            if self.command!="GET":
                if self.headers.get_content_type()!="application/json" or self.headers.get("Transfer-Encoding"):
                    self.reply(415,{"error":"Send JSON."});return
                length=int(self.headers.get("Content-Length",0))
                if not 0<=length<=32768:self.reply(413,{"error":"Request too large."});return
                body=self.rfile.read(length)
            if path=="/api/control" and self.command=="GET":
                self.reply(200,{"running":running(),"release":APP.resolve().name,"module":module_status(),"game":window.game()});return
            if path=="/api/control/game" and self.command=="GET":
                state=window.game()
                if urlsplit(self.path).query=="session":
                    state.update(running=running(),release=APP.resolve().name)
                self.reply(200,state);return
            if path=="/api/control/exit" and self.command=="POST":
                window.closing.set()
                try:self.reply(200,{"closing":True})
                finally:window.notify("exit")
                return
            if path in ("/api/control/server","/api/control/module","/api/control/game") and self.command=="POST":
                payload=json.loads(body or b"{}")
                with SWITCH:
                    if window.closing.is_set():self.reply(503,{"error":"The companion is closing."});return
                    if path.endswith("/server"):
                        desired=payload.get("running") if isinstance(payload,dict) else None
                        if type(desired) is not bool:self.reply(400,{"error":"Choose on or off."});return
                        switch("start" if desired else "stop")
                        if desired:backend_ready()
                        self.reply(200,{"running":running()});return
                    was_on=running()
                    launching=path.endswith("/game")
                    keep_running=was_on
                    try:
                        if not was_on:switch("start")
                        result=check_module()
                        if launching:
                            game=window.game()
                            if result.get("status")!="ready" and not (game["state"]=="running" and result.get("status")=="waiting_for_game_exit"):
                                self.reply(409,{"error":result.get("detail","The game module is unavailable.")});return
                            if window.closing.is_set():self.reply(503,{"error":"The companion is closing."});return
                            window.launch();keep_running=True
                            self.reply(200,window.game());return
                    finally:
                        if not keep_running:switch("stop")
                    if not was_on and result.get("status")=="waiting_for_game_exit":
                        result["detail"]="Close Mirthwood, then check again or switch Echoes on to apply the update."
                    self.reply(200,result);return
            if path.startswith("/api/control"):self.reply(404,{"error":"Unknown control."});return
            if path.startswith("/api/"):self.proxy(body);return
            asset=STATIC.get(path.removeprefix("/"))
            if self.command=="GET" and asset:
                self.send_bytes(200,asset[1],(APP/"wwwroot"/asset[0]).read_bytes());return
            self.reply(404,{"error":"Not found."})
        except (BrokenPipeError,ConnectionResetError):
            self.close_connection=True
        except (ValueError,TypeError):
            self.reply(400,{"error":"Invalid request."})
        except (OSError,subprocess.SubprocessError):
            self.reply(503,{"error":"The operation did not finish. Check the Echoes service in Diagnostics and retry."})
    do_GET=do_PUT=do_POST=handle_request

if __name__=="__main__":
    if sys.argv[1:]!=["--desktop"]:raise SystemExit("Open the Mirthwood: Echoes desktop companion.")
    with ThreadingHTTPServer(("127.0.0.1",17841),Handler) as server:
        threading.Thread(target=window.watch,args=(server,),daemon=True).start()
        window.notify("ready")
        try:server.serve_forever(poll_interval=.1)
        finally:
            window.closing.set()
            # Stop even an activating backend. In-flight HTTP requests cannot keep the window's unit alive.
            switch("stop")

