using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AliceDesk
{
    // Compile DeviceLight in isolation without a device or access to the application.
    internal sealed class DeviceTransport
    {
        internal string Shell(string script, CancellationToken cancellation, int timeout = 0) { throw new NotSupportedException(); }
    }
    internal static class LedScriptFixture
    {
        static void Main(string[] args)
        {
            var snapshot = new StringBuilder();
            for (int zone = 1; zone <= 3; zone++)
                foreach (string color in new[] {"red", "green", "blue"})
                    snapshot.Append(zone).Append(' ').Append(color).Append(" 7 led_ring_fb\n");
            File.WriteAllText(args[0], DeviceLight.PulseScript(snapshot.ToString().TrimEnd('\n')), new UTF8Encoding(false));
        }
    }
}
