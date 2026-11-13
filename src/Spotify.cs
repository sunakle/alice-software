using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AliceDesk {
    internal sealed class SpotifyOutcomeUnknownException : Exception {
        internal SpotifyOutcomeUnknownException(Exception inner) : base("Ответ Spotify потерян. Команда могла выполниться — проверьте воспроизведение перед повтором.",inner) {}
    }
    internal sealed class SpotifyTokens {
        public string access_token {get;set;}
        public string refresh_token {get;set;}
        public string token_type {get;set;}
        public int expires_in {get;set;}
        public long expires_at {get;set;}
        public string client_id {get;set;}
    }
    internal sealed class SpotifyDevice {
        public string id {get;set;}
        public string name {get;set;}
        public bool is_active {get;set;}
        public bool is_restricted {get;set;}
        public override string ToString() {return name+(is_active?" · играет":"");}
    }
    internal sealed class SpotifyDevices {public List<SpotifyDevice> devices {get;set;}}

    internal sealed class SpotifyClient : IDisposable {
        internal const string Redirect="http://127.0.0.1:5589/callback";
        internal const string VaultKey="spotify.oauth";
        static readonly SemaphoreSlim RefreshLock=new SemaphoreSlim(1,1);
        readonly Profile profile;
        readonly HttpClient http;
        internal SpotifyClient(Profile profile) : this(profile,new HttpClientHandler {AllowAutoRedirect=false}) {}
        internal SpotifyClient(Profile profile,HttpMessageHandler handler) {
            this.profile=profile; http=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(15),MaxResponseContentBufferSize=1024*1024};
        }
        static T Read<T>(string json) {return new JavaScriptSerializer {MaxJsonLength=1024*1024}.Deserialize<T>(json);}
        internal static string ClientId(string text) {
            string value=(text??"").Trim();
            if(!Regex.IsMatch(value,@"\A[a-fA-F0-9]{32}\z"))throw new InvalidOperationException("Введите Client ID приложения Spotify: 32 шестнадцатеричных символа. Client Secret не нужен.");
            return value;
        }
        internal static string Target(string text) {
            string value=(text??"").Trim();
            if(Regex.IsMatch(value,@"\Aspotify:(track|album|artist|playlist):[a-zA-Z0-9]{22}\z"))return value;
            Uri uri;
            if(Uri.TryCreate(value,UriKind.Absolute,out uri) && uri.Scheme=="https" && uri.Host=="open.spotify.com" && uri.IsDefaultPort && uri.UserInfo=="") {
                var match=Regex.Match(uri.AbsolutePath,@"\A/(?:intl-[a-zA-Z-]+/)?(track|album|artist|playlist)/([a-zA-Z0-9]{22})/?\z");
                if(match.Success)return "spotify:"+match.Groups[1].Value+":"+match.Groups[2].Value;
            }
            throw new InvalidOperationException("Вставьте ссылку open.spotify.com или Spotify URI на песню, альбом, исполнителя или плейлист.");
        }
        internal static string PlayBody(string target) {
            string uri=Target(target);
            return uri.StartsWith("spotify:track:",StringComparison.Ordinal)?Disk.Json(new {uris=new[]{uri}}):Disk.Json(new {context_uri=uri});
        }
        internal static string Challenge(string verifier) {
            using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
        }
        static string RandomValue() {
            var bytes=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
        }
        // Reject duplicate parameters and foreign state before looking at code/error.
        internal static string CallbackCode(string target,string state) {
            if(target==null || !target.StartsWith("/callback?",StringComparison.Ordinal) || target.Length>8192)return null;
            var pairs=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var pair in target.Substring(10).Split('&')) {
                var parts=pair.Split(new[]{'='},2);
                string key=Uri.UnescapeDataString(parts[0]);
                if(pairs.ContainsKey(key))return null;
                pairs[key]=parts.Length==2?Uri.UnescapeDataString(parts[1].Replace('+',' ')):"";
            }
            string returned,code;
            if(!pairs.TryGetValue("state",out returned) || returned!=state)return null;
            if(pairs.ContainsKey("error"))throw new InvalidOperationException("Вход в Spotify отменён или не разрешён.");
            return pairs.TryGetValue("code",out code) && code.Length>0?code:null;
        }
        internal async Task Login(string clientId,CancellationToken ct) {
            clientId=ClientId(clientId);
            string verifier=RandomValue(),state=RandomValue();
            var listener=new TcpListener(IPAddress.Loopback,5589);
            listener.Server.ExclusiveAddressUse=true;
            try {listener.Start();} catch(SocketException) {throw new InvalidOperationException("Порт 5589 занят. Закройте другое окно входа в Spotify и повторите.");}
            try {
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                    timeout.CancelAfter(TimeSpan.FromMinutes(3));
                    using(timeout.Token.Register(listener.Stop)) {
                        MediaUi.Open("https://accounts.spotify.com/authorize?response_type=code&client_id="+Uri.EscapeDataString(clientId)+
                            "&redirect_uri="+Uri.EscapeDataString(Redirect)+"&scope=user-read-playback-state%20user-modify-playback-state"+
                            "&code_challenge_method=S256&code_challenge="+Challenge(verifier)+"&state="+state);
                        string code=null;
                        while(code==null) {
                            TcpClient incoming;
                            try {incoming=await listener.AcceptTcpClientAsync();}
                            catch(Exception) {timeout.Token.ThrowIfCancellationRequested();throw;}
                            using(incoming) using(timeout.Token.Register(incoming.Close)) {
                                var stream=incoming.GetStream();
                                // Read only the request line, with a byte limit; a local client cannot hold login forever.
                                var bytes=new List<byte>();var one=new byte[1];
                                using(var requestTimeout=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token)) {
                                    requestTimeout.CancelAfter(3000);
                                    using(requestTimeout.Token.Register(incoming.Close))
                                    try {
                                        while(bytes.Count<8192 && await stream.ReadAsync(one,0,1,requestTimeout.Token)>0) {
                                            bytes.Add(one[0]);if(one[0]==10)break;
                                        }
                                    } catch(Exception) {timeout.Token.ThrowIfCancellationRequested();if(requestTimeout.IsCancellationRequested)continue;throw;}
                                }
                                string line=Encoding.ASCII.GetString(bytes.ToArray());var parts=line.Trim().Split(' ');
                                if(parts.Length==3 && parts[0]=="GET")code=CallbackCode(parts[1],state);
                                string html=code==null?"Invalid login callback. Return to Spotify sign-in.":"Spotify sign-in received. Return to the Station Hub to finish.";
                                byte[] response=Encoding.UTF8.GetBytes("HTTP/1.1 "+(code==null?"400 Bad Request":"200 OK")+"\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: "+Encoding.UTF8.GetByteCount(html)+"\r\n\r\n"+html);
                                await stream.WriteAsync(response,0,response.Length,timeout.Token);
                            }
                        }
                        var tokens=await Exchange(new Dictionary<string,string>{{"grant_type","authorization_code"},{"client_id",clientId},{"code",code},{"redirect_uri",Redirect},{"code_verifier",verifier}},timeout.Token);
                        if(String.IsNullOrEmpty(tokens.refresh_token))throw new InvalidDataException("Spotify не вернул refresh token. Повторите вход.");
                        timeout.Token.ThrowIfCancellationRequested();
                        tokens.client_id=clientId;
                        profile.PutSecret(VaultKey,Disk.Json(tokens));
                        profile.Set("local.spotify.clientId",clientId);
                    }
                }
            } finally {listener.Stop();}
        }
        async Task<SpotifyTokens> Exchange(Dictionary<string,string> fields,CancellationToken ct) {
            using(var content=new FormUrlEncodedContent(fields))
            using(var response=await http.PostAsync("https://accounts.spotify.com/api/token",content,ct)) {
                if(!response.IsSuccessStatusCode)throw Failure(response,true);
                var tokens=Read<SpotifyTokens>(await response.Content.ReadAsStringAsync());
                if(tokens==null || String.IsNullOrEmpty(tokens.access_token) || tokens.expires_in<=0 || !String.Equals(tokens.token_type,"Bearer",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Spotify вернул неполный ответ авторизации.");
                tokens.expires_at=DateTime.UtcNow.AddSeconds(tokens.expires_in).Ticks;
                return tokens;
            }
        }
        async Task<string> Access(string rejected,CancellationToken ct) {
            await RefreshLock.WaitAsync(ct);
            try {
                string saved=profile.Secret(VaultKey);
                if(saved.Length==0)throw new InvalidOperationException("Сначала войдите в Spotify в разделе «Музыка».");
                var tokens=Read<SpotifyTokens>(saved);
                if(tokens==null || tokens.client_id!=profile.Get("local.spotify.clientId") || String.IsNullOrEmpty(tokens.refresh_token))throw new InvalidOperationException("Войдите в Spotify заново.");
                if(tokens.expires_at>DateTime.UtcNow.AddSeconds(60).Ticks && !String.IsNullOrEmpty(tokens.access_token) && tokens.access_token!=rejected)return tokens.access_token;
                var next=await Exchange(new Dictionary<string,string>{{"grant_type","refresh_token"},{"client_id",tokens.client_id},{"refresh_token",tokens.refresh_token}},ct);
                if(String.IsNullOrEmpty(next.refresh_token))next.refresh_token=tokens.refresh_token;
                next.client_id=tokens.client_id;
                ct.ThrowIfCancellationRequested();
                if(profile.Secret(VaultKey)!=saved)throw new InvalidOperationException("Подключение Spotify изменилось. Повторите команду.");
                profile.PutSecret(VaultKey,Disk.Json(next));return next.access_token;
            } finally {RefreshLock.Release();}
        }
        internal static Exception Failure(HttpResponseMessage response,bool token) {
            int code=(int)response.StatusCode;
            string message=token?"Не удалось обновить вход Spotify. Войдите заново.":"Spotify не выполнил команду (HTTP "+code+").";
            if(code==401)message="Spotify отклонил авторизацию. Войдите заново.";
            if(code==403)message="Нет доступа Spotify: проверьте Premium, разрешения приложения и список пользователей в Dashboard.";
            if(code==404)message="Устройство Spotify недоступно. Откройте Spotify, запустите трек и обновите список устройств.";
            if(code==429)message="Spotify ограничил частоту запросов. Повторите позже"+(response.Headers.RetryAfter==null?".":" (Retry-After: "+response.Headers.RetryAfter+").");
            return new InvalidOperationException(message);
        }
        async Task<string> Request(HttpMethod method,string path,string body,CancellationToken ct) {
            string access=await Access(null,ct);
            for(int attempt=0;attempt<2;attempt++) {
                using(var request=new HttpRequestMessage(method,"https://api.spotify.com/v1/"+path)) {
                    request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",access);
                    if(body!=null)request.Content=new StringContent(body,Encoding.UTF8,"application/json");
                    HttpResponseMessage received;
                    try {received=await http.SendAsync(request,ct);}
                    catch(HttpRequestException ex) {if(method!=HttpMethod.Get)throw new SpotifyOutcomeUnknownException(ex);throw;}
                    catch(OperationCanceledException ex) {if(method!=HttpMethod.Get)throw new SpotifyOutcomeUnknownException(ex);throw;}
                    using(var response=received) {
                        if(response.StatusCode==HttpStatusCode.Unauthorized && attempt==0){access=await Access(access,ct);continue;}
                        if(!response.IsSuccessStatusCode)throw Failure(response,false);
                        return await response.Content.ReadAsStringAsync();
                    }
                }
            }
            throw new InvalidOperationException("Войдите в Spotify заново.");
        }
        internal async Task<List<SpotifyDevice>> Devices(CancellationToken ct) {
            var result=Read<SpotifyDevices>(await Request(HttpMethod.Get,"me/player/devices",null,ct));
            return result==null || result.devices==null?new List<SpotifyDevice>():result.devices.Where(d=>d!=null && !d.is_restricted && !String.IsNullOrEmpty(d.id)).ToList();
        }
        internal async Task Play(string target,CancellationToken ct) {
            string body=PlayBody(target),device=profile.Get("local.spotify.device");
            if(device.Length==0)throw new InvalidOperationException("Выберите устройство Spotify в разделе «Музыка».");
            await Request(HttpMethod.Put,"me/player/play?device_id="+Uri.EscapeDataString(device),body,ct);
        }
        public void Dispose() {http.Dispose();}
    }
}
