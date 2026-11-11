using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AliceDesk
{
    internal sealed class DeviceInventory
    {
        internal const string KnownBuild = "0.369.1.50.4794654278.20261021.133";
        internal const string Script = @"field() { printf '%s=%s\n' ""$1"" ""$2""; }
dt=/proc/device-tree
model=$(tr '\000\n' '  ' < ""$dt/model"" 2>/dev/null)
compat=$(tr '\000\n' '  ' < ""$dt/compatible"" 2>/dev/null)
field model ""$model""
field compatible ""$compat""
sku=$(for f in ""$dt/sku"" ""$dt/model"" ""$dt/product-name"" /etc/product_model; do [ ! -f ""$f"" ] || tr '\000' '\n' < ""$f""; done | grep -Eo 'YNDX-[0-9]{5}' | sort -u | head -n 2 | tr '\n' ' ')
field sku ""$sku""
field build ""$(head -n 1 /system/vendor/quasar/version_full 2>/dev/null)""
field kernel ""$(uname -mr)""
field uid ""$(id -u)""
field elf ""$(od -An -tu1 -j4 -N2 /system/vendor/quasar/maind 2>/dev/null | xargs)""
field machine ""$(od -An -tu1 -j18 -N2 /system/vendor/quasar/maind 2>/dev/null | xargs)""
flags=$(od -An -tu1 -j37 -N1 /system/vendor/quasar/maind 2>/dev/null | xargs)
case ""$flags"" in ''|*[!0-9]*) ;; *)
  if [ $((flags & 6)) -eq 4 ]; then field float hard
  elif [ $((flags & 6)) -eq 2 ]; then field float soft
  elif command -v readelf >/dev/null 2>&1 && readelf -A /system/vendor/quasar/maind 2>/dev/null | grep -q 'VFP registers'; then field float hard; fi;;
