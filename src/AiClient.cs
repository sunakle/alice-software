using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AliceDesk
{
    internal sealed class AiFailure : Exception
    {
        internal AiFailure(string message) : base(message) { }
    }

    internal sealed class AiEndpoint
    {
        internal readonly Uri Base;
        internal readonly string Provider;

        internal AiEndpoint(string provider, string url)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http") || uri.Host.Length == 0 ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                url.IndexOf('\\') >= 0 || url.Any(Char.IsControl))
                throw new AiFailure("Введите базовый HTTP(S)-адрес без логина, ключа, параметров или фрагмента.");
            if (provider == "deepseek" && (uri.Scheme != "https" || uri.Host != "api.deepseek.com" ||
                !uri.IsDefaultPort || uri.AbsolutePath.TrimEnd('/').Length != 0))
                throw new AiFailure("Для профиля DeepSeek используется https://api.deepseek.com. Для другого сервера выберите совместимый профиль.");
            bool local = provider == "ollama" || provider == "lmstudio" || provider == "local";
            if (uri.Scheme == "http" && (!local || !PrivateHost(uri)))
                throw new AiFailure("Облачный API требует HTTPS. Для HTTP выберите локальный сервер и укажите localhost или частный IP.");
            string last = uri.AbsolutePath.TrimEnd('/');
            if (last.EndsWith("/models", StringComparison.OrdinalIgnoreCase) ||
                last.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
                last.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                throw new AiFailure("Введите только базу API. Путь запроса программа добавит сама.");
            Base = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
            Provider = provider;
        }

        private static bool PrivateHost(Uri uri)
        {
            if (uri.IsLoopback) return true;
            IPAddress ip;
            if (!IPAddress.TryParse(uri.DnsSafeHost.Trim('[', ']'), out ip)) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 16) return ip.IsIPv6LinkLocal || (b[0] & 254) == 252;
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 168) || b[0] == 127;
        }

        internal string SecretId
        {
            get
            {
                using (SHA256 digest = SHA256.Create())
                    return "ai-" + BitConverter.ToString(digest.ComputeHash(
                        Encoding.UTF8.GetBytes(Provider + "\n" + Base.AbsoluteUri))).Replace("-", "");
            }
        }

        internal Uri Route(string part) { return new Uri(Base, part); }

        internal void CheckKey(string value)
        {
            if (Base.Scheme != "https" && value.Length > 0)
                throw new AiFailure("Ключи отправляются только по HTTPS. Для локального HTTP используйте сервер без ключа.");
            if (value.Length > 4096 || value.Any(c => c < 33 || c > 126))
                throw new AiFailure("Проверьте ключ. В нём есть пробелы, переносы или недопустимые символы.");
            if (Provider == "deepseek" && value.Length == 0)
                throw new AiFailure("Введите ключ DeepSeek и нажмите «Сохранить ключ».");
        }
    }

    internal sealed class AiTurn
    {
        public string role { get; set; }
        public string content { get; set; }
        internal AiTurn(string role, string text) { this.role = role; content = text; }
    }

    internal sealed class AiAnswer
    {
        internal string Text;
        internal string Model;
        internal string Finish;
    }

    internal sealed class AiClient : IDisposable
    {
        private const int Limit = 2 * 1024 * 1024;
        private readonly HttpClient http;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Limit };
        private readonly TimeSpan timeout;

        internal AiClient() : this(new HttpClientHandler {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
            SslProtocols = SslProtocols.Tls12
        }, TimeSpan.FromSeconds(90)) { }

        internal AiClient(HttpMessageHandler handler, TimeSpan timeout)
        {
            http = new HttpClient(handler);
            http.Timeout = Timeout.InfiniteTimeSpan;
            this.timeout = timeout;
        }

        internal static string ModelId(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Trim().Length > 240 ||
                value.Trim().Any(Char.IsControl))
                throw new AiFailure("Выберите модель или введите её точный ID из настроек сервера.");
            return value.Trim();
        }

        private static IDictionary<string, object> Object(object value)
        {
            var obj = value as IDictionary<string, object>;
            if (obj == null) throw new AiFailure("Сервер вернул неожиданный формат JSON.");
            return obj;
        }

        private static object Field(IDictionary<string, object> source, string name)
        {
            object value;
            return source.TryGetValue(name, out value) ? value : null;
        }

        private static IEnumerable Items(object value)
        {
            var items = value as object[];
            if (items == null) throw new AiFailure("В ответе нет ожидаемого массива.");
            return items;
        }

        internal async Task<string[]> Models(AiEndpoint endpoint, string key, CancellationToken cancel)
        {
            var reply = await Request(endpoint, key, "models", null, cancel).ConfigureAwait(false);
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in Items(Field(reply, "data")))
            {
                string id = Field(Object(item), "id") as string;
                if (!String.IsNullOrEmpty(id) && seen.Add(id)) result.Add(ModelId(id));
                if (result.Count > 4096) throw new AiFailure("Каталог слишком большой. Введите ID модели вручную.");
            }
            if (result.Count == 0) throw new AiFailure("Сервер вернул пустой каталог. Можно ввести модель вручную.");
            return result.ToArray();
        }

        internal async Task<AiAnswer> Generate(AiEndpoint endpoint, string key, string model,
            IList<AiTurn> history, string question, CancellationToken cancel)
        {
            model = ModelId(model);
            if (String.IsNullOrWhiteSpace(question)) throw new AiFailure("Введите текст запроса.");
            if (question.Length > 8000) throw new AiFailure("Одна реплика ограничена 8 000 символами.");
            var messages = new List<AiTurn>(history);
            messages.Add(new AiTurn("user", question));
            if (messages.Count > 49 || messages.Sum(x => x.content.Length) > 100000)
                throw new AiFailure("Диалог достиг предела. Начните новый диалог.");
            var payload = new Dictionary<string,object> {{"model",model},{"messages",messages},{"stream",false}};
            if (endpoint.Provider == "deepseek")
            {
                payload["max_tokens"] = 1024;
                payload["thinking"] = new {type = "disabled"};
            }
            string body = json.Serialize(payload);
            var raw = await Request(endpoint, key, "chat/completions", body, cancel).ConfigureAwait(false);
            object first = Items(Field(raw, "choices")).Cast<object>().FirstOrDefault();
            if (first == null) throw new AiFailure("Сервер не вернул ни одного варианта ответа.");
            var choice = Object(first);
            string answer = Field(Object(Field(choice, "message")), "content") as string;
            if (String.IsNullOrWhiteSpace(answer)) throw new AiFailure("Получен ответ без текстового содержимого.");
            return new AiAnswer {Text = answer, Model = Field(raw, "model") as string ?? model,
                Finish = Field(choice, "finish_reason") as string ?? ""};
        }

        private static AiFailure NetworkFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                var web = current as WebException;
                if (current is AuthenticationException || (web != null &&
                    (web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure)))
                    return new AiFailure("TLS-соединение не подтверждено. Проверьте сертификат, имя сервера, дату и время Windows.");
            }
            return new AiFailure("Нет ответа сервера. Проверьте адрес, сеть и доступность службы.");
        }

        private static AiFailure Status(int code, string body, bool catalog)
        {
            string hint;
            if (code >= 300 && code <= 399) hint = "Сервер перенаправил запрос. Переход заблокирован. Укажите конечную базу явно.";
            else if (code == 401 || code == 403) hint = "Ключ неверен либо у него нет доступа. Проверьте ключ и права.";
            else if (code == 402 || body.IndexOf("insufficient_balance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                body.IndexOf("insufficient_quota", StringComparison.OrdinalIgnoreCase) >= 0)
                hint = "Недостаточно средств или квоты. Проверьте баланс аккаунта провайдера.";
            else if (code == 429) hint = "Достигнут лимит запросов. Подождите перед ручным повтором.";
            else if (catalog && (code == 404 || code == 405)) hint = "Каталог моделей недоступен. Проверьте адрес или введите ID модели вручную.";
            else if (code == 404 || code == 400 || code == 422)
                hint = "Проверьте ID модели, доступ к ней и совместимость пути Chat Completions с сервером.";
            else if (code >= 500) hint = "Провайдер временно не смог обработать запрос.";
            else hint = "Сервер отклонил запрос. Проверьте адрес API и параметры провайдера.";
            return new AiFailure("HTTP " + code + ". " + hint);
        }

        private async Task<IDictionary<string, object>> Request(AiEndpoint endpoint, string key,
            string path, string body, CancellationToken cancel)
        {
            endpoint.CheckKey(key);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, endpoint.Route(path)))
            {
                limit.CancelAfter(timeout);
                if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                    using (limit.Token.Register(response.Dispose))
                    {
                        int code = (int)response.StatusCode;
                        if (code >= 300 && code <= 399) throw Status(code, "", body == null);
                        if (response.Content == null) throw new AiFailure("Сервер вернул пустой HTTP-ответ.");
                        if (response.Content.Headers.ContentLength > Limit) throw new AiFailure("Ответ превышает предел 2 МБ.");
                        string raw;
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var bytes = new MemoryStream())
                        {
                            var buffer = new byte[8192];
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, limit.Token).ConfigureAwait(false)) != 0)
                            {
                                if (bytes.Length + read > Limit) throw new AiFailure("Ответ превышает предел 2 МБ.");
                                bytes.Write(buffer, 0, read);
                            }
                            raw = Encoding.UTF8.GetString(bytes.ToArray()).TrimStart('\uFEFF');
                        }
                        limit.Token.ThrowIfCancellationRequested();
                        if (!response.IsSuccessStatusCode) throw Status(code, raw, body == null);
                        try { return Object(json.DeserializeObject(raw)); }
                        catch (ArgumentException) { throw new AiFailure("Получен некорректный JSON. Проверьте базовый адрес API."); }
                        catch (InvalidOperationException) { throw new AiFailure("Структура JSON не поддерживается."); }
                    }
                }
                catch (AiFailure) { throw; }
                catch (Exception ex)
                {
                    if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
                    if (limit.IsCancellationRequested) throw new AiFailure("Превышено время ожидания ответа. Сервер мог получить запрос.");
                    if (ex is HttpRequestException || ex is IOException || ex is WebException ||
                        ex is AuthenticationException || ex is OperationCanceledException) throw NetworkFailure(ex);
                    throw;
                }
            }
        }
        public void Dispose() { http.Dispose(); }
    }
}
