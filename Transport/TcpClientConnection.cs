using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace YuJanggi.Server.V2.Transport
{
    using Protocol.V2.Framing;
    using Protocol.V2.Messages;
    using Protocol.V2.Serialization;
    /// <summary>
    /// TCP 클라이언트 한 명과의 연결 및 메시지 송수신을 담당합니다.
    /// </summary>
    internal class TcpClientConnection : IDisposable
    {
        private readonly TcpClient      _client; // 연결 관리
        private readonly NetworkStream  _stream; // 실제 byte 송수신
        private readonly SemaphoreSlim  _sendLock;

        private bool _disposed;

        public string ConnectionInfo { get; }
        public TcpClientConnection(TcpClient client)
        {
            _client = client 
                ?? throw new ArgumentNullException(nameof(client));

            _stream = client.GetStream();

            ConnectionInfo =
                client.Client.RemoteEndPoint?.ToString()
                ?? "Unknown";

            _sendLock = new SemaphoreSlim(1, 1);
        }
        /// <summary>
        /// 클라이언트 연결을 종료합니다.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _sendLock.Dispose();
            _stream.Dispose();
            _client.Dispose();
        }

        /// <summary>
        /// 서버 메시지를 클라이언트에 전송합니다.
        /// </summary>
        public async Task SendAsync(
            ServerMessage message,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            byte[] body =
                MessageSerializer.Serialize(message);

            byte[] packet =
                MessageFramer.Encode(body);

            await _sendLock.WaitAsync(cancellationToken);

            try
            {
                await _stream.WriteAsync(
                    packet,
                    cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 클라이언트 메시지를 수신합니다.
        /// </summary>
        public async Task<ClientMessage> ReceiveAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            byte[] header =
                new byte[MessageFramer.HeaderSize];

            await _stream.ReadExactlyAsync(
                header,
                cancellationToken);

            int bodyLength =
                MessageFramer.DecodeBodyLength(header);

            byte[] body =
                new byte[bodyLength];

            await _stream.ReadExactlyAsync(
                body,
                cancellationToken);

            return MessageSerializer.Deserialize<ClientMessage>(body);
        }
        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(TcpClientConnection));
        }
    }
}
