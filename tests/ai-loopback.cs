using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AliceDesk
{
    internal static class AiLoopback
    {
        internal static void Verify()
        {
            var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            try
            {
                int port = ((IPEndPoint)server.LocalEndpoint).Port;
                string baseUrl = "http://127.0.0.1:" + port + "/v1";
                var endpoint = new AiEndpoint("local", baseUrl);
                Task fixture = Task.Run(delegate {
                    Exchange(server,"GET /v1/models",200,"{\"data\":[{\"id\":\"fixture-model\"}]}","");
                    Exchange(server,"POST /v1/chat/completions",200,
                        "{\"choices\":[{\"message\":{\"content\":\"local answer\"},\"finish_reason\":\"stop\"}]}","");
                    Exchange(server,"GET /v1/models",307,"","Location: " + baseUrl + "/unexpected\r\n");
                });
                using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
                using (var client = new AiClient())
                {
                    string[] options = client.Models(endpoint, "", limit.Token).GetAwaiter().GetResult();
                    if (options.Length != 1 || options[0] != "fixture-model") throw new InvalidDataException("Fixture model missing.");
                    var answer = client.Generate(endpoint,"",options[0],new List<AiTurn>(),"fixture question",limit.Token).GetAwaiter().GetResult();
                    if (answer.Text != "local answer") throw new InvalidDataException("Fixture chat response missing.");
                    bool blocked = false;
                    try { client.Models(endpoint,"",limit.Token).GetAwaiter().GetResult(); }
                    catch (AiFailure ex) { blocked = ex.Message.Contains("307"); }
                    if (!blocked) throw new InvalidDataException("Real HTTP redirect was accepted.");
                }
                fixture.GetAwaiter().GetResult();
                if (server.Pending()) throw new InvalidDataException("A second HTTP connection followed the redirect.");
            }
            finally { server.Stop(); }
        }

        private static void Exchange(TcpListener server, string expected, int status, string json, string extra)
        {
            using (var connection = server.AcceptTcpClient())
            using (var network = connection.GetStream())
            using (var reader = new StreamReader(network, Encoding.ASCII, false, 4096, true))
            {
                connection.ReceiveTimeout = 5000;
                connection.SendTimeout = 5000;
                string first = reader.ReadLine();
                if (first == null || !first.StartsWith(expected + " HTTP/")) throw new InvalidDataException("Unexpected path or method.");
                int length = 0;
                bool expect = false;
                string line;
                while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    if (line.StartsWith("Authorization:",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Local request leaked credentials.");
                    if (line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))
                        length = Int32.Parse(line.Substring(15).Trim(),CultureInfo.InvariantCulture);
                    if (line.Equals("Expect: 100-continue",StringComparison.OrdinalIgnoreCase)) expect = true;
                }
                if (expect) Write(network,"HTTP/1.1 100 Continue\r\n\r\n");
                if (length < 0 || length > 8192) throw new InvalidDataException("Unexpected content length.");
                if (expected.StartsWith("POST"))
                {
                    var body = new char[length];
                    if (reader.ReadBlock(body,0,length) != length || !new String(body).Contains("fixture question"))
                        throw new InvalidDataException("Missing chat request body.");
                }
                Write(network,"HTTP/1.1 " + status + (status == 200 ? " OK" : " Temporary Redirect") +
                    "\r\nConnection: close\r\nContent-Type: application/json\r\n" + extra + "Content-Length: " +
                    Encoding.UTF8.GetByteCount(json) + "\r\n\r\n" + json);
            }
        }
        private static void Write(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes,0,bytes.Length);
            stream.Flush();
        }
    }
}
