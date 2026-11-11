using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AliceDesk
{
    internal static class DeviceAudio
    {
        internal const int MaxWave=1024*1024;
        internal static string RadioUrl(string input)
        {
            Uri uri;
            if(input==null||input.Length>1500||!Uri.TryCreate(input.Trim(),UriKind.Absolute,out uri)||uri.Scheme!="https"||
                uri.UserInfo!=""||uri.Fragment!=""||uri.Query!=""||!Regex.IsMatch(uri.AbsoluteUri,@"\Ahttps://[A-Za-z0-9.:\[\]/%_~+-]+\z"))
                throw new ArgumentException("Для пробы нужен прямой HTTPS-адрес аудиопотока без логина, ключа, параметров или фрагмента.");
            return uri.AbsoluteUri;
        }
        internal static void ValidateWave(byte[] input)
        {
            if(input.Length<44||input.Length>MaxWave||Encoding.ASCII.GetString(input,0,4)!="RIFF"||Encoding.ASCII.GetString(input,8,4)!="WAVE")
                throw new InvalidDataException("Выберите обычный RIFF/WAVE-файл до 1 МиБ.");
        }
        internal static void Wav(DeviceTransport t,byte[] input,CancellationToken c)
        {
            ValidateWave(input);
            string sha=DeviceTransport.Hash(input);
            string script=@"set -eu
command -v timeout >/dev/null
cd /system/vendor/quasar
umask 077
tmp=$(mktemp /tmp/alicedesk-audio.XXXXXX)
trap 'rm -f ""$tmp""' EXIT
trap 'exit 91' HUP INT TERM
base64 -d > ""$tmp"" <<'ALICEDESK_INPUT'
"+Convert.ToBase64String(input)+@"
ALICEDESK_INPUT
[ ""$(sha256sum ""$tmp"" | cut -d' ' -f1)"" = "+sha+@" ] || exit 92
LD_LIBRARY_PATH=/system/vendor/quasar:libs timeout 12 ./maind --gst-launch ""filesrc location=$tmp ! decodebin ! audioconvert ! audioresample ! alsasink"" >/dev/null 2>&1 || { rc=$?; [ ""$rc"" = 124 ] || [ ""$rc"" = 143 ] || exit ""$rc""; }";
            t.Shell(script,c,24000);
        }
        internal static void Radio(DeviceTransport t,string url,CancellationToken c)
        {
            string safe=RadioUrl(url);
            string script=@"set -eu
cd /system/vendor/quasar
url='"+safe+@"'
LD_LIBRARY_PATH=/system/vendor/quasar:libs timeout 12 ./maind --gst-launch ""souphttpsrc location=$url ! decodebin ! audioconvert ! audioresample ! alsasink"" >/dev/null 2>&1 || { rc=$?; [ ""$rc"" = 124 ] || [ ""$rc"" = 143 ] || exit ""$rc""; }";
            t.Shell(script,c,20000);
        }
    }
}
