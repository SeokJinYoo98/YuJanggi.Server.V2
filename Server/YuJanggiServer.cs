using System;
using System.Net;

namespace YuJanggi.Server.V2.Server
{
    using Handlers;
    using Transport;
    using View;
    using YuJanggi.Protocol.V2.Messages;
    using YuJanggi.Server.V2.ClientSession;
    using YuJanggi.Server.V2.Matching;
    using YuJanggi.Server.V2.GameRoom;

    /// <summary>
    /// 유장기 서버의 실행 및 클라이언트 연결 수락을 관리합니다.
    /// </summary>
    internal sealed class YuJanggiServer
    {
        #region Constants

        private const int Port = 7777;
        private static readonly IPAddress Address = IPAddress.Any;

        #endregion

        #region Fields
        private readonly TcpConnectionListener  _listener;
        private readonly ClientSessionManager   _sessionManager;
        private readonly MatchMakingService _matchMakingService;
        private readonly Lock _roomSync = new();

        private readonly Dictionary<ClientMessageType, IMessageHandler> _handlers;

        #endregion

        #region Constructors

        public YuJanggiServer()
        {
            _listener =
                new TcpConnectionListener(
                    new IPEndPoint(Address, Port));

            _sessionManager =
                new ClientSessionManager();

            var handshakeHandler = new ProtocolHandshakeHandler();

            var gameRoomManager = new GameRoomManager(_sessionManager, _roomSync);
            _matchMakingService = new MatchMakingService(gameRoomManager);
            var matchingHandler = new MatchingHandler(_matchMakingService);

            _handlers =
                new Dictionary<ClientMessageType, IMessageHandler>
                {
                    {
                        ClientMessageType.HandshakeRequest,
                        handshakeHandler
                    },
                    {
                        ClientMessageType.MatchingRequest,
                        matchingHandler
                    },
                    {
                        ClientMessageType.MatchingCancelRequest,
                        matchingHandler
                    },
                    {
                        ClientMessageType.FormationSubmit,
                        matchingHandler
                    }
                };
        }

        #endregion

        #region Public Methods

        public async Task RunAsync(
            CancellationToken cancellationToken = default)
        {
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationToken = shutdown.Token;
            _listener.Start();

            NetworkView.Write(
                NetworkMessageType.Message,
                "YuJanggi Server started.");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClientConnection connection =
                        await _listener.AcceptAsync(
                            cancellationToken);

                    var session =
                        ClientSessionFactory.CreateClientSession(
                            connection);

                    if (!_sessionManager.Add(session))
                    {
                        session.Dispose();
                        continue;
                    }

                    NetworkView.Write(
                        NetworkMessageType.Message,
                        $"Client connected: {connection.ConnectionInfo}",
                        session.Nickname);

                    Task processingTask =
                        HandleClientAsync(
                            session,
                            cancellationToken);

                    session.AttachProcessingTask(
                        processingTask);
                }
            }
            finally
            {
                // 수락 루프가 오류로 끝나도 송수신 대기를 먼저 취소한 뒤 세션을 정리합니다.
                shutdown.Cancel();
                _listener.Stop();

                Task[] processingTasks = _sessionManager.GetProcessingTasks();
                _sessionManager.Clear();
                try
                {
                    await Task.WhenAll(processingTasks);
                }
                finally
                {
                    await _matchMakingService.ClearAsync();
                }
            }
        }

        #endregion

        #region Private Methods

        private async Task HandleClientAsync(
            IClientSession session,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ClientMessage message =
                        await session.ReceiveAsync(
                            cancellationToken);

                    if (!_handlers.TryGetValue(
                        message.Type,
                        out IMessageHandler? handler))
                    {
                        throw new InvalidOperationException(
                            $"처리할 수 없는 메시지입니다: {message.Type}");
                    }

                    await handler.HandleAsync(
                        session,
                        message,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                // 서버 종료
            }
            catch (EndOfStreamException)
            {
                // 클라이언트 연결 종료
            }
            catch (Exception exception)
            {
                NetworkView.Write(
                    NetworkMessageType.Error,
                    exception.ToString(),
                    session.Nickname);
            }
            finally
            {
                await DisconnectClient(session);
            }
        }

        private async Task DisconnectClient(
            IClientSession session)
        {
            // 서버 종료 시 세션 목록이 먼저 비워졌더라도 대기열은 반드시 정리합니다.

            bool removed;
            lock (_roomSync)
            {
                // 매니저의 룸 생성과 세션 제거를 같은 잠금으로 보호합니다.
                removed = _sessionManager.Remove(session.ClientId, out _);
            }

            // 서비스 → 룸 매니저 순서로 잠급니다. 서버 잠금을 보유한 채 서비스를 호출하지 않습니다.
            Task roomCleanup = _matchMakingService.DisconnectPlayerAsync(session);

            if (!removed)
            {
                await roomCleanup;
                return;
            }

            session.Dispose();

            await roomCleanup;

            NetworkView.Write(
                NetworkMessageType.Message,
                $"Client disconnected: {session.ConnectionInfo}",
                session.Nickname);
        }

        #endregion
    }
}
