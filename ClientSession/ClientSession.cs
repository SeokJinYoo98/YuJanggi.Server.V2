namespace YuJanggi.Server.V2.ClientSession
{
    using Transport;
    using YuJanggi.Protocol.V2.Messages;
    using YuJanggi.Server.V2.View;

    /// <summary>
    /// 서버에 연결된 단일 클라이언트의 세션 정보를 관리합니다.
    /// 클라이언트 연결 객체와 해당 클라이언트를 처리하는 작업을 함께 보관합니다.
    /// </summary>
    internal interface IClientSession : IDisposable
    {
        Task?   ProcessingTask { get; }
        Guid    ClientId { get; }
        string  ConnectionInfo { get; }
        string? Nickname { get; }
        bool    IsHandshakeCompleted { get; }
        // public TcpClientConnection Connection { get; }
        void AttachProcessingTask(Task processingTask);
        void CompleteHandshake();
        Task SendAsync(
             ServerMessage message,
             CancellationToken cancellationToken);
        Task<ClientMessage> ReceiveAsync(
            CancellationToken cancellationToken);
    }
    internal sealed class ClientSession : IClientSession
    {
        #region Field

        #endregion

        #region Properties
        public TcpClientConnection Connection { get; }
        public Guid ClientId =>
            Connection.ClientId;
        public string ConnectionInfo =>
            Connection.ConnectionInfo;
     
        public Task? ProcessingTask { get; private set; }
        public string? Nickname { get; private set; }
        public bool IsHandshakeCompleted { get; private set; }

        #endregion

        #region Constructors

        public ClientSession(
            TcpClientConnection connection,
            string? nickname)
        {
            Connection = connection;
            Nickname = nickname;
        }

        #endregion

        #region Public Methods

        public void AttachProcessingTask(
            Task processingTask)
        {
            if (ProcessingTask is not null)
            {
                throw new InvalidOperationException(
                    "ProcessingTask가 이미 설정되어 있습니다.");
            }

            ProcessingTask = processingTask;
        }

        public void CompleteHandshake()
        {
            IsHandshakeCompleted = true;
        }

        public void Dispose()
        {
            Connection.Dispose();
        }
        public async Task SendAsync(
         ServerMessage message,
         CancellationToken cancellationToken)
        {
            await Connection.SendAsync(
                message,
                cancellationToken);

            NetworkView.ShowSendMessage(
                Nickname,
                message);
        }

        public async Task<ClientMessage> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            ClientMessage message =
                    await Connection.ReceiveAsync(
                        cancellationToken);

            NetworkView.ShowReceiveMessage(
                Nickname,
                message);

            return message;
        }
    }
        #endregion
}

