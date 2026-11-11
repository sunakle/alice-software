using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AliceDesk {
    // HA is the sender and source of truth. Only the Windows client initiates TLS connections.
    internal sealed class MediaHome : IDisposable {
        const string CommandHelper = "input_text.stantsiya_desk_command";
        const string ResultHelper = "input_text.stantsiya_desk_reply";
        readonly HttpClient client;
        internal MediaHome(Uri root, string token, string source) : this(root, token, new LocalRoute(root, source)) { }
        MediaHome(Uri root, string token, HttpMessageHandler handler) {
            client = new HttpClient(handler) { BaseAddress = root, Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 262144 };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",token);
        }
        internal static Uri Address(string address) {
            Uri u;
            if (!Uri.TryCreate(address,UriKind.Absolute,out u) || u.Scheme != "https" || u.UserInfo != "" ||
                u.Query != "" || u.Fragment != "" || u.AbsolutePath != "/" || u.Host == "")
                throw new InvalidOperationException("Нужен HTTPS-адрес домашнего сервера HA с портом, без пути, логина или параметров.");
            return u;
        }
        static bool Private(IPAddress address) {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv4MappedToIPv6) address=address.MapToIPv4();
            var b=address.GetAddressBytes();
            if (address.AddressFamily==AddressFamily.InterNetworkV6) return (b[0]&254)==252 || address.IsIPv6LinkLocal;
            return b[0]==10 || (b[0]==172 && b[1]>=16 && b[1]<=31) || (b[0]==192 && b[1]==168) || (b[0]==169 && b[1]==254);
        }
        internal sealed class Adapter {
            internal string Id, Label;
            internal IPAddress[] Addresses;
            public override string ToString() { return Label; }
        }
        internal static Adapter[] Adapters() {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up)
                .Select(n=>new Adapter {Id=n.Id, Label=n.Name, Addresses=n.GetIPProperties().UnicastAddresses
                    .Select(a=>a.Address).Where(Private).ToArray()})
                .Where(n=>n.Addresses.Length>0)
                .OrderBy(n=>n.Addresses.All(IPAddress.IsLoopback)).ThenBy(n=>n.Label).ToArray();
        }
        static IPAddress RouteSource(IPAddress address, int port) {
            try {
                using(var socket=new Socket(address.AddressFamily,SocketType.Dgram,ProtocolType.Udp)) {
                    // A UDP connect selects the Windows route. No datagrams are sent.
                    socket.Connect(new IPEndPoint(address,port));
                    return ((IPEndPoint)socket.LocalEndPoint).Address;
                }
            } catch(SocketException) { return null; }
        }
        internal static IPAddress ChooseAddress(IPAddress[] addresses, IPAddress[] permitted, Func<IPAddress,IPAddress> routeSource) {
            if(addresses.Length==0 || addresses.Any(a=>!Private(a)))
                throw new InvalidOperationException("Адрес HA должен разрешаться только в частные IP домашней сети.");
            foreach (IPAddress address in addresses) {
                var actual=routeSource(address);
                if(actual!=null && permitted.Any(p=>p.Equals(actual))) return address;
            }
            throw new InvalidOperationException("Маршрут до HA проходит вне выбранного интерфейса или интерфейс отключён.");
        }
        internal static void PinRequest(HttpRequestMessage req, Uri origin, IPAddress destination) {
            if(req.RequestUri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) || !Private(destination))
                throw new InvalidOperationException("Адрес получателя не совпадает с привязкой домашнего сервера.");
            // .NET Framework connects to RequestUri and uses the explicit Host for TLS/SNI.
            string originalHost=origin.GetComponents(UriComponents.HostAndPort,UriFormat.UriEscaped);
            req.RequestUri=new UriBuilder(req.RequestUri) { Host=destination.ToString() }.Uri;
            req.Headers.Host=originalHost;
        }
        sealed class LocalRoute : DelegatingHandler {
            readonly Uri origin;
            readonly string source;
            internal LocalRoute(Uri origin, string source) : base(new HttpClientHandler {
                AllowAutoRedirect=false, UseProxy=false, UseCookies=false, UseDefaultCredentials=false,
                SslProtocols=System.Security.Authentication.SslProtocols.Tls12
            }) {
                if(String.IsNullOrWhiteSpace(source))throw new InvalidOperationException("Выберите домашний сетевой интерфейс и подключите HA.");
                this.origin=origin; this.source=source;
            }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) {
                var adapter=Adapters().FirstOrDefault(n=>n.Id==source);
                if(adapter==null)throw new InvalidOperationException("Выбранный домашний интерфейс отключён.");
                IPAddress literal;
                IPAddress[] addresses;
                if(IPAddress.TryParse(origin.DnsSafeHost.Trim('[',']'),out literal)) addresses=new[]{literal};
                else {
                    var resolve=Dns.GetHostAddressesAsync(origin.IdnHost);
                    if(await Task.WhenAny(resolve,Task.Delay(5000,ct)).ConfigureAwait(false)!=resolve) {
                        ct.ThrowIfCancellationRequested(); throw new TimeoutException("DNS домашнего сервера не ответил.");
                    }
                    addresses=await resolve.ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                var ip=ChooseAddress(addresses,adapter.Addresses,a=>RouteSource(a,origin.Port));
                PinRequest(req,origin,ip);
                return await base.SendAsync(req,ct).ConfigureAwait(false);
            }
        }
        static string Field(Dictionary<string,object> d,string key) {
            object o; return d.TryGetValue(key,out o) && o!=null ? Convert.ToString(o,CultureInfo.InvariantCulture) : "";
        }
        static void Success(HttpResponseMessage response) {
            if (response.IsSuccessStatusCode) return;
            int status=(int)response.StatusCode;
            if (status==401 || status==403) throw new InvalidOperationException("Home Assistant отклонил токен или права. Перепривяжите агент.");
            if (status==404) throw new InvalidOperationException("Home Assistant не нашёл помощник. Добавьте оба input_text из инструкции.");
            throw new InvalidOperationException("Home Assistant вернул HTTP " + status + ".");
        }
        async Task<string> Read(string helper,CancellationToken ct) {
            using (var response=await client.GetAsync("api/states/"+helper,ct)) {
                Success(response);
                var obj=MediaCommands.Serializer.Deserialize<Dictionary<string,object>>(await response.Content.ReadAsStringAsync());
                if (obj==null || Field(obj,"entity_id")!=helper || !obj.ContainsKey("state"))
                    throw new InvalidOperationException("Сервер вернул неожиданный объект помощника.");
                return Field(obj,"state");
            }
        }
        internal async Task Verify(CancellationToken ct) {
            await Read(CommandHelper,ct); await Read(ResultHelper,ct);
        }
        internal Task<string> ReadCommand(CancellationToken ct) { return Read(CommandHelper,ct); }
        internal async Task Ack(string eventId,string outcome,CancellationToken ct) {
            string value = eventId + "|" + outcome;
            var json=MediaCommands.Serializer.Serialize(new {entity_id=ResultHelper,value=value});
            using (var body=new StringContent(json,Encoding.UTF8,"application/json"))
            using (var response=await client.PostAsync("api/services/input_text/set_value",body,ct)) { Success(response); }
        }
        internal static bool Decode(string state, string pair, long now,out string id,out string action) {
            id=action=null;
            if (String.IsNullOrEmpty(state) || state.Length>255 || !state.StartsWith("{",StringComparison.Ordinal)) return false;
            try {
                var d = MediaCommands.Serializer.Deserialize<Dictionary<string,object>>(state);
                if (d==null || Field(d,"pair")!=pair || String.IsNullOrEmpty(pair)) return false;
                string ev=Field(d,"id"), cmd=Field(d,"action"); long created;
                if (!Regex.IsMatch(ev,@"\A[A-Za-z0-9_-]{8,64}\z") || !Regex.IsMatch(cmd,@"\A[a-z][a-z0-9_-]{0,47}\z") ||
                    !long.TryParse(Field(d,"ts"),NumberStyles.None,CultureInfo.InvariantCulture,out created) ||
                    created < now-30 || created > now+5) return false;
                id=ev; action=cmd; return true;
            } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; }
        }
        internal static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
        internal static string Remember(string history, string id, long now, out bool duplicate) {
            var kept=new List<string>(); duplicate=false;
            foreach (var item in (history ?? "").Split(' ')) {
                var parts=item.Split(':'); long time;
                if (parts.Length!=2 || !long.TryParse(parts[1],out time) || time<now-50) continue;
                if (parts[0]==id) duplicate=true;
                kept.Add(item);
            }
            if (!duplicate) kept.Add(id+":"+now.ToString(CultureInfo.InvariantCulture));
            return String.Join(" ",kept);
        }
        public void Dispose() { client.Dispose(); }
        sealed class TestServer : HttpMessageHandler {
            internal int Reads;
            internal string Reply;
            internal HttpStatusCode Status=HttpStatusCode.OK;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct) {
                if (req.RequestUri.Host!="ha.test" || req.Headers.Authorization==null ||
                    req.Headers.Authorization.Scheme!="Bearer" || req.Headers.Authorization.Parameter!="test-only")
                    throw new Exception("Запрос фикстуры ушёл без привязки токена к адресу HA.");
                if (req.Method==HttpMethod.Get) {
                    string helper=req.RequestUri.Segments.Last();
                    if (req.RequestUri.AbsolutePath!="/api/states/"+helper || (helper!=CommandHelper && helper!=ResultHelper))
                        throw new Exception("Некорректный адрес помощника HA.");
                    Reads++;
                    return new HttpResponseMessage(Status) { Content=new StringContent(MediaCommands.Serializer.Serialize(new {entity_id=helper,state="idle"})) };
                }
                if (req.Method!=HttpMethod.Post || req.RequestUri.AbsolutePath!="/api/services/input_text/set_value") throw new Exception("Некорректный адрес ответа HA.");
                var body=MediaCommands.Serializer.Deserialize<Dictionary<string,object>>(await req.Content.ReadAsStringAsync());
                if (Field(body,"entity_id")!=ResultHelper) throw new Exception("Ответ отправлен в другой помощник.");
                Reply=Field(body,"value"); return new HttpResponseMessage(Status){Content=new StringContent("[]")};
            }
        }
        static async Task HttpTest() {
            var server=new TestServer();
            using (var conn=new MediaHome(Address("https://ha.test:8123"),"test-only",server)) {
                await conn.Verify(CancellationToken.None);
                await conn.Ack("event_123","ok",CancellationToken.None);
                if (server.Reads!=2 || server.Reply!="event_123|ok") throw new Exception("Нарушен HTTP-контракт HA.");
                server.Status=HttpStatusCode.Unauthorized;
                try { await conn.ReadCommand(CancellationToken.None); }
                catch (InvalidOperationException ex) { if (ex.Message.Contains("токен")) return; throw; }
                throw new Exception("HTTP 401 ошибочно принят как работающий токен HA.");
            }
        }
        internal static void Test() {
            const string message="{\"pair\":\"pc1\",\"id\":\"test_12345\",\"action\":\"chrome\",\"ts\":1000}";
            string id,action;
            if (!Decode(message,"pc1",1000,out id,out action) || action!="chrome" || Decode(message,"other",1000,out id,out action) ||
                Decode(message,"pc1",1100,out id,out action) || Decode(message,"pc1",500,out id,out action)) throw new Exception("Проверка срока/адресата HA не прошла.");
            bool duplicate; string remembered=Remember("","test_12345",1000,out duplicate);
            if (duplicate) throw new Exception("Первое событие HA помечено повтором.");
            Remember(remembered,"test_12345",1001,out duplicate);
            if (!duplicate || Private(IPAddress.Parse("8.8.8.8")) || !Private(IPAddress.Parse("192.168.2.5"))) throw new Exception("Проверка повторного события/сети HA не прошла.");
            var destination=IPAddress.Parse("192.168.1.10"); var nic=IPAddress.Parse("192.168.1.30");
            if(!ChooseAddress(new[]{destination},new[]{nic},x=>nic).Equals(destination)) throw new Exception("Выбор маршрута HA.");
            bool blocked=false;
            try { ChooseAddress(new[]{destination,IPAddress.Parse("8.8.8.8")},new[]{nic},x=>nic); }
            catch(InvalidOperationException) {blocked=true;}
            if(!blocked)throw new Exception("Разрешён смешанный публичный и частный DNS-ответ.");
            blocked=false;
            try { ChooseAddress(new[]{destination},new[]{nic},x=>IPAddress.Loopback); }
            catch(InvalidOperationException) {blocked=true;}
            if(!blocked)throw new Exception("Разрешён маршрут через другой интерфейс.");
            using(var req=new HttpRequestMessage(HttpMethod.Get,"https://ha.test:8123/api/states/test")) {
                PinRequest(req,Address("https://ha.test:8123"),destination);
                if(req.RequestUri.Host!=destination.ToString() || req.Headers.Host!="ha.test:8123" || req.RequestUri.AbsolutePath!="/api/states/test")
                    throw new Exception("Фактический IP или TLS-имя HA изменены.");
            }
            Task.Run((Func<Task>)HttpTest).GetAwaiter().GetResult();
        }
    }
}
