using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AliceDesk
{
    internal static class HomeReconnectChecks
    {
        static readonly BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Instance;
        static object Field(object target, string name) { return target.GetType().GetField(name, Members).GetValue(target); }
        static void SetField(object target, string name, object value) { target.GetType().GetField(name, Members).SetValue(target, value); }
        static object Call(object target, string name, params object[] args) {
            try { return target.GetType().GetMethod(name, Members).Invoke(target, args); }
            catch(TargetInvocationException ex) { throw ex.InnerException; }
        }
        static void Check(bool condition, string message) { if(!condition)throw new Exception(message); }
        static void Wait(Task task) {
            var start=DateTime.UtcNow;
            while(!task.IsCompleted && DateTime.UtcNow-start<TimeSpan.FromSeconds(12)) {
                Application.DoEvents(); Thread.Sleep(10);
            }
            if(!task.IsCompleted)throw new TimeoutException("Тест HA не завершился.");
            task.GetAwaiter().GetResult();
        }
        sealed class FakeServer : HttpMessageHandler {
            internal bool fail;
            internal string state;
            internal readonly List<string> replies=new List<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel) {
                if(request.Method==HttpMethod.Post) {
                    if(fail)return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    var body=await request.Content.ReadAsStringAsync();
                    var item=MediaCommands.Serializer.Deserialize<Dictionary<string,object>>(body);
                    replies.Add((string)item["value"]);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content=new StringContent(MediaCommands.Serializer.Serialize(new {
                        entity_id="input_text.stantsiya_desk_command",state=state
                    }))
                };
            }
        }
        static MediaHome FakeHome(Uri origin, FakeServer server) {
            var ctor=typeof(MediaHome).GetConstructor(Members,null,
                new[]{typeof(Uri),typeof(string),typeof(HttpMessageHandler)},null);
            return (MediaHome)ctor.Invoke(new object[]{origin,"fixture-token",server});
        }
        static string TokenPath(Profile profile,string key) {
            return (string)Call(profile,"SecretFile",key);
        }
        [STAThread] static int Main(string[] args) {
            try {
                Application.EnableVisualStyles();
                var store=new ProfileStore(args[0]);
                var profile=store.Active;
                string original="https://127.0.0.1:1/";
                profile.SetMany(new Dictionary<string,string> {
                    {"local.agent.haOrigin",original},{"local.agent.haNetwork","fixture-adapter"},
                    {"local.agent.haEnabled","1"}
                });
                profile.PutSecret("agent.haToken","previous-token");
                using(var page=new AgentPage(profile)) {
                    ((System.Windows.Forms.Timer)Field(page,"timer")).Stop();
                    var oldSession=Field(page,"session");
                    var oldHome=Field(page,"home");
                    var networks=(ComboBox)Field(page,"network");
                    Check(networks.Items.Count>0,"Нет сетевого интерфейса для теста.");
                    networks.SelectedIndex=0;
                    ((TextBox)Field(page,"server")).Text="https://127.0.0.1:2/";
                    ((TextBox)Field(page,"token")).Text="new-token";
                    Wait((Task)Call(page,"Connect"));
                    Check(ReferenceEquals(oldSession,Field(page,"session")) && ReferenceEquals(oldHome,Field(page,"home")),
                        "Ошибка проверки отключила прежнюю сессию.");
                    Check(profile.Get("local.agent.haOrigin")==original && profile.Get("local.agent.haEnabled")=="1" &&
                        profile.Secret("agent.haToken")=="previous-token","Ошибка проверки изменила сохранённое подключение.");
                    using(var previousCheck=new CancellationTokenSource()) {
                        SetField(page,"connecting",previousCheck);
                        ((TextBox)Field(page,"server")).Text="not an https address";
                        Wait((Task)Call(page,"Connect"));
                        Check(previousCheck.IsCancellationRequested && Field(page,"connecting")==null &&
                            ReferenceEquals(oldSession,Field(page,"session")),
                            "Новый некорректный запрос не отменил предыдущую проверку.");
                    }
                    var changed=new Uri("https://127.0.0.1:3/");
                    Call(page,"SaveVerifiedConnection",changed,"new-adapter","new-token","agent.haToken");
                    Check(profile.Get("local.agent.haV2TokenSlot")=="2" && profile.Secret("agent.haToken2")=="new-token",
                        "Новая запись токена не активирована.");
                    Check(profile.Secret("agent.haToken")=="previous-token","Прежний токен перезаписан до замены профиля.");
                    var restored=new ProfileStore(args[0]).Active;
                    Check(restored.Get("local.agent.haV2Origin")==changed.AbsoluteUri &&
                        restored.Get("local.agent.haV2TokenSlot")=="2" && restored.Secret("agent.haToken2")=="new-token",
                        "Сохранённое подключение не восстанавливается после перезапуска.");
                    Check(restored.Get("local.agent.haOrigin")=="" && restored.Get("local.agent.haEnabled")=="v2",
                        "Старая версия может отправить прежний токен новому серверу.");
                    string blocked=TokenPath(profile,"agent.haToken3");
                    Directory.CreateDirectory(blocked);
                    bool failed=false;
                    try { Call(page,"SaveVerifiedConnection",new Uri("https://127.0.0.1:4/"),"third","third-token","agent.haToken2"); }
                    catch(IOException) { failed=true; }
                    finally { Directory.Delete(blocked); }
                    Check(failed && profile.Get("local.agent.haV2Origin")==changed.AbsoluteUri &&
                        profile.Get("local.agent.haV2TokenSlot")=="2" && profile.Secret("agent.haToken2")=="new-token",
                        "Сбой записи изменил существующий сервер или токен.");
                    string originalPair=profile.Get("local.agent.pair");
                    // The old release clears this setting before it rotates the pair.
                    // Revocation must take effect even if it is interrupted between them.
                    profile.Set("local.agent.haEnabled","");
                    Check(!(bool)Call(page,"ActiveEnabled"),"Отзыв старой версией включил предыдущую V2-сессию.");
                    profile.SetMany(new Dictionary<string,string> {
                        {"local.agent.haEnabled","v2"},{"local.agent.pair",Guid.NewGuid().ToString("N")}
                    });
                    Check(!(bool)Call(page,"ActiveEnabled"),"После смены адресата восстановилась старая V2-сессия.");
                    profile.Set("local.agent.pair",originalPair);
                    // If a user explicitly reconnects with an older release, the next
                    // update must adopt that route and migrate it without mixing keys.
                    profile.SetMany(new Dictionary<string,string> {
                        {"local.agent.haEnabled","1"},{"local.agent.haOrigin",original},
                        {"local.agent.haNetwork","legacy-adapter"}
                    });
                    Check((string)Call(page,"ActiveOrigin")==original &&
                        (string)Call(page,"ActiveTokenKey")=="agent.haToken",
                        "Обновление проигнорировало повторно настроенную старую привязку.");
                    Call(page,"SaveVerifiedConnection",changed,"new-adapter","new-token","agent.haToken");
                    Check(profile.Get("local.agent.haEnabled")=="v2" && profile.Get("local.agent.haOrigin")=="" &&
                        profile.Get("local.agent.haV2Origin")==changed.AbsoluteUri,
                        "Повторная миграция оставила старую привязку активной.");
                }

                // Simulate an abrupt stop after the event was reserved and before its outcome was stored.
                const string eventId="test_event_123";
                long now=MediaHome.Now();
                profile.SetMany(new Dictionary<string,string> {
                    {"local.agent.haSeen",eventId+":"+now},
                    {"local.agent.pendingAcks",MediaCommands.Serializer.Serialize(new List<PendingHomeAck> {
                        new PendingHomeAck {origin="https://127.0.0.1:1/",id="older_event_456",result="denied"},
                        new PendingHomeAck {origin="https://127.0.0.1:3/",id=eventId,result="unknown"}
                    })}
                });
                store=new ProfileStore(args[0]); profile=store.Active;
                using(var page=new AgentPage(profile)) {
                    ((System.Windows.Forms.Timer)Field(page,"timer")).Stop();
                    var old=(MediaHome)Field(page,"home"); old.Dispose();
                    var fake=new FakeServer {fail=true};
                    var pair=((TextBox)Field(page,"pair")).Text;
                    fake.state=MediaCommands.Serializer.Serialize(new {pair=pair,id=eventId,ts=now,action="not_configured"});
                    SetField(page,"home",FakeHome(new Uri("https://127.0.0.1:3/"),fake));
                    Wait((Task)Call(page,"Tick"));
                    Check(profile.Get("local.agent.pendingAcks").Length>0 && fake.replies.Count==0,
                        "Ошибка отправки потеряла неподтверждённый ответ.");
                    fake.fail=false; SetField(page,"retryAt",DateTime.MinValue);
                    Wait((Task)Call(page,"Tick"));
                    Check(fake.replies.Count==1 && fake.replies[0]==eventId+"|unknown" &&
                        profile.Get("local.agent.pendingAcks").Contains("older_event_456") &&
                        !profile.Get("local.agent.pendingAcks").Contains(eventId) && profile.Get("local.agent.haSeen").Contains(eventId),
                        "После перезапуска ответ не восстановлен или команда выполнена повторно.");
                    foreach(string outcome in new[]{"ok","denied"}) {
                        string id="another_event_"+outcome;
                        profile.SetMany(new Dictionary<string,string> {
                            {"local.agent.haSeen",id+":"+now},
                            {"local.agent.pendingAcks",MediaCommands.Serializer.Serialize(new List<PendingHomeAck> {
                                new PendingHomeAck {origin="https://127.0.0.1:3/",id=id,result=outcome}
                            })}
                        });
                        fake.state=MediaCommands.Serializer.Serialize(new {pair=pair,id=id,ts=now,action="not_configured"});
                        SetField(page,"lastState",null);
                        Wait((Task)Call(page,"Tick"));
                        Check(fake.replies[fake.replies.Count-1]==id+"|"+outcome && profile.Get("local.agent.pendingAcks")=="",
                            "Записанный результат не восстановлен: "+outcome);
                    }
                    var backlog=new List<PendingHomeAck>();
                    for(int i=0;i<512;i++)backlog.Add(new PendingHomeAck {
                        origin="https://127.0.0.1:1/",id="old_event_"+i.ToString("D4"),result="unknown"
                    });
                    profile.SetMany(new Dictionary<string,string> {
                        {"local.agent.haSeen",""},
                        {"local.agent.pendingAcks",MediaCommands.Serializer.Serialize(backlog)}
                    });
                    fake.state=MediaCommands.Serializer.Serialize(new {pair=pair,id="next_event_789",ts=MediaHome.Now(),action="not_configured"});
                    SetField(page,"lastState",null); SetField(page,"retryAt",DateTime.MinValue);
                    int previousReplies=fake.replies.Count;
                    Wait((Task)Call(page,"Tick"));
                    Check(profile.Get("local.agent.haSeen")=="" && fake.replies.Count==previousReplies &&
                        MediaCommands.Serializer.Deserialize<List<PendingHomeAck>>(profile.Get("local.agent.pendingAcks")).Count==512 &&
                        ((Label)Field(page,"netStatus")).Text.Contains("Очередь ответов HA заполнена"),
                        "Переполненная очередь изменилась или новая команда была отмечена обработанной.");
                    Call(page,"Revoke");
                    Check(profile.Get("local.agent.haEnabled")=="" && profile.Secret("agent.haToken")=="" &&
                        profile.Get("local.agent.haV2Enabled")=="" && profile.Secret("agent.haToken2")=="" &&
                        profile.Secret("agent.haToken3")=="" && profile.Get("local.agent.pendingAcks")=="",
                        "Отзыв не удалил обе записи токена и журнал ответов.");
                }
                Console.WriteLine("OK: неудачное переподключение, атомарная замена токена, восстановление ACK и отзыв доступа.");
                return 0;
            } catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
        }
    }
}
