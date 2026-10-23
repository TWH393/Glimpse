using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WpfApp1;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> call, string contains)
{
    try { await call(); throw new Exception("Expected rejection"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains(contains), contains); }
}
async Task Mock(int engine, string response, Func<JsonElement, bool> validate, int status = 200)
{
    var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
    int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
    using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
    var serve = Task.Run(async () => {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream);
        using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
        Check(validate(body.RootElement), $"engine {engine} request body");
        var bytes = Encoding.UTF8.GetBytes(response);
        context.Response.StatusCode = status; context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    });
    var settings = new Settings { Engine = engine, ApiUrl = $"http://127.0.0.1:{port}/chat", OnlineUrl = $"http://127.0.0.1:{port}", Model = "test-model" };
    if(status == 200) Check(await Translation.Translate("Hello", "en", "zh", settings, CancellationToken.None) == "你好", $"engine {engine} response parsing");
    else await Reject(() => Translation.Translate("Hello", "en", "zh", settings, CancellationToken.None), "HTTP 429");
    await serve.WaitAsync(TimeSpan.FromSeconds(5));
}
await Mock(0, "{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}", b => b.GetProperty("model").GetString() == "test-model" && b.GetProperty("messages")[1].GetProperty("content").GetString() == "Hello");
await Mock(1, "{\"translatedText\":\"你好\"}", b => b.GetProperty("q").GetString() == "Hello" && b.GetProperty("target").GetString() == "zh");
await Mock(0, "{}", _ => true, 429);
await Reject(() => Translation.Translate("Hello", "en", "zh", new Settings { Offline = true }, CancellationToken.None), "严格离线");
await Reject(() => Translation.Translate(new string('x', 16001), "en", "zh", new Settings(), CancellationToken.None), "16,000");
await Reject(() => Translation.Translate("Hello", "en", "zh", new Settings { Engine = -1 }, CancellationToken.None), "有效的翻译引擎");
await Reject(() => Translation.Translate("Hello", "en", "zh", new Settings { ApiUrl = "http://example.com", Model = "x" }, CancellationToken.None), "HTTPS");
var cancelled = new CancellationToken(true);
try { await Translation.Translate("Hi", "en", "zh", new Settings { ApiUrl = "http://127.0.0.1:1/", Model = "x" }, cancelled); throw new Exception("Cancellation ignored"); }
catch(OperationCanceledException) { Check(true, "cancelled request"); }
var secret = "dummy-test-key-never-a-real-credential";
var protect = typeof(Settings).GetMethod("Protect", BindingFlags.NonPublic | BindingFlags.Static)!;
var unprotect = typeof(Settings).GetMethod("Unprotect", BindingFlags.NonPublic | BindingFlags.Static)!;
var encrypted = (string)protect.Invoke(null, new object[] { secret })!;
Check(encrypted != secret && (string)unprotect.Invoke(null, new object[] { encrypted })! == secret, "DPAPI round trip");
var serialized = JsonSerializer.Serialize(new Settings { ApiKey = secret, OnlineKey = secret });
Check(!serialized.Contains(secret), "plaintext keys excluded from settings JSON");
Console.WriteLine($"{passed} checks passed. HTTP responses are test doubles; no real translation service was called.");
