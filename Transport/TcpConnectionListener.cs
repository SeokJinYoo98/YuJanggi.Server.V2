using System;
using System.Net;
using System.Net.Sockets;

namespace YuJanggi.Server.V2.Transport
{
    /// <summary>
    /// TCP 서버 포트를 열고 클라이언트 연결 요청을 수락합니다.
    /// </summary>
    internal class TcpConnectionListener
    {
        private readonly TcpListener _listener;
        public TcpConnectionListener(IPEndPoint ipEndPoint)
        {
            _listener = new(ipEndPoint);
        }

        /// <summary>
        /// TCP 연결 수신을 시작합니다.
        /// </summary>
        public void Start()
        {
            _listener.Start();
        }

        /// <summary>
        /// 클라이언트 연결을 비동기로 대기하고 수락합니다.
        /// </summary>
        public async Task<TcpClientConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
        {
            TcpClient client =
                await _listener.AcceptTcpClientAsync(
                    cancellationToken);

            return new TcpClientConnection(client);
        }

        /// <summary>
        /// TCP 연결 수신을 중지합니다.
        /// </summary>
        public void Stop()
        {
            _listener.Stop();
        }
    }
}
