using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SearchPort;

record Snapshot(string Url, string Title, string Text);

static class PageHandoff
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool Allowed(string address) => Uri.TryCreate(address, UriKind.Absolute, out var url)
        && (url.Scheme == "https" || url.Scheme == "http") && url.UserInfo.Length == 0;

    // Extraction is deliberately narrow. This is not a complete secret or PII filter.
    const string Script = """
        (() => {
          if (!document.body) return null;
          const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          let text = '', node, seen = 0;
          while ((node = walk.nextNode()) && text.length < 12000 && seen++ < 20000) {
            const e = node.parentElement;
            const value = node.nodeValue.trim();
            if (!value || !e || e.closest('script,style,noscript,template,form,input,textarea,select,[contenteditable],[hidden],[aria-hidden=true]')) continue;
            if (!e.checkVisibility({checkOpacity:true,checkVisibilityCSS:true})) continue;
            const remaining = 12000 - text.length;
            text += (text ? '\n' : '') + value.slice(0, remaining - (text ? 1 : 0));
          }
          return { url: location.href, title: document.title.slice(0,300), text };
        })()
        """;

    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(5);

    public static async Task<Snapshot> Capture(CoreWebView2 core, TimeSpan? timeout = null)
    {
        var before = core.Source;
        if (!Allowed(before)) throw new InvalidOperationException("Only HTTP and HTTPS pages can be shared.");
        string result;
        var scriptTask = core.ExecuteScriptAsync(Script);
        try
        {
            result = await scriptTask.WaitAsync(timeout ?? CaptureTimeout);
        }
        catch (TimeoutException ex)
        {
            // Observe any late fault on the orphaned script task so it does not surface as unobserved.
            _ = scriptTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException("Timed out reading page text. The page may be busy or showing a dialog.", ex);
        }
        if (result.Length > 120_000) throw new InvalidOperationException("The captured response exceeds the share limit.");
        var snapshot = JsonSerializer.Deserialize<Snapshot>(result, Json);
        if (snapshot is null || snapshot.Title is null || snapshot.Text is null ||
            before != core.Source || before != snapshot.Url)
            throw new InvalidOperationException("The page changed or is not ready. Try again.");
        var cut = before.IndexOfAny(['?', '#']);
        return new Snapshot(cut < 0 ? before : before[..cut],
            snapshot.Title[..Math.Min(300, snapshot.Title.Length)], snapshot.Text[..Math.Min(12_000, snapshot.Text.Length)]);
    }

    public static (string Name, Task Completion) Offer(Snapshot snapshot, CancellationToken cancellation)
    {
        var name = "SearchWindows-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        return (name, Deliver(pipe, snapshot, cancellation));
    }

    static async Task Deliver(NamedPipeServerStream pipe, Snapshot snapshot, CancellationToken cancellation)
    {
        await using (pipe)
        using (var ttl = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            ttl.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await pipe.WaitForConnectionAsync(ttl.Token).ConfigureAwait(false);
                await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(snapshot, Json), ttl.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ttl.IsCancellationRequested) { }
        }
    }
}
