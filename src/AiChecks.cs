using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AliceDesk
{
    internal static class AiChecks
    {
        private sealed class Stub : HttpMessageHandler
        {
            internal int Calls;
            internal Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Reply;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
            {
                Calls++;
                return Reply(message, token);
            }
        }
        private static readonly CancellationToken NoCancel = CancellationToken.None;
        private static readonly AiEndpoint DeepSeek = new AiEndpoint("deepseek","https://api.deepseek.com/");
        private const string FakeKey = "test-only-local-fixture";

        private static void Assert(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Проверка ИИ: " + name);
        }
        private static HttpResponseMessage Response(int status, string body)
        {
            return new HttpResponseMessage((HttpStatusCode)status) {Content = new StringContent(body, Encoding.UTF8, "application/json")};
        }
        private static async Task Expected(Func<Task> action, string hint)
        {
            try { await action().ConfigureAwait(false); }
            catch (AiFailure ex) {
                Assert(ex.Message.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0, "тип ошибки: " + hint);
                Assert(!ex.Message.Contains(FakeKey), "ключ в исключении");
                return;
            }
            throw new InvalidOperationException("Ожидалась ошибка ИИ: " + hint);
        }
        private static void Invalid(Action action)
        {
            try { action(); }
            catch (AiFailure) { return; }
            throw new InvalidOperationException("Ошибочное разрешение URL, ключа или модели.");
        }
        private static async Task Suite()
        {
            foreach (string url in new[] {"https://other.example","http://api.deepseek.com",
                "https://api.deepseek.com:987","https://api.deepseek.com/v1","https://api.deepseek.com?key=example",
                "https://user@api.deepseek.com","https://api.deepseek.com#secret"})
                Invalid(delegate { new AiEndpoint("deepseek",url); });
            Invalid(delegate { new AiEndpoint("compatible","http://127.0.0.1:99/v1"); });
            Invalid(delegate { new AiEndpoint("ollama","http://8.8.8.8/v1"); });
            Invalid(delegate { new AiEndpoint("local","http://192.168.1.10:11434/v1").CheckKey(FakeKey); });
            Invalid(delegate { new AiEndpoint("compatible","https://provider.example/chat/completions"); });
            Invalid(delegate { DeepSeek.CheckKey("bad\r\nheader"); });
            Invalid(delegate { DeepSeek.CheckKey(""); });
            var local = new AiEndpoint("local","http://127.0.0.1:1234/v1/");
            Assert(local.Route("models").AbsolutePath == "/v1/models","сборка URL");
            Assert(DeepSeek.Route("chat/completions").AbsoluteUri == "https://api.deepseek.com/chat/completions","URL DeepSeek");
            var a = new AiEndpoint("compatible","https://x.example/v1/");
            Assert(a.SecretId == new AiEndpoint("compatible","https://X.example:443/v1").SecretId,"канонический адрес");
            Assert(a.SecretId != new AiEndpoint("compatible","https://y.example/v1").SecretId &&
                a.SecretId != new AiEndpoint("compatible","https://x.example/other").SecretId &&
                a.SecretId != new AiEndpoint("local","https://x.example/v1").SecretId, "изоляция ключа");
            var stub = new Stub();
            using (var api = new AiClient(stub,TimeSpan.FromSeconds(5)))
            {
                stub.Reply = delegate(HttpRequestMessage req,CancellationToken token) {
                    Assert(req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath == "/models", "контракт каталога");
                    Assert(req.Headers.Authorization.Scheme == "Bearer" && req.Headers.Authorization.Parameter == FakeKey, "авторизация");
                    Assert(!req.RequestUri.AbsoluteUri.Contains(FakeKey),"ключ в URL");
                    return Task.FromResult(Response(200,"{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":\"a\"}]}"));
                };
                string[] catalog = await api.Models(DeepSeek,FakeKey,NoCancel).ConfigureAwait(false);
                Assert(catalog.Length == 2 && catalog[1] == "b","каталог");
                var hist = new List<AiTurn> {new AiTurn("user","первый"),new AiTurn("assistant","один")};
                stub.Reply = async delegate(HttpRequestMessage req,CancellationToken token) {
                    Assert(req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/chat/completions", "контракт чата");
                    string payload = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var document = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(payload);
                    Assert((string)document["model"]=="a" && ((IList)document["messages"]).Count == 3,"история запроса");
                    Assert(!payload.Contains(FakeKey),"ключ в теле запроса");
                    return Response(200,"{\"model\":\"answer-id\",\"choices\":[{\"message\":{\"content\":\"тестовый ответ\"},\"finish_reason\":\"stop\"}]}");
                };
                var answer = await api.Generate(DeepSeek,FakeKey,"a",hist,"второй",NoCancel).ConfigureAwait(false);
                Assert(answer.Text == "тестовый ответ" && answer.Model == "answer-id" && hist.Count == 2,"ответ и неизменность истории");
                int[] codes = {301, 307, 401, 403, 402, 429, 404, 500};
                string[] hints = {"перенаправил","перенаправил","ключ","ключ","средств","лимит","модели","провайдер"};
                for (int i=0; i<codes.Length; i++)
                {
                    int code = codes[i], before = stub.Calls;
                    stub.Reply = delegate { var message = Response(code, "{\"error\":\"" + FakeKey + "\"}");
                        message.Headers.Location = new Uri("https://another.example/"); return Task.FromResult(message); };
                    await Expected(async delegate { await api.Generate(DeepSeek,FakeKey,"a",hist,"q",NoCancel).ConfigureAwait(false); }, hints[i]).ConfigureAwait(false);
                    Assert(stub.Calls == before + 1, "запрещён повтор");
                }
                stub.Reply = delegate { return Task.FromResult(Response(404,"")); };
                await Expected(async delegate { await api.Models(DeepSeek,FakeKey,NoCancel).ConfigureAwait(false); },"каталог").ConfigureAwait(false);
                stub.Reply = delegate { return Task.FromResult(Response(200,"<html>ошибка</html>")); };
                await Expected(async delegate { await api.Models(DeepSeek,FakeKey,NoCancel).ConfigureAwait(false); },"JSON").ConfigureAwait(false);
                stub.Reply = delegate { return Task.FromResult(Response(200,"{\"choices\":[]}")); };
                await Expected(async delegate { await api.Generate(DeepSeek,FakeKey,"a",hist,"q",NoCancel).ConfigureAwait(false); },"вариант").ConfigureAwait(false);
                stub.Reply = delegate { return Task.FromResult(Response(200,"{\"choices\":[{\"message\":{\"content\":null}}]}")); };
                await Expected(async delegate { await api.Generate(DeepSeek,FakeKey,"a",hist,"q",NoCancel).ConfigureAwait(false); },"текст").ConfigureAwait(false);
                stub.Reply = delegate { throw new HttpRequestException("fixture",new AuthenticationException("fixture")); };
                await Expected(async delegate { await api.Models(DeepSeek,FakeKey,NoCancel).ConfigureAwait(false); },"TLS").ConfigureAwait(false);
                stub.Reply = delegate { throw new HttpRequestException("fixture"); };
                await Expected(async delegate { await api.Models(DeepSeek,FakeKey,NoCancel).ConfigureAwait(false); },"сеть").ConfigureAwait(false);
                stub.Reply = async delegate(HttpRequestMessage req,CancellationToken token) {
                    await Task.Delay(Timeout.Infinite,token).ConfigureAwait(false);
                    return Response(200,"{}");
                };
                using (var stop = new CancellationTokenSource())
                {
                    var waiting = api.Models(DeepSeek,FakeKey,stop.Token);
                    stop.Cancel();
                    bool cancelled = false;
                    try { await waiting.ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Assert(cancelled,"отмена");
                }
            }
            var slow = new Stub();
            slow.Reply = async delegate(HttpRequestMessage req,CancellationToken token) {
                await Task.Delay(Timeout.Infinite,token).ConfigureAwait(false); return Response(200,"{}");
            };
            using (var api = new AiClient(slow,TimeSpan.FromMilliseconds(80)))
                await Expected(async delegate { await api.Models(DeepSeek,FakeKey,NoCancel).ConfigureAwait(false); },"время ожидания").ConfigureAwait(false);
            var settings = new JavaScriptSerializer().DeserializeObject(AiPage.MakeFragment(
                "https://api.deepseek.com/v1","future-id","спроси нейросеть",false)) as Dictionary<string,object>;
            Assert(settings != null && (string)settings["llm_model"] == "future-id" &&
                (string)((Dictionary<string,object>)settings["routing"])["default_route"] == "yandex","конфигурация маршрута");
        }
        internal static void Run() { Suite().GetAwaiter().GetResult(); }
    }
}
