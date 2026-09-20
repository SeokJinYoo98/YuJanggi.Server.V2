using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace YuJanggi.Server.V2.Server
{
    using System.Collections.Concurrent;
    using Transport;
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
        public YuJanggiServer()
        {
            _listener    = new TcpConnectionListener(new IPEndPoint(Address, Port));
        }
        /// <summary>
        /// 서버를 시작하고 클라이언트 연결을 계속 수락합니다.
        /// </summary>
        public async Task RunAsync(
            CancellationToken cancellationToken = default)
        {
            _listener.Start();

            Console.WriteLine("YuJanggi Server started.");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var guid = Guid.NewGuid();
                    TcpClientConnection connection =
                        await _listener.AcceptAsync(cancellationToken);

                    _connections.TryAdd(guid, connection);
                    var task = HandleClientAsync(connection, cancellationToken);
                    _clientTasks.TryAdd(guid, task);

                    Console.WriteLine(
                        $"{guid}_Client connected: {connection.ConnectionInfo}");
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

                    // 나중에 메시지 처리
                }
            }
            catch (OperationCanceledException)
            {
                // 서버 종료 등으로 취소됨
            }
            catch (IOException)
            {
                // 클라이언트 연결 종료
            }
            finally
            {
                connection.Dispose();

                Console.WriteLine(
                    $"Client disconnected: {connection.ConnectionInfo}");
            }
        }
    }
}
