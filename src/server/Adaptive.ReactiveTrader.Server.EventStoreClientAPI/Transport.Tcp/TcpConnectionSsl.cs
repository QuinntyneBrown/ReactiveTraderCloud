using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using EventStore.ClientAPI.Common;
using EventStore.ClientAPI.Common.Utils;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace EventStore.ClientAPI.Transport.Tcp
{
    internal class TcpConnectionSsl : TcpConnectionBase, ITcpConnection
    {
        public static ITcpConnection CreateConnectingConnection(ILogger log,
                                                                Guid connectionId, 
                                                                IPEndPoint remoteEndPoint, 
                                                                string targetHost,
                                                                bool validateServer,
                                                                TcpClientConnector connector, 
                                                                TimeSpan connectionTimeout,
                                                                Action<ITcpConnection> onConnectionEstablished, 
                                                                Action<ITcpConnection, SocketError> onConnectionFailed,
                                                                Action<ITcpConnection, SocketError> onConnectionClosed)
        {
            var connection = new TcpConnectionSsl(log, connectionId, remoteEndPoint, onConnectionClosed);
            // ReSharper disable ImplicitlyCapturedClosure
            connector.InitConnect(remoteEndPoint,
                                  (_, socket) =>
                                  {
                                      connection.InitClientSocket(socket, targetHost, validateServer);
                                      if (onConnectionEstablished != null)
                                          Task.Run(() => onConnectionEstablished(connection));
                                  },
                                  (_, socketError) =>
                                  {
                                      if (onConnectionFailed != null)
                                          Task.Run(() => onConnectionFailed(connection, socketError));
                                  }, connection, connectionTimeout);
            // ReSharper restore ImplicitlyCapturedClosure
            return connection;
        }

        public Guid ConnectionId { get { return _connectionId; } }
        public int SendQueueSize { get { return _sendQueue.Count; } }

        private readonly Guid _connectionId;
        private readonly ILogger _log;

        private readonly ConcurrentQueue<ArraySegment<byte>> _sendQueue = new ConcurrentQueue<ArraySegment<byte>>();
        private readonly ConcurrentQueue<ArraySegment<byte>> _receiveQueue = new ConcurrentQueue<ArraySegment<byte>>();
        private readonly MemoryStream _memoryStream = new MemoryStream();

        private readonly object _streamLock = new object();
        private bool _isSending;
        private int _receiveHandling;
        private int _isClosed;

        private Action<ITcpConnection, IEnumerable<ArraySegment<byte>>> _receiveCallback;
        private readonly Action<ITcpConnection, SocketError> _onConnectionClosed;

        private SslStream _sslStream;
        private bool _isAuthenticated;
        private int _sendingBytes;
        private bool _validateServer;
        private readonly byte[] _receiveBuffer = new byte[TcpConfiguration.SocketBufferSize];

        private TcpConnectionSsl(ILogger log, Guid connectionId, IPEndPoint remoteEndPoint,
                                 Action<ITcpConnection, SocketError> onConnectionClosed): base(remoteEndPoint)
        {
            Ensure.NotNull(log, "log");
            Ensure.NotEmptyGuid(connectionId, "connectionId");

            _log = log;
            _connectionId = connectionId;
            _onConnectionClosed = onConnectionClosed;
        }

        private async void InitClientSocket(Socket socket, string targetHost, bool validateServer)
        {
            Ensure.NotNull(targetHost, "targetHost");

            InitConnectionBase(socket);
            //_log.Info("TcpConnectionSsl::InitClientSocket({0}, L{1})", RemoteEndPoint, LocalEndPoint);

            _validateServer = validateServer;

            lock (_streamLock)
            {
                try
                {
                    socket.NoDelay = true;
                }
                catch (ObjectDisposedException)
                {
                    CloseInternal(SocketError.Shutdown, "Socket is disposed.");
                    return;
                }

                _sslStream = new SslStream(new NetworkStream(socket, true), false, ValidateServerCertificate, null);
                try
                {
                    Task.Run(() =>_sslStream.AuthenticateAsClientAsync(targetHost)).ContinueWith(OnEndAuthenticateAsClient);

                }
                catch (AuthenticationException exc)
                {
                    _log.Info(exc, "[S{0}, L{1}]: Authentication exception on BeginAuthenticateAsClient.", RemoteEndPoint, LocalEndPoint);
                    CloseInternal(SocketError.SocketError, exc.Message);
                }
                catch (ObjectDisposedException)
                {
                    CloseInternal(SocketError.SocketError, "SslStream disposed.");
                }
                catch (Exception exc)
                {
                    _log.Info(exc, "[S{0}, L{1}]: Exception on BeginAuthenticateAsClient.", RemoteEndPoint, LocalEndPoint);
                    CloseInternal(SocketError.SocketError, exc.Message);
                }
            }
        }

        private void OnEndAuthenticateAsClient(IAsyncResult ar)
        {
            try
            {
                lock (_streamLock)
                {
                    var sslStream = (SslStream) ar.AsyncState;
                    DisplaySslStreamInfo(sslStream);
                    _isAuthenticated = true;
                }
                StartReceive();
                TrySend();
            }
            catch (AuthenticationException exc)
            {
                _log.Info(exc, "[S{0}, L{1}]: Authentication exception on EndAuthenticateAsClient.", RemoteEndPoint, LocalEndPoint);
                CloseInternal(SocketError.SocketError, exc.Message);
            }
            catch (ObjectDisposedException)
            {
                CloseInternal(SocketError.SocketError, "SslStream disposed.");
            }
            catch (Exception exc)
            {
                _log.Info(exc, "[S{0}, L{1}]: Exception on EndAuthenticateAsClient.", RemoteEndPoint, LocalEndPoint);
                CloseInternal(SocketError.SocketError, exc.Message);
            }
        }

        // The following method is invoked by the RemoteCertificateValidationDelegate. 
        public bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (!_validateServer)
                return true;

            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;
            _log.Error("[S{0}, L{1}]: Certificate error: {1}", RemoteEndPoint, LocalEndPoint, sslPolicyErrors);
            // Do not allow this client to communicate with unauthenticated servers. 
            return false;
        }

        private void DisplaySslStreamInfo(SslStream stream)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("[S{0}, L{1}]:\n", RemoteEndPoint, LocalEndPoint);
            sb.AppendFormat("Cipher: {0} strength {1}\n", stream.CipherAlgorithm, stream.CipherStrength);
            sb.AppendFormat("Hash: {0} strength {1}\n", stream.HashAlgorithm, stream.HashStrength);
            sb.AppendFormat("Key exchange: {0} strength {1}\n", stream.KeyExchangeAlgorithm, stream.KeyExchangeStrength);
            sb.AppendFormat("Protocol: {0}\n", stream.SslProtocol);
            sb.AppendFormat("Is authenticated: {0} as server? {1}\n", stream.IsAuthenticated, stream.IsServer);
            sb.AppendFormat("IsSigned: {0}\n", stream.IsSigned);
            sb.AppendFormat("Is Encrypted: {0}\n", stream.IsEncrypted);
            sb.AppendFormat("Can read: {0}, write {1}\n", stream.CanRead, stream.CanWrite);
            sb.AppendFormat("Can timeout: {0}\n", stream.CanTimeout);
            sb.AppendFormat("Certificate revocation list checked: {0}\n", stream.CheckCertRevocationStatus);

            X509Certificate localCert = stream.LocalCertificate;
            if (localCert != null)
                sb.AppendFormat("Local certificate was issued to {0} : {1}.\n",
                                localCert.Subject, localCert.ToString());
            else
                sb.AppendFormat("Local certificate is null.\n");

            // Display the properties of the client's certificate.
            X509Certificate remoteCert = stream.RemoteCertificate;
            if (remoteCert != null)
                sb.AppendFormat("Remote certificate was issued to {0} : {1}",
                                remoteCert.Subject, remoteCert.ToString());
            else
                sb.AppendFormat("Remote certificate is null.\n");

            _log.Info(sb.ToString());
        }

        public void EnqueueSend(IEnumerable<ArraySegment<byte>> data)
        {
            lock (_streamLock)
            {
                int bytes = 0;
                foreach (var segment in data)
                {
                    _sendQueue.Enqueue(segment);
                    bytes += segment.Count;
                }
                NotifySendScheduled(bytes);
            }
            TrySend();
        }

        private async void TrySend()
        {
            lock (_streamLock)
            {
                if (_isSending || _sendQueue.Count == 0 || _sslStream == null || !_isAuthenticated) return;
                if (TcpConnectionMonitor.Default.IsSendBlocked()) return;
                _isSending = true;
            }

            _memoryStream.SetLength(0);

            ArraySegment<byte> sendPiece;
            while (_sendQueue.TryDequeue(out sendPiece))
            {
                _memoryStream.Write(sendPiece.Array, sendPiece.Offset, sendPiece.Count);
                if (_memoryStream.Length >= TcpConnection.MaxSendPacketSize)
                    break;
            }
            _sendingBytes = (int) _memoryStream.Length;

            try
            {
                NotifySendStarting(_sendingBytes);
                ArraySegment<byte> buffer;

                if (_memoryStream.TryGetBuffer(out buffer))
                {
                    await _sslStream.WriteAsync(buffer.Array, 0, _sendingBytes);
                    OnEndWrite();
                }
            }
            catch (SocketException exc)
            {
                _log.Debug(exc, "SocketException '{0}' during BeginWrite.", exc.SocketErrorCode);
                CloseInternal(exc.SocketErrorCode, "SocketException during BeginWrite.");
            }
            catch (ObjectDisposedException)
            {
                CloseInternal(SocketError.SocketError, "SslStream disposed.");
            }
            catch (Exception exc)
            {
                _log.Debug(exc, "Exception during BeginWrite.");
                CloseInternal(SocketError.SocketError, "Exception during BeginWrite");
            }
        }

        private void OnEndWrite()
        {
            try
            {
                NotifySendCompleted(_sendingBytes);

                lock (_streamLock)
                {
                    _isSending = false;
                }
                TrySend();
            }
            catch (SocketException exc)
            {
                _log.Debug(exc, "SocketException '{0}' during EndWrite.", exc.SocketErrorCode);
                NotifySendCompleted(0);
                CloseInternal(exc.SocketErrorCode, "SocketException during EndWrite.");
            }
            catch (ObjectDisposedException)
            {
                NotifySendCompleted(0);
                CloseInternal(SocketError.SocketError, "SslStream disposed.");
            }
            catch (Exception exc)
            {
                _log.Debug(exc, "Exception during EndWrite.");
                NotifySendCompleted(0);
                CloseInternal(SocketError.SocketError, "Exception during EndWrite.");
            }
        }

        public void ReceiveAsync(Action<ITcpConnection, IEnumerable<ArraySegment<byte>>> callback)
        {
            Ensure.NotNull(callback, "callback");

            if (Interlocked.Exchange(ref _receiveCallback, callback) != null)
            {
                _log.Error("ReceiveAsync called again while previous call was not fulfilled");
                throw new InvalidOperationException("ReceiveAsync called again while previous call was not fulfilled");
            }
            TryDequeueReceivedData();
        }

        private async void StartReceive()
        {
            try
            {
                NotifyReceiveStarting();
                var readLength = await _sslStream.ReadAsync(_receiveBuffer, 0, _receiveBuffer.Length);
                OnEndRead(readLength);
            }
            catch (SocketException exc)
            {
                _log.Debug(exc, "SocketException '{0}' during BeginRead.", exc.SocketErrorCode);
                CloseInternal(exc.SocketErrorCode, "SocketException during BeginRead.");
            }
            catch (ObjectDisposedException)
            {
                CloseInternal(SocketError.SocketError, "SslStream disposed.");
            }
            catch (Exception exc)
            {
                _log.Debug(exc, "Exception during BeginRead.");
                CloseInternal(SocketError.SocketError, "Exception during BeginRead.");
            }
        }

        private void OnEndRead(int readLength)
        {
            var bytesRead = readLength;

            if (bytesRead <= 0) // socket closed normally
            {
                NotifyReceiveCompleted(0);
                CloseInternal(SocketError.Success, "Socket closed.");
                return;
            }

            NotifyReceiveCompleted(bytesRead);

            var buffer = new ArraySegment<byte>(new byte[bytesRead]);
            Buffer.BlockCopy(_receiveBuffer, 0, buffer.Array, buffer.Offset, bytesRead);
            _receiveQueue.Enqueue(buffer);

            StartReceive();
            TryDequeueReceivedData();
        }

        private void TryDequeueReceivedData()
        {
            if (Interlocked.CompareExchange(ref _receiveHandling, 1, 0) != 0)
                return;
            do
            {
                if (_receiveQueue.Count > 0 && _receiveCallback != null)
                {
                    var callback = Interlocked.Exchange(ref _receiveCallback, null);
                    if (callback == null)
                    {
                        _log.Error("Threading issue in TryDequeueReceivedData. Callback is null.");
                        throw new Exception("Threading issue in TryDequeueReceivedData. Callback is null.");
                    }

                    var res = new List<ArraySegment<byte>>(_receiveQueue.Count);
                    ArraySegment<byte> piece;
                    while (_receiveQueue.TryDequeue(out piece))
                    {
                        res.Add(piece);
                    }

                    callback(this, res);

                    int bytes = 0;
                    for (int i = 0, n = res.Count; i < n; ++i)
                    {
                        bytes += res[i].Count;
                    }
                    NotifyReceiveDispatched(bytes);
                }
                Interlocked.Exchange(ref _receiveHandling, 0);
            } while (_receiveQueue.Count > 0
                     && _receiveCallback != null
                     && Interlocked.CompareExchange(ref _receiveHandling, 1, 0) == 0);
        }

        public void Close(string reason)
        {
            CloseInternal(SocketError.Success, reason ?? "Normal socket close."); // normal socket closing
        }

        private void CloseInternal(SocketError socketError, string reason)
        {
            if (Interlocked.CompareExchange(ref _isClosed, 1, 0) != 0)
                return;

            NotifyClosed();

            _log.Info("ClientAPI {12} closed [{0:HH:mm:ss.fff}: S{1}, L{2}, {3:B}]:\nReceived bytes: {4}, Sent bytes: {5}\n"
                      + "Send calls: {6}, callbacks: {7}\nReceive calls: {8}, callbacks: {9}\nClose reason: [{10}] {11}\n",
                      DateTime.UtcNow, RemoteEndPoint, LocalEndPoint, _connectionId,
                      TotalBytesReceived, TotalBytesSent,
                      SendCalls, SendCallbacks,
                      ReceiveCalls, ReceiveCallbacks,
                      socketError, reason, GetType().Name);

            if (_sslStream != null)
                Helper.EatException(() => _sslStream.Dispose());

            _onConnectionClosed?.Invoke(this, socketError);
        }

        public override string ToString()
        {
            return "S" + RemoteEndPoint;
        }
    }
}
