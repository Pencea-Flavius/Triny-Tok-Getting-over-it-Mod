using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MelonLoader;
using Newtonsoft.Json;

namespace TrinyTokGOI
{
    public class TrinyTokClient
    {
        private string host;
        private int port;
        private Socket socket;
        private bool running = true;
        private Queue<EffectRequest> incoming = new Queue<EffectRequest>();
        private MelonLogger.Instance logger;

        public TrinyTokClient(string host, int port, MelonLogger.Instance logger)
        {
            this.host   = host;
            this.port   = port;
            this.logger = logger;
        }

        public bool IsRunning => running;

        public void Start()
        {
            running = true;
            new Thread(NetworkLoop) { IsBackground = true }.Start();
            new Thread(DispatchLoop) { IsBackground = true }.Start();
        }

        private void NetworkLoop()
        {
            while (running)
            {
                logger.Msg($"Connecting to {host}:{port}...");
                try
                {
                    IPAddress ip;
                    if (!IPAddress.TryParse(host, out ip))
                        ip = Dns.GetHostAddresses(host)[0];

                    var endpoint = new IPEndPoint(ip, port);
                    socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                    var ar = socket.BeginConnect(endpoint, null, null);
                    bool connected = ar.AsyncWaitHandle.WaitOne(10000, true);

                    if (connected && socket.Connected)
                    {
                        logger.Msg($"Connected to {host}:{port}");
                        socket.ReceiveTimeout = 5000;
                        ReceiveLoop();
                    }
                    else
                    {
                        logger.Warning("Connection timed out");
                    }
                }
                catch (Exception e)
                {
                    logger.Warning($"Connection error: {e.GetType().Name} — {e.Message}");
                }

                try { socket?.Close(); } catch { }
                socket = null;

                if (!running) break;

                logger.Msg("Retrying in 10s...");
                for (int i = 0; i < 100 && running; i++)
                    Thread.Sleep(100);
            }
        }

        private void ReceiveLoop()
        {
            string buffer = "";
            byte[] raw = new byte[4096];

            while (running)
            {
                try
                {
                    int received = socket.Receive(raw);
                    if (received <= 0) { logger.Msg("Server closed connection"); break; }

                    buffer += Encoding.ASCII.GetString(raw, 0, received);
                    string[] chunks = buffer.Split('\0');
                    buffer = chunks[chunks.Length - 1];

                    for (int i = 0; i < chunks.Length - 1; i++)
                    {
                        string msg = chunks[i].Trim();
                        if (string.IsNullOrEmpty(msg)) continue;
                        try
                        {
                            var req = JsonConvert.DeserializeObject<EffectRequest>(msg);
                            if (req == null || req.IsKeepAlive()) continue;
                            lock (incoming) { incoming.Enqueue(req); }
                        }
                        catch (Exception ex)
                        {
                            logger.Error($"JSON parse error: {ex.Message} | raw: {msg}");
                        }
                    }
                }
                catch (SocketException se)
                {
                    if (se.SocketErrorCode == SocketError.TimedOut)
                        SendKeepAlive();
                    else if (se.SocketErrorCode == SocketError.Interrupted)
                        { logger.Msg("Receive interrupted, reconnecting..."); break; }
                    else
                        { logger.Warning($"Socket error: {se.SocketErrorCode} — {se.Message}"); break; }
                }
                catch (ObjectDisposedException) { break; }
                catch (ThreadAbortException) { Thread.ResetAbort(); break; }
                catch (Exception ex) { if (running) logger.Error($"Receive error: {ex.Message}"); break; }
            }

            logger.Msg("Disconnected");
        }

        private void SendKeepAlive()
        {
            try
            {
                if (socket != null && socket.Connected)
                {
                    var ka = new EffectResponse(0, EffectResponse.STATUS_KEEPALIVE, "");
                    Send(JsonConvert.SerializeObject(ka));
                }
            }
            catch { }
        }

        private void DispatchLoop()
        {
            while (running)
            {
                EffectRequest req = null;
                lock (incoming)
                {
                    if (incoming.Count > 0)
                        req = incoming.Dequeue();
                }

                if (req != null)
                    TrinyTokGOIMod.Instance?.HandleRequest(req);

                Thread.Sleep(50);
            }
        }

        public void Stop()
        {
            running = false;
            try { socket?.Close(); } catch { }
        }

        public void SendResponse(EffectResponse response)
        {
            try { Send(JsonConvert.SerializeObject(response)); }
            catch (Exception e) { logger.Error($"SendResponse failed: {e.Message}"); }
        }

        private void Send(string json)
        {
            if (socket != null && socket.Connected)
                socket.Send(Encoding.ASCII.GetBytes(json + "\0"));
        }
    }

    public class EffectRequest
    {
        public string code;
        public int    id;
        public int    duration;
        public string type;
        public string viewer;

        public bool IsKeepAlive() => id == 0 && type == "255";
    }

    public class EffectResponse
    {
        public int    id;
        public int    status;
        public string message;

        public const int STATUS_SUCCESS   = 0;
        public const int STATUS_FAILURE   = 1;
        public const int STATUS_RETRY     = 3;
        public const int STATUS_KEEPALIVE = 255;

        public EffectResponse(int id, int status, string message = "")
        {
            this.id      = id;
            this.status  = status;
            this.message = message;
        }
    }
}