esac
field libc ""$(getconf GNU_LIBC_VERSION 2>/dev/null || ls /lib/libc-*.so 2>/dev/null | head -n 1)""
field free ""$(df -Pk /data 2>/dev/null | awk 'END {print $4}')""
field mem ""$(awk '/^MemTotal:/{print $2}' /proc/meminfo)""
field load ""$(cut -d' ' -f1-3 /proc/loadavg 2>/dev/null)""
field cpus ""$(grep -c '^processor' /proc/cpuinfo 2>/dev/null)""
field net ""$(ls /sys/class/net | grep -E '^(eth|wlan|usb|lo)[0-9]*$' | tr '\n' ' ')""
field tools ""$(for tool in sh yse opkg yam yamd fastfetch socat aplay amixer base64 sha256sum stat nohup shairport-sync nqptp avahi-daemon setsid; do command -v ""$tool"" >/dev/null 2>&1 && printf '%s ' ""$tool""; done)""
field processes ""$(for app in maind yamd dropbear adbd shairport-sync nqptp avahi-daemon bluealsa librespot pulsar quasar-bridge; do pidof ""$app"" >/dev/null 2>&1 && printf '%s ' ""$app""; done)""
field alsa ""$(grep -c '^[ ]*[0-9]' /proc/asound/cards 2>/dev/null)""
field rings ""$(c=0; for region in 1 2 3; do for col in red green blue; do d=/sys/class/leds/plum:$col:ring$region; [ -r ""$d/brightness"" ] && [ -r ""$d/trigger"" ] && c=$((c+1)); done; done; echo ""$c"")""
field clock ""$(for d in /sys/class/leds/*clock* /sys/class/clockface /dev/clockface; do if [ -e ""$d"" ]; then echo present; break; fi; done)""
field ipc ""$(awk '$2 ~ /:2693$/ && $4==""0A"" {print ""listen""; exit}' /proc/net/tcp /proc/net/tcp6 2>/dev/null)""
field bridge ""$(test -f /data/opt/quasar-bridge/app/start.sh && echo present)""
if [ -r /system/vendor/quasar/launch.json ]; then
  if grep -q -- '--updatesd' /system/vendor/quasar/launch.json; then field ota present; else field ota absent; fi
fi
if [ -r /data/opt/etc/yse/sources ]; then field sources ""$(awk 'NF && $1 !~ /^#/ {n++} END {print n+0}' /data/opt/etc/yse/sources)""; fi
field ipclog ""$(for f in /var/log/quasar.log /data/quasar/logs/alice.log /data/logs/quasar.log; do if [ -f ""$f"" ] && [ ! -L ""$f"" ]; then tail -n 100 ""$f"" | awk 'BEGIN {n=0;e=0} {n++} tolower($0) ~ /error|fatal/ {e++} END {print n,e}'; break; fi; done)""
identity=
for f in /proc/device-tree/serial-number /etc/machine-id /sys/class/net/wlan0/address; do
  if [ -s ""$f"" ] || [ -r ""$f"" ]; then
    identity=$(tr -d '\000\r\n ' < ""$f"" 2>/dev/null)
    [ -n ""$identity"" ] && [ ""$identity"" != '00:00:00:00:00:00' ] && break
    identity=
  fi
done
[ -z ""$identity"" ] || field binding ""$(printf '%s' ""$identity"" | sha256sum 2>/dev/null | cut -d' ' -f1)""
true";
        internal readonly Dictionary<string,string> Values = new Dictionary<string,string>(StringComparer.Ordinal);
        internal string Value(string k) { string v; return Values.TryGetValue(k,out v) ? v : ""; }
        internal bool Plum { get {
            string compatible=Value("compatible");
            var boards=compatible.Split(' ').Where(s=>s.StartsWith("yandex,",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(boards.Length>0)return boards.All(s=>s.Equals("yandex,station-lite2",StringComparison.OrdinalIgnoreCase));
            return Regex.IsMatch(Value("model"), @"\A(?:Yandex[ ,/_-]+)?(?:Station[ ,/_-]+Lite[ ,/_-]*2[ ,/_(-]*)?Plum[ )]*\z",RegexOptions.IgnoreCase);
        } }
        internal bool Armhf { get { return Value("elf") == "1 1" && Value("machine") == "40 0" && Value("float")=="hard"; } }
        internal bool Has(string field, string name) { return Value(field).Split(' ').Contains(name); }
        internal string Stamp { get { return DeviceTransport.Hash(DeviceTransport.Utf.GetBytes(string.Join("\n",new[]{Value("binding"),Value("compatible"),Value("model"),Value("sku"),Value("build"),Value("elf"),Value("machine"),Value("float")}))); } }
        internal bool Bound { get { return Plum && Regex.IsMatch(Value("binding"), @"\A[a-f0-9]{64}\z") && Regex.IsMatch(Value("build"),@"\A[0-9][0-9A-Za-z._-]{2,99}\z") && (Value("sku")=="" || Value("sku")=="YNDX-00026" || Value("sku")=="YNDX-00028"); } }
        internal bool Ready { get { return Bound && Value("uid")=="0" && Armhf; } }
        internal bool TtsReady { get { return Ready && Value("build")==KnownBuild && Value("ipc")=="listen" && Has("tools","socat") && Has("processes","maind"); } }
        internal bool LedReady { get { return Ready && (Value("sku")=="YNDX-00026"||Value("sku")=="YNDX-00028") && Value("build")==KnownBuild && Value("rings")=="9" && Has("tools","nohup") && Has("tools","setsid") && !Has("processes","pulsar"); } }
        internal static readonly string[] SafeKeys = "model compatible sku build kernel uid elf machine libc free mem load cpus net tools processes alsa rings clock ipc bridge ipclog float ota sources".Split(' ');
        internal static DeviceInventory Parse(string input)
        {
            var result = new DeviceInventory();
            foreach (var line in input.Split('\n')) {
                int ix=line.IndexOf('=');
                if (ix<1) continue;
                string key=line.Substring(0,ix), val=line.Substring(ix+1).Trim();
                if ((!SafeKeys.Contains(key) && key!="binding") || result.Values.ContainsKey(key) || val.Length>200) continue;
                if (!Regex.IsMatch(val,@"\A[A-Za-z0-9 /:,()._+-]*\z")) continue;
                // Do not collect arbitrary board serials or local addresses in diagnostic labels.
                if ((key=="model" || key=="compatible") && Regex.IsMatch(val,@"(?:[0-9A-Fa-f]{2}:){5}|(?:\d{1,3}\.){3}\d{1,3}|[A-Za-z]{1,4}\d[A-Za-z0-9]{10,}")) continue;
                result.Values[key]=val;
            }
            return result;
        }
        internal string Report()
        {
            string[] labels={"Модель из device tree","Совместимость платы","Подтверждённый SKU","Полная сборка","Ядро","UID","Класс и порядок байтов ELF","Машинный тип ELF","libc","Свободно в /data, КиБ","ОЗУ всего, КиБ","Средняя нагрузка","Ядра","Сетевые интерфейсы","Найденные утилиты","Найденные службы","Число ALSA-карт","Каналы RGB из 9","Интерфейс часов (эвристика)","IPC озвучки","Загрузчик Quasar Bridge","Строки / ошибки в ограниченной выборке лога","ARM float ABI","Роль updatesd в launch.json (только один из механизмов OTA)","Настроенные реестры yse, количество"};
            var sb=new StringBuilder();
            for (int i=0;i<SafeKeys.Length;i++) sb.AppendLine(labels[i]+" — "+(Value(SafeKeys[i])=="" ? "нет данных" : Value(SafeKeys[i])));
            sb.AppendLine("Приложение /system/vendor/quasar/maind — "+(Armhf?"32-битный ARM ELF, little endian, hard-float":"целевой ABI armhf не подтверждён"));
            return sb.ToString();
        }
    }
}
