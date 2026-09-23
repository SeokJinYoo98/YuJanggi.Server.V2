using System;
using System.Net;

namespace YuJanggi.Server.V2.Server
{
    using Handlers;
    using Transport;
    using View;
    using YuJanggi.Protocol.V2.Messages;
    using YuJanggi.Server.V2.ClientSession;

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

            _handlers =
                new Dictionary<ClientMessageType, IMessageHandler>
                {
                {
                    ClientMessageType.ProtocolHandshake,
                    new ProtocolHandshakeHandler()
                },
                {
                    ClientMessageType.MatchingRequest,
                    new MatchingHandler()
                }
                };
        }

        #endregion

        #region Public Methods

        public async Task RunAsync(
            CancellationToken cancellationToken = default)
        {
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
                _listener.Stop();

                _sessionManager.Clear();
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
            if (!_sessionManager.Remove(
                session.ClientId,
                out _))
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
