using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace YuJanggi.Server.V2.Server
{
    using System.Collections.Concurrent;
    using Handlers;
    using Transport;
    using View;
    using YuJanggi.Protocol.V2.Messages;

    /// <summary>
    /// 유장기 서버의 실행 및 클라이언트 연결 수락을 관리합니다.
    /// </summary>
    internal class YuJanggiServer
    {
        private const int Port = 7777;
        private static readonly IPAddress Address = IPAddress.Any;

        private readonly TcpConnectionListener _listener;

        private readonly ConcurrentDictionary<Guid, TcpClientConnection> _connections = new();
        private readonly ConcurrentDictionary<Guid, Task> _clientTasks = new();

        private readonly Dictionary<ClientMessageType, IMessageHandler> _handlers;
        public YuJanggiServer()
        {
            _listener    = new TcpConnectionListener(new IPEndPoint(Address, Port));

            _handlers = new Dictionary<ClientMessageType, IMessageHandler>
            {
                {
                    ClientMessageType.MatchingRequest,
                    new MatchingHandler()
                },
                {
                    ClientMessageType.ProtocolHandshake,
                    new ProtocolHandshakeHandler()
                }
            };
        }
        /// <summary>
        /// 서버를 시작하고 클라이언트 연결을 계속 수락합니다.
        /// </summary>
        public async Task RunAsync(
            CancellationToken cancellationToken = default)
        {
            _listener.Start();

            NetworkView.Write(NetworkMessageType.Message, "YuJanggi Server started.");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClientConnection connection =
                        await _listener.AcceptAsync(cancellationToken);
                    var guid = connection.ClientId;

                    _connections.TryAdd(guid, connection);
                    NetworkView.Write(NetworkMessageType.Message,
                        $"Client connected: {connection.ConnectionInfo}", guid);
                    var task = HandleClientAsync(connection, cancellationToken);
                    _clientTasks.TryAdd(guid, task);
                }
            }
            finally
            {
                _listener.Stop();
            }
        }

        private async Task HandleClientAsync(
            TcpClientConnection connection,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ClientMessage message =
                        await connection.ReceiveAsync(cancellationToken);

                    if (!_handlers.TryGetValue(
                        message.Type,
                        out var handler))
                    {
                        throw new InvalidOperationException(
                            $"처리할 수 없는 메시지입니다: {message.Type}");
                    }

                    await handler.HandleAsync(
                        connection,
                        message,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 서버 종료 등으로 취소됨
            }
            catch (EndOfStreamException)
            {
                // 클라이언트 연결 종료
            }
            catch (Exception exception)
            {
                NetworkView.Write(NetworkMessageType.Error, exception.ToString(), connection.ClientId);
            }
            finally
            {
                DisconnectClient(connection);
            }
        }
        private void DisconnectClient(
            TcpClientConnection connection)
        {
            var clientId = connection.ClientId;

            connection.Dispose();

            _connections.TryRemove(
                clientId,
                out _);

            _clientTasks.TryRemove(
                clientId,
                out _);

            NetworkView.Write(
                NetworkMessageType.Message,
                $"Client disconnected: {connection.ConnectionInfo}",
                clientId);
        }
    }
}
