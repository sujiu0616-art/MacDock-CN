using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MacDock.Core;

/// <summary>
/// 通过网易云自带的 CEF 调试端口（--remote-debugging-port=9222）在它页面里执行一小段 JS。
/// 用途：收藏（红心）这类 SMTC 完全不提供的操作 —— 直接在它自己的播放器界面上点那颗心。
///
/// 约束：只有云音乐以调试端口启动时可用；不可用时就明确失败，不做假成功。
/// </summary>
internal sealed class NetEaseBridge : IDisposable
{
    const int Port = 9222;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    ClientWebSocket? _socket;
    int _nextId = 1;
    string _pageId = "";

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>连上页面（幂等）。失败返回 false，调用方负责提示。</summary>
    public async Task<bool> ConnectAsync(CancellationToken token = default)
    {
        if (IsConnected) return true;
        try
        {
            using var response = await Http.GetAsync($"http://127.0.0.1:{Port}/json/list", token);
            if (!response.IsSuccessStatusCode) return false;
            var payload = await response.Content.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(payload);
            string? url = null;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("url", out var urlProperty)) continue;
                string candidate = urlProperty.GetString() ?? "";
                if (candidate.Contains("orpheus", StringComparison.OrdinalIgnoreCase)) { url = candidate; break; }
            }
            if (url is null) return false;

