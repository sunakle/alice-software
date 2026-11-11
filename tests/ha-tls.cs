using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace AliceDesk
{
    // Separate client limited to this loopback fixture. It accepts only its freshly generated leaf.
    internal static class HaTlsChecks
    {
        internal static void Run()
        {
            MediaHome.Test();
            using (var key = RSA.Create(2048))
            {
                var certRequest = new CertificateRequest("CN=fixture.ha.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("fixture.ha.invalid");
                certRequest.CertificateExtensions.Add(san.Build());
                using (var generated = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1)))
                using (var cert = new X509Certificate2(generated.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.UserKeySet))
                {
                    var local = new TcpListener(IPAddress.Loopback, 0);
                    local.Start();
                    try
                    {
                        int port = ((IPEndPoint)local.LocalEndpoint).Port;
                        Task server = Task.Run(delegate {
                            using (var connection = local.AcceptTcpClient())
                            using (var tls = new SslStream(connection.GetStream(), false))
                            {
                                connection.ReceiveTimeout = connection.SendTimeout = 7000;
                                tls.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
                                using (var reader = new StreamReader(tls,Encoding.ASCII,false,1024,true))
                                {
                                    bool hostFound = false;
                                    string line;
                                    while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                                        if (line.Equals("Host: fixture.ha.invalid:" + port,StringComparison.OrdinalIgnoreCase)) hostFound = true;
                                    if (!hostFound) throw new InvalidDataException("Original Host absent on TLS server.");
                                }
                                var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 2\r\n\r\nok");
                                tls.Write(bytes,0,bytes.Length);
                            }
                        });
                        Uri origin = new Uri("https://fixture.ha.invalid:" + port);
                        try { using (var handler = new HttpClientHandler {AllowAutoRedirect=false,UseProxy=false,SslProtocols=SslProtocols.Tls12})
                        {
                            bool nameVerified = false;
                            handler.ServerCertificateCustomValidationCallback = delegate(HttpRequestMessage request,X509Certificate2 remote,X509Chain chain,SslPolicyErrors errors) {
                                nameVerified = remote != null && remote.Thumbprint == cert.Thumbprint &&
                                    (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0;
                                return nameVerified;
                            };
                            using (var client = new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(8)})
                            using (var req = new HttpRequestMessage(HttpMethod.Get,new Uri(origin,"/api/states/x")))
                            {
                                MediaHome.PinRequest(req,origin,IPAddress.Loopback);
                                using (var response = client.SendAsync(req).GetAwaiter().GetResult())
                                    if (!nameVerified || !response.IsSuccessStatusCode) throw new InvalidDataException("TLS name did not match selected origin.");
                            }
                        } } catch (Exception ex) {
                            if (server.IsFaulted) throw new AggregateException(ex, server.Exception);
                            throw;
                        }
                        server.GetAwaiter().GetResult();
                    }
                    finally { local.Stop(); }
                }
            }
        }
    }
}
