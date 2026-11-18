using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AIBlackHole;

static class BrowserTest
{
    // Acceptance helper only. Edge is never required for inference or used as a bundled application dependency.
    public static async Task<object> Run(Paths paths, Gateway gateway)
    {
        string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        if (!File.Exists(edge)) return new { status = "NOT_RUN", reason = "Edge not installed; test with the target machine's browser." };
        int port = Native.FreePort(); string profile = paths.Data("browser-acceptance");
        var info = new ProcessStartInfo(edge) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-extensions", "--disable-sync", "--disable-crash-reporter", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + port, "--user-data-dir=" + profile, "--window-size=1440,1000", "about:blank" }) info.ArgumentList.Add(arg);
        using var browser = Process.Start(info)!; using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4) };
        using var ws = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); int sequence = 0;
        async Task<JsonNode?> Call(string method, object parameters)
        {
            int id = ++sequence; byte[] data = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { id, method, @params = parameters }));
            await ws.SendAsync(data, WebSocketMessageType.Text, true, timeout.Token);
            for (;;)
            {
                using var buffer = new MemoryStream(); var bytes = new byte[65536]; WebSocketReceiveResult receive;
                do { receive = await ws.ReceiveAsync(bytes, timeout.Token); if (receive.MessageType == WebSocketMessageType.Close) throw new IOException("Browser closed early."); buffer.Write(bytes, 0, receive.Count); } while (!receive.EndOfMessage);
                var message = JsonNode.Parse(buffer.ToArray());
                if (message?["id"]?.GetValue<int>() != id) continue;
                if (message["error"] != null) throw new IOException("Browser automation command failed."); return message["result"];
            }
        }
        async Task<JsonNode?> Evaluate(string expression) => (await Call("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }))?["result"]?["value"];
        try
        {
            string? socket = null;
            for (int i = 0; i < 40; i++)
            {
                try { var tabs = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list")); socket = tabs?.AsArray().FirstOrDefault(t => t?["type"]?.GetValue<string>() == "page")?["webSocketDebuggerUrl"]?.GetValue<string>(); if (socket != null) break; } catch (HttpRequestException) { }
                await Task.Delay(250, timeout.Token);
            }
            if (socket == null) throw new IOException("Browser debugging endpoint unavailable.");
            await ws.ConnectAsync(new Uri(socket), timeout.Token);
            await Call("Page.enable", new { }); await Call("Runtime.enable", new { });
            await Call("Page.navigate", new { url = gateway.ChatUrl + "&page=chat" });
            bool ready = false;
            for (int i = 0; i < 50; i++) { ready = (await Evaluate("!!document.getElementById('online') && document.getElementById('online').textContent === 'MODEL READY'"))?.GetValue<bool>() == true; if (ready) break; await Task.Delay(300, timeout.Token); }
            if (!ready) throw new IOException("Packaged browser UI did not reach Ready.");
            await Evaluate("document.getElementById('prompt').value='Reply with only the answer: 9 plus 6';document.getElementById('chat-form').requestSubmit();true");
            string reply = "";
            for (int i = 0; i < 180; i++)
            {
                reply = (await Evaluate("document.querySelector('.message.assistant .text')?.textContent || ''"))?.GetValue<string>() ?? "";
                bool finished = (await Evaluate("typeof controller !== 'undefined' && controller === null && !!document.querySelector('.message.assistant')"))?.GetValue<bool>() == true;
                if (finished) break; await Task.Delay(500, timeout.Token);
            }
            bool erased = (await Evaluate("location.hash === ''"))?.GetValue<bool>() == true;
            var screenshot = await Call("Page.captureScreenshot", new { format = "png", captureBeyondViewport = false });
            string image = paths.Data("browser-acceptance.png"); await File.WriteAllBytesAsync(image, Convert.FromBase64String(screenshot!["data"]!.GetValue<string>()));
            var result = new { status = !string.IsNullOrWhiteSpace(reply) && erased ? "PASS" : "FAIL", reply, launchFragmentErased = erased, screenshot = image, browser = "installed Edge in an isolated test profile", title = (await Evaluate("document.title"))?.GetValue<string>() };
            Json.Save(paths.Data("browser-acceptance.json"), result);
            try { await Call("Browser.close", new { }); } catch (WebSocketException) { } catch (IOException) { } return result;
        }
        finally
        {
            if (!browser.HasExited) { try { using var deadline = new CancellationTokenSource(5000); await browser.WaitForExitAsync(deadline.Token); } catch { browser.Kill(true); } }
        }
    }
}