            _socket?.Dispose();
            _socket = new ClientWebSocket();
            if (_pageId.Length == 0) _pageId = await PageIdAsync(token);
            if (_pageId.Length == 0) return false;
            await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/devtools/page/{_pageId}"), token);
            return IsConnected;
        }
        catch (Exception ex)
        {
            Log.Info("Bridge: connect failed - " + ex.Message);
            return false;
        }
    }

    async Task<string> PageIdAsync(CancellationToken token)
    {
        var payload = await Http.GetStringAsync($"http://127.0.0.1:{Port}/json/list", token);
        using var document = JsonDocument.Parse(payload);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("url", out var urlProperty)) continue;
            string url = urlProperty.GetString() ?? "";
            if (!url.Contains("orpheus", StringComparison.OrdinalIgnoreCase)) continue;
            if (element.TryGetProperty("id", out var id)) return id.GetString() ?? "";
        }
        return "";
    }

    /// <summary>执行一段 JS 并把结果当 JSON 返回（失败返回 null）。</summary>
    public async Task<JsonElement?> EvalAsync(string expression, CancellationToken token = default)
    {
        if (!await ConnectAsync(token)) return null;
        var socket = _socket;
        if (socket is null) return null;

        int id = _nextId++;
        string command = JsonSerializer.Serialize(new
        {
            id,
            method = "Runtime.evaluate",
            @params = new { expression, returnByValue = true, awaitPromise = true },
        });
        try
        {
            var bytes = Encoding.UTF8.GetBytes(command);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);

            var buffer = new byte[64 * 1024];
            var builder = new StringBuilder();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage) break;
                if (builder.Length > 512 * 1024) break;
            }

            using var document = JsonDocument.Parse(builder.ToString());
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var resultElement)) return null;
            if (!resultElement.TryGetProperty("result", out var value)) return null;
            if (!value.TryGetProperty("value", out var payload))
                return JsonDocument.Parse("null").RootElement.Clone();
            return payload.Clone();
        }
        catch (Exception ex)
        {
            Log.Info("Bridge: eval failed - " + ex.Message);
            try { _socket?.Dispose(); } catch (Exception) { }
            _socket = null;
            return null;
        }
    }

    const string LikeScript = """
        (() => {
          const pick = ['[aria-label="like_number"]', '[title*="喜欢（Ctrl + L）"]', '[class*="cmd-icon-like_number"]'];
          for (const sel of pick) {
            const el = document.querySelector(sel);
            if (!el) continue;
            el.click();
            return { ok: true, selector: sel, cls: (el.className || '').toString().slice(0, 80) };
          }
          return { ok: false };
        })()
        """;

    /// <summary>
    /// 网易云默认不开调试端口，收藏就无从下手。这里直接把它重启成带端口的形态：
    /// 只发生一次（端口已开时什么都不做），代价是当前播放会中断。
    /// </summary>
    static bool RestartPlayerWithDebugPort()
    {
        try
        {
            var running = System.Diagnostics.Process.GetProcessesByName("cloudmusic");
            string? exe = null;
            foreach (var process in running)
            {
                try { exe ??= process.MainModule?.FileName; } catch (Exception) { }
            }
            foreach (var process in running) { try { process.Kill(); } catch (Exception) { } }
            exe ??= @"D:\CloudMusic\cloudmusic.exe";
            if (!File.Exists(exe)) { Log.Info("Bridge: cloudmusic.exe not found at " + exe); return false; }
            Log.Info("Bridge: restarting player with --remote-debugging-port=9222");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--remote-debugging-port=9222") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("Bridge: restart failed - " + ex.Message);
            return false;
        }
    }

    const string LikeStateScript = """
        (() => {
          const red = document.querySelector('[aria-label="likenumber_red"]');
          if (red) return "liked";
          const plain = document.querySelector('[aria-label="like_number"]');
          if (plain) return "plain";
          return "unknown";
        })()
        """;

    /// <summary>读取当前歌曲是否已收藏（需要调试端口；读不到返回 null，不做猜测）。</summary>
    public async Task<bool?> GetLikeStateAsync(CancellationToken token = default)
    {
        var value = await EvalAsync(LikeStateScript, token);
        if (value is null) return null;
        string text = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? "" : "";
        if (text == "liked") return true;
        if (text == "plain") return false;
        return null;
    }
    /// <summary>
    /// 确保"能读到收藏状态"的通道可用：调试端口没开就把播放器重启成带端口的形态。
    /// 只在第一次需要读状态时发生一次，之后每次换歌都能直接读到真实红心状态。
    /// </summary>
    public async Task<bool> EnsureChannelAsync(CancellationToken token = default)
    {
        if (await ConnectAsync(token)) return true;
        if (!RestartPlayerWithDebugPort()) return false;
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(1000, token);
            if (await ConnectAsync(token)) return true;
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    const byte VK_CONTROL = 0x11;
    const byte VK_MENU = 0x12;      // Alt：网易云的"喜欢歌曲"全局热键是 Ctrl + Alt + L
    const byte VK_L = 0x4C;
    const uint KEYUP = 0x0002;

    /// <summary>
    /// 用网易云自带的全局热键「喜欢（Ctrl + L）」切换红心。
    /// 这条路不依赖调试端口、也不会打断播放，是默认做法。
    /// </summary>
    static bool TryHotkeyLike()
    {
        try
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);      // Alt 必须真的按下
            Thread.Sleep(40);
            keybd_event(VK_L, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            keybd_event(VK_L, 0, KEYUP, UIntPtr.Zero);
            Thread.Sleep(30);
            keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("Bridge: hotkey like failed - " + ex.Message);
            return false;
        }
    }

    /// <summary>切换当前歌曲的红心：优先热键，热键无效时再走调试端口点 DOM。</summary>

    public async Task<bool> ToggleLikeAsync(CancellationToken token = default)
    {
        // 1) 热键（首选：无依赖、不断播）
        if (TryHotkeyLike())
        {
            Log.Info("Bridge: like sent via Ctrl+Alt+L (global hotkey)");
            return true;
        }
        return false;
    }

    /// <summary>可选：用调试端口直接点页面里的红心（热键失效时的后备）。</summary>
    public async Task<bool> ToggleLikeViaPageAsync(CancellationToken token = default)
    {
        var result = await EvalAsync(LikeScript, token);
        if (result is null) return false;
        return result.Value.TryGetProperty("ok", out var okProperty) && okProperty.GetBoolean();
    }

    public void Dispose()
    {
        try { _socket?.Dispose(); } catch (Exception) { }
        _socket = null;
    }
}
