using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MacDock.Core;

/// <summary>
/// 通过网易云自带的 CEF 调试端口（--remote-debugging-port=9222）在它页面里执行一小段 JS。
/// 用途：读取"这首歌是否已红心"、以及热键失效时点页面里的红心。
///
/// 关键点：
/// · 127.0.0.1 不能走系统代理（装了加速器/代理时会被吞掉，表现为超时）；
/// · 连接是单例，必须串行使用 —— ClientWebSocket 不支持并发 Send。
/// </summary>
internal sealed class NetEaseBridge : IDisposable
{
    const int Port = 9222;

    static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = false,
        Proxy = null,
    })
    { Timeout = TimeSpan.FromSeconds(3) };

    readonly SemaphoreSlim _gate = new(1, 1);
    ClientWebSocket? _socket;
    int _nextId = 1;
    string _pageId = "";

    const string LikeStateScript = """
        (() => {
          if (document.querySelector('[aria-label="likenumber_red"]')) return "liked";
          if (document.querySelector('[aria-label="like_number"]')) return "plain";
          return "unknown";
        })()
        """;

    const string LikeClickScript = """
        (() => {
          const el = document.querySelector('[aria-label="like_number"], [aria-label="likenumber_red"]');
          if (!el) return "no-button";
          el.click();
          return "clicked";
        })()
        """;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    // ── 连接 ────────────────────────────────────────────────────────────
    async Task<bool> ConnectCoreAsync(CancellationToken token)
    {
        if (_socket is { State: WebSocketState.Open }) return true;

        try { _socket?.Dispose(); } catch (Exception) { }
        _socket = null;

        if (_pageId.Length == 0) _pageId = await FindPageIdAsync(token);
        if (_pageId.Length == 0) return false;

        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/devtools/page/{_pageId}"), token);
        _socket = socket;
        return true;
    }

    static async Task<string> FindPageIdAsync(CancellationToken token)
    {
        try
        {
            string payload = await Http.GetStringAsync($"http://127.0.0.1:{Port}/json/list", token);
            using var document = JsonDocument.Parse(payload);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("url", out var urlProperty)) continue;
                string url = urlProperty.GetString() ?? "";
                if (!url.Contains("orpheus", StringComparison.OrdinalIgnoreCase)) continue;
                if (element.TryGetProperty("id", out var id)) return id.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            Log.Info("Bridge: page lookup failed - " + ex.Message);
        }
        return "";
    }

    public async Task<bool> ConnectAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return await ConnectCoreAsync(token); }
        catch (Exception ex) { Log.Info("Bridge: connect failed - " + ex.Message); return false; }
        finally { _gate.Release(); }
    }

    // ── 执行 JS ─────────────────────────────────────────────────────────
    public async Task<JsonElement?> EvalAsync(string expression, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!await ConnectCoreAsync(token)) return null;
            var socket = _socket;
            if (socket is null) return null;

            int id = _nextId++;
            string command = JsonSerializer.Serialize(new
            {
                id,
                method = "Runtime.evaluate",
                @params = new { expression, returnByValue = true, awaitPromise = true },
            });
            await socket.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Text, true, token);

            var buffer = new byte[32 * 1024];
            var builder = new StringBuilder();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage) break;
                if (builder.Length > 256 * 1024) break;
            }

            using var document = JsonDocument.Parse(builder.ToString());
            if (!document.RootElement.TryGetProperty("result", out var resultElement)) return null;
            if (!resultElement.TryGetProperty("result", out var value)) return null;
            if (!value.TryGetProperty("value", out var payload)) return JsonDocument.Parse("null").RootElement.Clone();
            return payload.Clone();
        }
        catch (Exception ex)
        {
            Log.Info("Bridge: eval failed - " + ex.Message);
            try { _socket?.Dispose(); } catch (Exception) { }
            _socket = null;
            _pageId = "";
            return null;
        }
        finally { _gate.Release(); }
    }

    // ── 业务 ────────────────────────────────────────────────────────────
    /// <summary>读取当前歌曲是否已收藏；读不到返回 null（不猜）。</summary>
    public async Task<bool?> GetLikeStateAsync(CancellationToken token = default)
    {
        var value = await EvalAsync(LikeStateScript, token);
        if (value is null || value.Value.ValueKind != JsonValueKind.String) return null;
        return value.Value.GetString() switch
        {
            "liked" => true,
            "plain" => false,
            _ => (bool?)null,
        };
    }

    /// <summary>点页面里的红心（热键失效时的后备），返回是否点到了。</summary>
    public async Task<bool> ClickLikeInPageAsync(CancellationToken token = default)
    {
        var value = await EvalAsync(LikeClickScript, token);
        return value is { ValueKind: JsonValueKind.String } && value.Value.GetString() == "clicked";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    const byte VK_CONTROL = 0x11;
    const byte VK_MENU = 0x12;
    const byte VK_L = 0x4C;
    const uint KEYUP = 0x0002;

    /// <summary>网易云的"喜欢歌曲"全局热键：Ctrl + Alt + L。</summary>
    public static bool SendLikeHotkey()
    {
        try
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            Thread.Sleep(15);
            keybd_event(VK_L, 0, 0, UIntPtr.Zero);
            Thread.Sleep(15);
            keybd_event(VK_L, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("Bridge: hotkey failed - " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        try { _socket?.Dispose(); } catch (Exception) { }
        _socket = null;
        _gate.Dispose();
    }
}
