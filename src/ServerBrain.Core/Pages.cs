using System.Net;

namespace ServerBrain;

/// <summary>Small standalone HTML pages served by the proxy (cold-start splash, offline, access gate...).</summary>
internal static class Pages
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        :root{color-scheme:dark}*{box-sizing:border-box}
        body{margin:0;min-height:100vh;display:grid;place-items:center;font:15px/1.5 "Segoe UI Variable","Segoe UI",system-ui,sans-serif;color:#e9ecff;
          background:radial-gradient(1200px 700px at 15% 10%,#2a1f6b55,transparent 60%),radial-gradient(900px 600px at 90% 90%,#0e6a7a44,transparent 60%),#0a0b14}
        .card{width:min(520px,92vw);padding:34px 34px 28px;border-radius:22px;background:#ffffff0d;border:1px solid #ffffff1f;backdrop-filter:blur(18px);box-shadow:0 30px 80px #0008;text-align:center}
        h1{font-size:22px;margin:18px 0 6px}p{margin:6px 0;color:#aab0d6}small{color:#7c83ad}
        .orb{width:74px;height:74px;margin:0 auto;border-radius:50%;background:conic-gradient(from 0deg,var(--c,#8b7bff),#35e0c2,var(--c,#8b7bff));animation:spin 1.6s linear infinite;
          -webkit-mask:radial-gradient(circle 28px at center,transparent 97%,#000 100%);mask:radial-gradient(circle 28px at center,transparent 97%,#000 100%);filter:drop-shadow(0 0 14px var(--c,#8b7bff))}
        .orb.still{animation:none;opacity:.5;filter:none}
        @keyframes spin{to{transform:rotate(360deg)}}
        .log{margin:16px 0 0;padding:10px 12px;border-radius:12px;background:#0006;font:12px/1.5 ui-monospace,Consolas,monospace;color:#9fe8d8;text-align:left;max-height:96px;overflow:hidden;white-space:pre-wrap;word-break:break-all}
        button,.btn{margin-top:16px;padding:10px 20px;border-radius:12px;border:0;background:linear-gradient(135deg,#7c6bff,#35c9e0);color:#fff;font:600 14px "Segoe UI",system-ui,sans-serif;cursor:pointer;text-decoration:none;display:inline-block}
        input{width:100%;padding:12px 14px;border-radius:12px;border:1px solid #ffffff30;background:#0005;color:#fff;font:600 18px ui-monospace,Consolas,monospace;letter-spacing:4px;text-align:center;text-transform:uppercase;outline:none}
        input:focus{border-color:#8b7bff}.err{color:#ff8a9b}
        """;

    private static string Shell(string title, string body, string? color = null) =>
        $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{E(title)}</title><style>{Css}</style></head><body style=\"--c:{E(string.IsNullOrWhiteSpace(color) ? "#8b7bff" : color)}\"><div class=\"card\">{body}</div></body></html>";

    public static string Splash(string name, string? color, bool isPublic)
    {
        var log = isPublic ? "" : "<div class=\"log\" id=\"log\" hidden></div>";
        const string script = """
            <script>
            const msg=document.getElementById('msg'),log=document.getElementById('log');let n=0;
            async function poll(){try{const r=await fetch('/__sb/status',{cache:'no-store'});if(r.ok){const s=await r.json();
              if(s.ready){location.reload();return}
              if(log&&s.lines&&s.lines.length){log.hidden=false;log.textContent=s.lines.join('\n')}
              if(['crashed','failed','blocked'].includes(s.status)){msg.textContent=s.error||'The project could not start.';document.querySelector('.orb').classList.add('still');return}
            }}catch(e){}n++;setTimeout(poll,n<10?700:1500)}poll();
            </script>
            """;
        return Shell($"Starting {name}…",
            $"<div class=\"orb\"></div><h1>Waking up {E(name)}…</h1><p id=\"msg\">Starting the project. This page opens it automatically.</p>{log}<small>Server Brain</small>{script}", color);
    }

    public static string Offline(string name, string? color, string status, string? error, bool canStart)
    {
        const string script = """
            <script>
            const b=document.getElementById('b');if(b)b.onclick=async()=>{b.disabled=true;b.textContent='Starting…';await fetch('/__sb/start',{method:'POST'});location.reload()};
            </script>
            """;
        var msg = string.IsNullOrWhiteSpace(error) ? "Nothing is answering on this address right now." : E(error);
        return Shell($"{name} is not running",
            $"<div class=\"orb still\"></div><h1>{E(name)} is {E(status.ToLowerInvariant())}</h1><p>{msg}</p>{(canStart ? "<button id=\"b\">Start it</button>" : "")}{script}", color);
    }

    public static string Gate(string name, string? error) => Shell($"{name} – access code",
        $"<div class=\"orb still\"></div><h1>{E(name)}</h1><p>Enter the access code you were given.</p>" +
        "<form method=\"post\" action=\"/__sb/unlock\" autocomplete=\"off\"><input name=\"code\" maxlength=\"16\" autofocus placeholder=\"••••••••\">" +
        (error != null ? $"<p class=\"err\">{E(error)}</p>" : "") + "<button type=\"submit\">Open</button></form>");

    public static string NotFound(string host, int dashboardPort, bool showLinks) => Shell("Not found",
        $"<div class=\"orb still\"></div><h1>Nothing lives at {E(host)}</h1>" +
        $"<p>{(showLinks ? "No project uses this name. Open the dashboard to add one." : "This address is not available.")}</p>" +
        (showLinks ? $"<a class=\"btn\" href=\"http://127.0.0.1:{dashboardPort}\">Open dashboard</a>" : ""));

    public static string BadGateway(string name, int port, string? detail) => Shell($"{name} is not answering",
        $"<div class=\"orb still\"></div><h1>{E(name)} is not answering</h1><p>The project is running but nothing replied on port {port}.</p><p><small>{E(detail)}</small></p><button onclick=\"location.reload()\">Try again</button>");
}
