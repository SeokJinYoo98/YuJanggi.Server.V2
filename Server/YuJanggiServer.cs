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
        private readonly Dictionary<string, JanggiRoom> _gameRooms = new();

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

            _matchMakingService = new MatchMakingService();
            var matchingHandler = new MatchingHandler(_matchMakingService, CreateGameRoom);

            _handlers =
                new Dictionary<ClientMessageType, IMessageHandler>
                {
                    {
                        ClientMessageType.ProtocolHandshake,
                        handshakeHandler
                    },
                    {
                        ClientMessageType.MatchingRequest,
                        matchingHandler
                    },
                    {
                        ClientMessageType.MatchingCancelRequest,
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
                await Task.WhenAll(processingTasks);
                lock (_roomSync)
                    _gameRooms.Clear();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 매칭된 초·한 참가자로 장기 룸을 초기화하고 매칭 ID별로 보관합니다.
        /// 양쪽 Accepted 응답 전송 후, MatchingFound 이벤트 전송 전에 호출합니다.
        /// 준비 완료 처리와 실제 게임 시작은 수행하지 않습니다.
        /// </summary>
        private JanggiRoom CreateGameRoom(string matchId, MatchPair matchPair)
        {
            var room = new JanggiRoom();
            room.Initialize(matchId, matchPair);

            lock (_roomSync)
            {
                // 참가자 종료와 룸 등록을 같은 잠금으로 보호합니다.
                if (!_sessionManager.Contains(matchPair.First.ClientId) ||
                    !_sessionManager.Contains(matchPair.Second.ClientId))
                    throw new InvalidOperationException("연결이 종료된 참가자의 룸을 생성할 수 없습니다.");

                _gameRooms.Add(room.MatchId, room);
            }
            return room;
        }

        private async Task HandleClientAsync(
            IClientSession session,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ClientMessage message =
                        await session.Connection.ReceiveAsync(
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
                DisconnectClient(session);
            }
        }

        private void DisconnectClient(
            IClientSession session)
        {
            // 서버 종료 시 세션 목록이 먼저 비워졌더라도 대기열은 반드시 정리합니다.
            _matchMakingService.CancelMatch(session);

            bool removed;
            lock (_roomSync)
            {
                removed = _sessionManager.Remove(session.ClientId, out _);
                foreach (var entry in _gameRooms.ToArray())
                {
                    if (entry.Value.ChoPlayer?.ClientId == session.ClientId ||
                        entry.Value.HanPlayer?.ClientId == session.ClientId)
                        _gameRooms.Remove(entry.Key);
                }
            }

            // TODO:
            // 현재는 참가자가 연결을 종료하면 해당 룸 참조만 제거하며 상대에게 별도 종료 알림을 보내지 않습니다.
            // 상대가 MatchingFound를 받았다면 Matched 상태로 남을 수 있습니다.
            // 룸 생명주기 구현 시 상대 알림, 재접속 여부 및 Close의 자원 정리 정책을 연결해야 합니다.
            if (!removed)
            {
                return;
            }

            session.Dispose();

            NetworkView.Write(
                NetworkMessageType.Message,
                $"Client disconnected: {session.ConnectionInfo}",
                session.Nickname);
        }

        #endregion
    }
}
