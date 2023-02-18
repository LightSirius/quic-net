using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using StirlingLabs.MsQuic;
using StirlingLabs.MsQuic.Bindings;

namespace Test;

public class Test
{
    private const ushort Port = 31999;
    private const string Loopback = "127.0.0.1";

    private static QuicServerConnection _serverSide = null!;
    private static QuicClientConnection _clientSide = null!;
    private static QuicListener _listener = null!;
    private static QuicRegistration _reg = null!;
    private static QuicCertificate _cert;

    private static void Main()
    {
        Setup();
        Send();
    }

    private static void Setup()
    {
        _cert = new(File.OpenRead("D:\\CS\\Github\\quic-net\\localhost.p12"));
        _reg = new QuicRegistration("test");

        using var listenerCfg = new QuicServerConfiguration(_reg, "test");

        listenerCfg.ConfigureCredentials(_cert);

        _listener = new QuicListener(listenerCfg);
        _listener.Start(new(IPAddress.Loopback, Port));

        using var clientCfg = new QuicClientConfiguration(_reg, "test");
        clientCfg.ConfigureCredentials();
        _clientSide = new(clientCfg);

        // 생성자에 들어간 숫자만큼 시그널이 올떄까지 대기
        using var cde = new CountdownEvent(2);

        _clientSide.CertificateReceived += (_, _, _, _, _)
           => {
               Console.WriteLine("## handled client CertificateReceived");
               return MsQuic.QUIC_STATUS_SUCCESS;
           };

        _listener.NewConnection += (_, connection) => {
            _serverSide = connection;
            connection.CertificateReceived += (_, _, _, _, _)
                => {
                    Console.WriteLine("## handled server CertificateReceived");
                    return MsQuic.QUIC_STATUS_SUCCESS;
                };
            Console.WriteLine("## handled _listener.NewConnection");
        };

        _listener.ClientConnected += (_, connection) => {
            Console.WriteLine("handling _listener.ClientConnected");
            //_serverSide.Should().Be(connection); // 요건 뭐지?
            cde.Signal();
            Console.WriteLine("## handled _listener.ClientConnected");
        };

        _clientSide.Connected += _ => {
            Console.WriteLine("handling _clientSide.Connected");
            cde.Signal();
            Console.WriteLine("## handled _clientSide.Connected");
        };

        _clientSide.Start(Loopback, Port);
        Console.WriteLine("## starting _clientSide");

        cde.Wait(); // 여기서 대기

        _listener.UnobservedException += (_, info) => {
            info.Throw();
        };

        _clientSide.UnobservedException += (_, info) => {
            info.Throw();
        };

        _serverSide.UnobservedException += (_, info) => {
            info.Throw();
        };
    }

    private unsafe static void Send()
    {
        Memory<byte> utf8Hello = Encoding.UTF8.GetBytes("Hello Quic!!");
        var dataLength = utf8Hello.Length;

        using var cde = new CountdownEvent(1);

        QuicStream serverStream = null!;

        _serverSide.IncomingStream += (_, stream) => {
            Console.WriteLine("## handling _serverSide.IncomingStream");
            serverStream = stream;
            cde.Signal();
        };

        Console.WriteLine("## waiting for _serverSide.IncomingStream");

        using var clientStream = _clientSide.OpenStream();

        cde.Wait();
        cde.Reset();

        // unsafe 관련해서 fixed나 C#에서 포인터 사용이 가능한 이유는 찾아보기
        Span<byte> dataReceived = stackalloc byte[dataLength];

        fixed (byte* pDataReceived = dataReceived)
        {
            var ptrDataReceived = (IntPtr)pDataReceived;

            // 서버 스트림에서 데이터를 받았을때 콜백을 정의
            serverStream.DataReceived += _ => {
                var dataReceived = new Span<byte>((byte*)ptrDataReceived, dataLength);
                var read = serverStream.Receive(dataReceived);

                string message = Encoding.UTF8.GetString((byte*)ptrDataReceived, read);

                Console.WriteLine($"##  handled serverStream.DataReceived size: {read}, message: {message}");

                cde.Signal();
            };

        }

        // 클라에서 스트림 데이터 전송
        var task = clientStream.SendAsync(utf8Hello, QUIC_SEND_FLAGS.FIN);

        Console.WriteLine("## waiting for serverStream.DataReceived");
        cde.Wait();

        Console.WriteLine("## waiting for clientStream.SendAsync");
        task.Wait();
    }
}