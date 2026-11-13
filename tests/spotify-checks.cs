using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AliceDesk {
    internal static class SpotifyChecks {
        const string Id="0123456789ABCDEFGHIJKL";
        const string Client="0123456789abcdef0123456789abcdef";
        static void Check(bool value,string message) {if(!value)throw new Exception(message);}
        static void Reject(Action action) {try {action();}catch(InvalidOperationException){return;}throw new Exception("Expected rejection");}
        sealed class Server : HttpMessageHandler {
            internal readonly Queue<Func<HttpRequestMessage,Task<HttpResponseMessage>>> Steps=new Queue<Func<HttpRequestMessage,Task<HttpResponseMessage>>>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
                ct.ThrowIfCancellationRequested();Check(Steps.Count>0,"Unexpected request: "+request.RequestUri);return Steps.Dequeue()(request);
            }
            internal void Add(HttpMethod method,string suffix,int status,string body,Action<HttpRequestMessage,string> inspect=null) {
                Steps.Enqueue(async request=>{
                    Check(request.Method==method && request.RequestUri.AbsoluteUri.EndsWith(suffix),"Wrong request: "+request.Method+" "+request.RequestUri);
                    string payload=request.Content==null?"":await request.Content.ReadAsStringAsync();
                    if(inspect!=null)inspect(request,payload);
                    return new HttpResponseMessage((HttpStatusCode)status) {Content=new StringContent(body)};
                });
            }
        }
        static void Seed(Profile p,bool expired) {
            p.Set("local.spotify.clientId",Client);p.Set("local.spotify.device","device-test");
            p.PutSecret(SpotifyClient.VaultKey,Disk.Json(new SpotifyTokens {client_id=Client,access_token="access-old",refresh_token="refresh-old",expires_at=DateTime.UtcNow.AddMinutes(expired?-1:10).Ticks}));
        }
        static async Task Run(string directory) {
            foreach(string kind in new[]{"track","album","artist","playlist"}) {
                string uri="spotify:"+kind+":"+Id;
                Check(SpotifyClient.Target("https://open.spotify.com/"+kind+"/"+Id+"?si=discard") == uri,"Share URL normalization");
                Check(SpotifyClient.Target("https://open.spotify.com/intl-ru/"+kind+"/"+Id)==uri,"Locale link");
                var body=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(SpotifyClient.PlayBody(uri));
                Check(body.ContainsKey(kind=="track"?"uris":"context_uri"),"Wrong playback body");
            }
            foreach(string bad in new[]{"https://open.spotify.com.evil/track/"+Id,"https://evil@open.spotify.com/track/"+Id,"http://open.spotify.com/track/"+Id,"spotify:episode:"+Id,"spotify:track:short","spotify:track:"+Id+"\nmalicious","https://open.spotify.com:8443/track/"+Id})Reject(()=>SpotifyClient.Target(bad));
            Check(SpotifyClient.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")=="E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM","RFC 7636 PKCE vector");
            Check(SpotifyClient.CallbackCode("/callback?code=fixture&state=correct","correct")=="fixture","Valid OAuth callback");
            foreach(string bad in new[]{"/callback?code=x&state=wrong","/callback?code=x&state=correct&state=correct","https://example.org/callback?code=x&state=correct","/callback?state=correct","/favicon.ico"})Check(SpotifyClient.CallbackCode(bad,"correct")==null,"Invalid callback accepted");
            Reject(()=>SpotifyClient.CallbackCode("/callback?error=access_denied&state=correct","correct"));
            var store=new ProfileStore(directory);var p=store.Active;Seed(p,true);
            using(var server=new Server())using(var client=new SpotifyClient(p,server)) {
                server.Add(HttpMethod.Post,"accounts.spotify.com/api/token",200,"{\"access_token\":\"fresh\",\"token_type\":\"Bearer\",\"expires_in\":3600}",(r,b)=>{
                    Check(b.Contains("grant_type=refresh_token") && b.Contains("refresh_token=refresh-old") && !b.Contains("client_secret"),"Refresh must use PKCE client without secret");
                    Check(r.Headers.Authorization==null,"Bearer leaked to token endpoint");
                });
                server.Add(HttpMethod.Put,"me/player/play?device_id=device-test",204,"",(r,b)=>{
                    Check(r.Headers.Authorization.Parameter=="fresh" && b.Contains("\"uris\"") && b.Contains(Id),"Track not sent to chosen device");
                });
                await client.Play("spotify:track:"+Id,CancellationToken.None);
                Check(p.Secret(SpotifyClient.VaultKey).Contains("refresh-old"),"Refresh token was lost when response omitted it");
                server.Add(HttpMethod.Get,"me/player/devices",401,"");
                server.Add(HttpMethod.Post,"accounts.spotify.com/api/token",200,"{\"access_token\":\"rotated\",\"refresh_token\":\"refresh-next\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
                server.Add(HttpMethod.Get,"me/player/devices",200,"{\"devices\":[{\"id\":\"ok\",\"name\":\"PC\"},{\"id\":\"no\",\"is_restricted\":true},{\"name\":\"missing id\"}]}",(r,b)=>Check(r.Headers.Authorization.Parameter=="rotated","401 did not refresh"));
                Check((await client.Devices(CancellationToken.None)).Single().id=="ok","Restricted devices must not be selectable");
                Check(p.Secret(SpotifyClient.VaultKey).Contains("refresh-next"),"Rotation not saved");
                foreach(int code in new[]{403,404,429,500}) {
                    server.Add(HttpMethod.Put,"me/player/play?device_id=device-test",code,"sensitive provider detail");
                    try {await client.Play("spotify:playlist:"+Id,CancellationToken.None);throw new Exception("Failure claimed success");}
                    catch(InvalidOperationException ex) {Check(!ex.Message.Contains("sensitive"),"Raw error leaked");}
                }
                server.Steps.Enqueue(r=>{throw new HttpRequestException("connection lost after send");});
                bool unknown=false;try {await client.Play("spotify:artist:"+Id,CancellationToken.None);}catch(SpotifyOutcomeUnknownException){unknown=true;}
                Check(unknown && server.Steps.Count==0,"Uncertain play must not be retried");
                server.Add(HttpMethod.Get,"me/player/devices",401,"");
                server.Add(HttpMethod.Post,"accounts.spotify.com/api/token",200,"{\"access_token\":\"retry\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
                server.Add(HttpMethod.Get,"me/player/devices",401,"");
                bool denied=false;try {await client.Devices(CancellationToken.None);}catch(InvalidOperationException){denied=true;}
                Check(denied && server.Steps.Count==0,"401 retry must be bounded");
                p.Set("local.spotify.device",null);
                try {await client.Play("spotify:track:"+Id,CancellationToken.None);throw new Exception("Missing device accepted");}catch(InvalidOperationException){}
                using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try {await client.Devices(cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}}
            }
            Seed(p,true);
            using(var server=new Server())using(var client=new SpotifyClient(p,server)) {
                server.Add(HttpMethod.Post,"accounts.spotify.com/api/token",200,"{\"access_token\":\"stale\",\"token_type\":\"Bearer\",\"expires_in\":3600}",(r,b)=>p.PutSecret(SpotifyClient.VaultKey,""));
                try {await client.Devices(CancellationToken.None);throw new Exception("Logout was overwritten by refresh");}catch(InvalidOperationException){}
                Check(p.Secret(SpotifyClient.VaultKey)=="","Refresh revived logged-out credentials");
            }
            var rule=MediaCommands.Validate(new DeskAction {Id="evening",Kind="spotify",Phrases="включи вечер; вечерний плейлист",Target="https://open.spotify.com/playlist/"+Id+"?si=discard"});
            p.Set("agent.actions",Disk.Json(new[]{rule}));p.Set("local.agent.pair",new string('a',32));
            p.Set("local.agent.approved.evening",MediaCommands.Fingerprint(rule));
            Check(MediaCommands.Resolve(new[]{rule}," ВКЛЮЧИ  ВЕЧЕР! ")==rule,"Voice phrase matching");
            string yaml=VoiceExport.Package(new[]{rule},p);
            Check(yaml.Contains("mode: queued") && yaml.Contains("trigger: conversation") && yaml.Contains("вечерний плейлист") && !yaml.Contains(Id) && !yaml.Contains("refresh"),"HA package routing/secrets");
            File.WriteAllText(Path.Combine(directory,"fixture.yaml"),yaml,Disk.Utf8);
            rule.Target="spotify:track:"+Id;Reject(()=>MediaCommands.VerifyApproval(rule,p.Get("local.agent.approved.evening")));
            rule.Phrases="включи {anything}";p.Set("local.agent.approved.evening",MediaCommands.Fingerprint(rule));Reject(()=>VoiceExport.Package(new[]{rule},p));
            Seed(p,false);var exported=Path.Combine(directory,"profile-export.json");store.Export(p,exported);
            string portable=File.ReadAllText(exported);
            Check(!portable.Contains("access-old") && !portable.Contains("refresh-old") && !portable.Contains("local.spotify") && !portable.Contains("approved"),"Credentials or approvals exported");
            var copy=store.Import(exported);Check(MediaCommands.Load(copy).Single().Kind=="spotify" && copy.Secret(SpotifyClient.VaultKey)=="","Spotify mapping not portable");
            Console.WriteLine("PASS: Spotify targets, PKCE/state, refresh/rotation, 401 retry, device selection, 403/404/429/500, cancellation, uncertain delivery, logout race, phrase mapping, HA export and profile isolation.");
        }
        static int Main(string[] args) {try {Run(args[0]).GetAwaiter().GetResult();return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    }
}
