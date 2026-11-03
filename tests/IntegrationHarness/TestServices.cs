// Integration-test helpers for Service Monitor. Compiled with the .NET Framework compiler that ships with
// Windows (C# 5), so they run on any test machine without installing anything:
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /out:SvcMonTest.exe TestServices.cs
//
//   SvcMonTest.exe          as the service "SvcMonTestCrashy": a misbehaving service that is told what to do by writing a
//                           word into C:\ProgramData\SvcMonTest\command.txt:
//                             crash    – throws an unhandled exception (like a vendor service that shows a MessageBox from inside the service)
//                             stop     – stops itself cleanly
//                             freeze   – stops answering on TCP 47999 while still "Running"
//                             unfreeze – answers again
//                           Create C:\ProgramData\SvcMonTest\hang-on-start to make the next start hang for 60 s.
//   SvcMonTest.exe sink     as the service "SvcMonTestSink": captures SMTP mail on 127.0.0.1:2525 and HTTP POSTs on
//                           127.0.0.1:8099 into C:\ProgramData\SvcMonTest\inbox so notifications can be checked offline.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Threading;

public static class Program
{
    public const string Dir = @"C:\ProgramData\SvcMonTest";

    public static void Main(string[] args)
    {
        Directory.CreateDirectory(Dir);
        if (args.Length > 0 && args[0] == "sink") ServiceBase.Run(new SinkService());
        else ServiceBase.Run(new CrashyService());
    }
}

public class CrashyService : ServiceBase
{
    private TcpListener _listener;
    private volatile bool _running;

    public CrashyService() { ServiceName = "SvcMonTestCrashy"; }

    protected override void OnStart(string[] args)
    {
        var hang = Path.Combine(Program.Dir, "hang-on-start");
        if (File.Exists(hang))
        {
            File.Delete(hang);
            Thread.Sleep(TimeSpan.FromSeconds(60)); // stays "Starting" → hung
        }
        _running = true;
        Listen();
        new Thread(Loop) { IsBackground = true }.Start();
    }

    protected override void OnStop()
    {
        _running = false;
        if (_listener != null) _listener.Stop();
    }

    private void Listen()
    {
        _listener = new TcpListener(IPAddress.Loopback, 47999);
        _listener.Start();
        var l = _listener;
        new Thread(() =>
        {
            try { while (true) { using (var c = l.AcceptTcpClient()) { } } }
            catch (Exception) { }
        }) { IsBackground = true }.Start();
    }

    private void Loop()
    {
        var file = Path.Combine(Program.Dir, "command.txt");
        while (_running)
        {
            Thread.Sleep(500);
            if (!File.Exists(file)) continue;
            var command = File.ReadAllText(file).Trim().ToLowerInvariant();
            File.Delete(file);
            if (command == "crash")
                new Thread(() => { throw new InvalidOperationException("Simulated crash requested by the integration test."); }).Start();
            else if (command == "stop") Stop();
            else if (command == "freeze") _listener.Stop();
            else if (command == "unfreeze") Listen();
        }
    }
}

public class SinkService : ServiceBase
{
    private string _inbox;

    public SinkService() { ServiceName = "SvcMonTestSink"; }

    protected override void OnStart(string[] args)
    {
        _inbox = Path.Combine(Program.Dir, "inbox");
        Directory.CreateDirectory(_inbox);
        Serve(2525, Smtp);
        Serve(8099, Http);
    }

    private void Serve(int port, Action<TcpClient> handler)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        new Thread(() =>
        {
            while (true)
            {
                var client = listener.AcceptTcpClient();
                new Thread(() => { try { handler(client); } catch (Exception) { } finally { client.Close(); } }) { IsBackground = true }.Start();
            }
        }) { IsBackground = true }.Start();
    }

    private string NewFile(string ext)
    {
        return Path.Combine(_inbox, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
    }

    private void Smtp(TcpClient client)
    {
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
        writer.WriteLine("220 sink ESMTP");
        var envelope = new StringBuilder();
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            var upper = line.ToUpperInvariant();
            if (upper.StartsWith("EHLO")) { writer.WriteLine("250-sink"); writer.WriteLine("250-8BITMIME"); writer.WriteLine("250 SMTPUTF8"); }
            else if (upper.StartsWith("HELO")) writer.WriteLine("250 sink");
            else if (upper.StartsWith("MAIL") || upper.StartsWith("RCPT")) { envelope.AppendLine(line); writer.WriteLine("250 OK"); }
            else if (upper == "DATA")
            {
                writer.WriteLine("354 Go ahead");
                var data = new StringBuilder();
                while ((line = reader.ReadLine()) != null && line != ".") data.AppendLine(line.StartsWith("..") ? line.Substring(1) : line);
                File.WriteAllText(NewFile(".eml"), "X-Envelope: " + envelope.ToString().Replace("\r\n", " | ") + "\r\n" + data);
                envelope.Clear();
                writer.WriteLine("250 Queued");
            }
            else if (upper == "QUIT") { writer.WriteLine("221 Bye"); return; }
            else writer.WriteLine("250 OK");
        }
    }

    private void Http(TcpClient client)
    {
        var stream = client.GetStream();
        var headerBytes = new MemoryStream();
        var last4 = 0;
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0) return;
            headerBytes.WriteByte((byte)b);
            last4 = (last4 << 8) | b;
            if (last4 == 0x0D0A0D0A) break;
        }
        var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
        var length = 0;
        foreach (var h in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
            if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(h.Substring(15).Trim());
        var body = new byte[length];
        var read = 0;
        while (read < length) { var n = stream.Read(body, read, length - read); if (n <= 0) break; read += n; }
        File.WriteAllText(NewFile(".json"), headers.Split('\r')[0] + "\r\n" + Encoding.UTF8.GetString(body, 0, read));
        var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        stream.Write(ok, 0, ok.Length);
    }
}
