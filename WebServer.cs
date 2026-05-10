using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace RemotePlay;

internal sealed class WebServer
{
    private static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv"];

    private readonly AppConfig _config;
    private HttpListener _listener = new();
    private readonly Action<string> _onPlay;
    private readonly Action _onStop;
    private readonly Action _onPause;

    public WebServer(AppConfig config, Action<string> onPlay, Action onStop, Action onPause)
    {
        _config = config;
        _onPlay = onPlay;
        _onStop = onStop;
        _onPause = onPause;
    }

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://*:{_config.Port}/");
        _listener.Start();
        Logger.Info($"Web server listening on port {_config.Port}");
        Task.Run(ListenLoopAsync);
    }

    public void Stop()
    {
        try { _listener.Stop(); } catch { }
    }

    private async Task ListenLoopAsync()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestSafe(ctx));
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error("Unexpected error in listen loop", ex);
            }
        }
    }

    private void HandleRequestSafe(HttpListenerContext ctx)
    {
        try { HandleRequest(ctx); }
        catch (Exception ex)
        {
            Logger.Error("Error handling request", ex);
            TrySendResponse(ctx, 500, "text/plain", "Internal server error");
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var urlPath = req.Url?.AbsolutePath ?? "/";

        Logger.Info($"{req.HttpMethod} {urlPath}");

        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

        switch (urlPath)
        {
            case "/" or "/index.html":
                TrySendResponse(ctx, 200, "text/html; charset=utf-8", HtmlPage);
                break;

            // Returns the subfolders and video files in a given directory.
            // Query param: dir=<base64-encoded absolute path>  (omit for root movies folder)
            case "/api/browse":
                HandleBrowse(ctx);
                break;

            case "/api/play":
                HandlePlay(ctx);
                break;

            case "/api/stop":
                _onStop();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            case "/api/pause":
                _onPause();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            default:
                TrySendResponse(ctx, 404, "text/plain", "Not found");
                break;
        }
    }

    private void HandleBrowse(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMoviesPath;
        var dirParam = ctx.Request.QueryString["dir"];

        string targetDir;
        if (string.IsNullOrWhiteSpace(dirParam))
        {
            targetDir = root;
        }
        else
        {
            targetDir = Encoding.UTF8.GetString(Convert.FromBase64String(dirParam));
            // Security: ensure path stays within the configured root
            if (!IsUnderRoot(targetDir, root))
            {
                TrySendResponse(ctx, 403, "text/plain", "Forbidden");
                return;
            }
        }

        if (!Directory.Exists(targetDir))
        {
            try { Directory.CreateDirectory(targetDir); } catch { }
            TrySendResponse(ctx, 200, "application/json",
                JsonSerializer.Serialize(new { folders = Array.Empty<object>(), files = Array.Empty<object>(), current = targetDir, isRoot = true }));
            return;
        }

        var folders = Directory.EnumerateDirectories(targetDir)
            .OrderBy(d => d)
            .Select(d => new
            {
                name = Path.GetFileName(d),
                dir = Convert.ToBase64String(Encoding.UTF8.GetBytes(d))
            })
            .ToArray();

        var files = Directory.EnumerateFiles(targetDir)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .Select(f => new
            {
                name = Path.GetFileNameWithoutExtension(f),
                path = Convert.ToBase64String(Encoding.UTF8.GetBytes(f))
            })
            .ToArray();

        // Parent dir (null if we're already at root)
        string? parentEncoded = null;
        var parent = Path.GetDirectoryName(targetDir);
        if (parent is not null && IsUnderRoot(parent, root) || parent == root)
            parentEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(parent));

        var result = JsonSerializer.Serialize(new
        {
            folders,
            files,
            current = Path.GetFileName(targetDir),
            currentFull = targetDir,
            parent = parentEncoded,
            isRoot = string.Equals(targetDir, root, StringComparison.OrdinalIgnoreCase)
        });

        TrySendResponse(ctx, 200, "application/json", result);
    }

    private void HandlePlay(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var filePath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));
        if (!File.Exists(filePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
            return;
        }

        _onPlay(filePath);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void TrySendResponse(HttpListenerContext ctx, int status,
        string contentType, string body)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentEncoding = Encoding.UTF8;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to send HTTP response", ex);
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static readonly string HtmlPage = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8"/>
        <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
        <title>RemotePlay</title>
        <style>
          *{box-sizing:border-box;margin:0;padding:0}
          body{background:#111;color:#eee;font-family:'Segoe UI',sans-serif;min-height:100vh;display:flex;flex-direction:column}
          header{background:#1a1a2e;padding:.8rem 1.2rem;display:flex;align-items:center;gap:.8rem;flex-wrap:wrap;position:sticky;top:0;z-index:10}
          h1{font-size:1.3rem;color:#e94560;white-space:nowrap}
          #search{background:#222;color:#eee;border:1px solid #444;padding:.45rem .7rem;border-radius:4px;font-size:.9rem;flex:1;min-width:120px}
          .btn{border:none;border-radius:4px;cursor:pointer;font-size:.85rem;padding:.45rem 1rem;color:#fff;transition:background .15s}
          .btn-red{background:#e94560}.btn-red:hover{background:#c73652}
          .btn-dim{background:#333}.btn-dim:hover{background:#555}
          #transport{display:flex;gap:.4rem;flex-wrap:wrap}
          #breadcrumb{background:#0d0d1a;padding:.5rem 1.2rem;font-size:.82rem;color:#888;display:flex;align-items:center;gap:.3rem;flex-wrap:wrap;border-bottom:1px solid #1e1e2e}
          #breadcrumb span{color:#aaa}
          #breadcrumb a{color:#00d4aa;cursor:pointer;text-decoration:none}.#breadcrumb a:hover{text-decoration:underline}
          #status{padding:.45rem 1.2rem;background:#0a0a0a;font-size:.8rem;color:#777;border-bottom:1px solid #1a1a1a}
          #browser{padding:1rem 1.2rem;flex:1}
          .section-label{color:#555;font-size:.75rem;text-transform:uppercase;letter-spacing:.08em;margin-bottom:.5rem;margin-top:1rem}
          .section-label:first-child{margin-top:0}
          .folder-list{display:flex;flex-direction:column;gap:.3rem;margin-bottom:.5rem}
          .folder-row{display:flex;align-items:center;gap:.6rem;background:#1a1a2e;border-radius:6px;padding:.55rem .9rem;cursor:pointer;transition:background .15s}
          .folder-row:hover{background:#252540}
          .folder-icon{font-size:1.1rem}
          .folder-name{font-size:.9rem;word-break:break-word}
          .movie-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:.7rem}
          .movie-card{background:#1e1e2e;border-radius:8px;padding:.8rem;display:flex;flex-direction:column;gap:.5rem;cursor:pointer;border:2px solid transparent;transition:transform .12s,box-shadow .12s,border-color .12s}
          .movie-card:hover{transform:translateY(-2px);box-shadow:0 5px 18px rgba(233,69,96,.25)}
          .movie-card.playing{border-color:#e94560}
          .movie-title{font-size:.85rem;line-height:1.3;word-break:break-word;flex:1}
          .play-btn{background:#e94560;color:#fff;border:none;padding:.35rem;border-radius:4px;cursor:pointer;font-size:.85rem;width:100%}
          .play-btn:hover{background:#c73652}
          #empty{text-align:center;padding:3rem;color:#555}
          .back-row{display:flex;align-items:center;gap:.6rem;background:#161624;border-radius:6px;padding:.55rem .9rem;cursor:pointer;margin-bottom:.5rem;border:1px solid #2a2a3e}
          .back-row:hover{background:#1e1e30}
        </style>
        </head>
        <body>
        <header>
          <h1>ðŸŽ¬ RemotePlay</h1>
          <input id="search" type="search" placeholder="Filter in this folderâ€¦" oninput="onSearch()"/>
          <div id="transport">
            <button class="btn btn-dim" onclick="api('/api/pause')">â¸ Pause</button>
            <button class="btn btn-dim" onclick="api('/api/stop')">â¹ Stop</button>
            <button class="btn btn-dim" onclick="browse(currentDir)">ðŸ”„</button>
          </div>
        </header>
        <div id="breadcrumb"></div>
        <div id="status">Loadingâ€¦</div>
        <div id="browser"></div>

        <script>
        let currentDir = null;   // null = root
        let currentData = null;
        let playingPath = null;

        async function browse(dirKey) {
          setStatus('Loadingâ€¦');
          currentDir = dirKey;
          document.getElementById('search').value = '';
          try {
            const url = dirKey ? '/api/browse?dir=' + encodeURIComponent(dirKey) : '/api/browse';
            const res = await fetch(url);
            if (!res.ok) { setStatus('Server error ' + res.status); return; }
            currentData = await res.json();
            render(currentData);
            setStatus(currentData.folders.length + ' folder(s), ' + currentData.files.length + ' video(s)');
          } catch(e) {
            setStatus('Error: ' + e);
          }
        }

        function onSearch() {
          if (!currentData) return;
          const q = document.getElementById('search').value.toLowerCase().trim();
          if (!q) { render(currentData); return; }
          const filtered = { ...currentData, files: currentData.files.filter(f => f.name.toLowerCase().includes(q)), folders: [] };
          render(filtered, true);
        }

        function render(data, searching) {
          // Breadcrumb
          const bc = document.getElementById('breadcrumb');
          bc.innerHTML = '<a onclick="browse(null)">ðŸ  Root</a>';
          if (!data.isRoot && data.currentFull) {
            bc.innerHTML += '<span>â€º</span><span>' + esc(data.current) + '</span>';
          }

          const browser = document.getElementById('browser');
          let html = '';

          // Back button
          if (!data.isRoot && data.parent != null && !searching) {
            html += `<div class="back-row" onclick="browse('${data.parent}')"><span>â¬…</span><span>Back</span></div>`;
          }

          // Folders
          if (data.folders.length && !searching) {
            html += '<div class="section-label">Folders</div><div class="folder-list">';
            html += data.folders.map(f =>
              `<div class="folder-row" onclick="browse('${f.dir}')"><span class="folder-icon">ðŸ“</span><span class="folder-name">${esc(f.name)}</span></div>`
            ).join('');
            html += '</div>';
          }

          // Movies
          if (data.files.length) {
            html += '<div class="section-label">Videos</div><div class="movie-grid">';
            html += data.files.map(f =>
              `<div class="movie-card ${f.path === playingPath ? 'playing' : ''}" id="card-${f.path}">
                <div class="movie-title">${esc(f.name)}</div>
                <button class="play-btn" onclick="play('${f.path}','${esc(f.name)}')">â–¶ Play</button>
              </div>`
            ).join('');
            html += '</div>';
          }

          if (!data.folders.length && !data.files.length) {
            html = '<div id="empty">No subfolders or video files here.</div>';
          }

          browser.innerHTML = html;
        }

        async function play(encodedPath, name) {
          playingPath = encodedPath;
          setStatus('Starting: ' + name);
          // Update card highlight without full re-render
          document.querySelectorAll('.movie-card').forEach(c => c.classList.remove('playing'));
          const card = document.getElementById('card-' + encodedPath);
          if (card) card.classList.add('playing');
          await api('/api/play?path=' + encodeURIComponent(encodedPath));
          setStatus('Now playing: ' + name);
        }

        async function api(url) {
          try { await fetch(url); }
          catch(e) { setStatus('Command failed: ' + e); }
        }

        function setStatus(msg) { document.getElementById('status').textContent = msg; }

        function esc(s) {
          return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');
        }

        browse(null);
        </script>
        </body>
        </html>
        """;
}
