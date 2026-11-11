using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AliceDesk
{
    internal static class DeviceLight
    {
        private const string Paths = @"for zone in 1 2 3; do for color in red green blue; do
d=/sys/class/leds/plum:$color:ring$zone
";
        private const string Helpers = @"stock() { sed -n 's/.*\[\([^]]*\)\].*/\1/p' ""$1/trigger""; }
unmuted() { awk '/[|]mute-switch / {for(i=1;i<=NF;i++){if($i==""hi"")a=1;if($i==""lo"")a=-1};n++} END {exit !(n==1 && a==1)}' /sys/kernel/debug/gpio; }
";
        internal static string Snapshot(DeviceTransport d,CancellationToken c)
        {
            string data=d.Shell("set -e\n"+Helpers+"unmuted || exit 81\n"+Paths+
                "[ -w \"$d/brightness\" ] && [ -w \"$d/trigger\" ] && [ \"$(stock \"$d\")\" = led_ring_fb ] || exit 82\n"+
                "printf '%s %s %s %s\\n' \"$zone\" \"$color\" \"$(cat \"$d/brightness\")\" \"$(stock \"$d\")\"\ndone; done",c);
            var lines=data.Trim().Split('\n');
            if(lines.Length!=9||!lines.All(x=>Regex.IsMatch(x,@"\A[123] (?:red|green|blue) (?:0|[1-9][0-9]{0,2}) led_ring_fb\z") && int.Parse(x.Split(' ')[2])<=255))throw new IOException("Не все девять каналов освещения вернули штатное состояние.");
            return string.Join("\n",lines);
        }
        internal static void Pulse(DeviceTransport d,string snapshot,CancellationToken c)
        {
            string script=@"set -eu
umask 077
base=$(mktemp -d /tmp/alicedesk-led.XXXXXX)
cat > ""$base/before"" <<'BEFORE'
"+snapshot+@"
BEFORE
cat > ""$base/run"" <<'WORKER'
set -eu
base=$1
"+Helpers+@"
lock=/tmp/alicedesk-led-owner
mkdir ""$lock"" || exit 84
owned=0
restore() {
  success=1
  if [ ""$owned"" = 1 ]; then
    while read -r zone color old trig; do
      d=/sys/class/leds/plum:$color:ring$zone
      now=$(stock ""$d"")
      if [ ""$now"" = none ]; then printf '%s\n' ""$old"" > ""$d/brightness"" || success=0; fi
    done < ""$base/live""
    while read -r zone color old trig; do
      d=/sys/class/leds/plum:$color:ring$zone
      if [ ""$(stock ""$d"")"" = none ]; then printf '%s\n' ""$trig"" > ""$d/trigger"" || success=0; fi
      [ ""$(stock ""$d"")"" = ""$trig"" ] || success=0
    done < ""$base/live""
  fi
  rmdir ""$lock"" || success=0
  [ ""$owned"" = 1 ] && [ ""$success"" = 1 ] && [ -f ""$base/painted"" ] && echo OK > ""$base/result"" || echo FAIL > ""$base/result""
}
trap restore EXIT
trap 'exit 85' HUP INT TERM
unmuted || exit 81
while read -r zone color expected trig; do
  d=/sys/class/leds/plum:$color:ring$zone
  [ ""$(stock ""$d"")"" = ""$trig"" ] || exit 83
  printf '%s %s %s %s\n' ""$zone"" ""$color"" ""$(cat ""$d/brightness"")"" ""$trig"" >> ""$base/live""
done < ""$base/before""
cmp -s ""$base/live"" ""$base/before"" || exit 83
owned=1
while read -r zone color old trig; do
  d=/sys/class/leds/plum:$color:ring$zone
  unmuted || exit 81
  printf 'none\n' > ""$d/trigger""
  val=0; [ ""$color"" != blue ] || val=24
  echo ""$val"" > ""$d/brightness""
done < ""$base/live""
touch ""$base/painted""
for tick in 1 2 3 4 5 6 7 8; do unmuted || break; sleep 0.25; done
WORKER
nohup setsid sh ""$base/run"" ""$base"" </dev/null >/dev/null 2>&1 &
for w in 1 2 3 4 5 6 7; do [ ! -f ""$base/result"" ] || break; sleep 1; done
if [ -f ""$base/result"" ]; then
  result=$(cat ""$base/result"")
  rm -f ""$base/run"" ""$base/result"" ""$base/live"" ""$base/before"" ""$base/painted""
  rmdir ""$base""
  [ ""$result"" = OK ] || exit 87
else
  exit 88
fi";
            d.Shell(script,c,14000);
        }
    }
}
