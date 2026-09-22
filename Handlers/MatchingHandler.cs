using System;
using System.Threading;
using System.Threading.Tasks;

namespace YuJanggi.Server.V2.Handlers
{
    using Protocol.V2.Messages;
    using Protocol.V2.Messages.MessageFactory;
    using Protocol.V2.Matching;

    using Transport;
    using View;

    /// <summary>
    /// 매칭 신청·취소 요청의 처리 진입점입니다.
    /// 대기열 관리와 응답 전송은 아직 구현하지 않습니다.
    /// </summary>
    internal sealed class MatchingHandler : IMessageHandler
    {
        public async Task HandleAsync(
            TcpClientConnection connection,
            ClientMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(message.RequestId))
            {
                throw new InvalidOperationException(
                    "매칭 요청에 RequestId가 없습니다.");
            }

            if (message.Type != ClientMessageType.MatchingRequest &&
                message.Type != ClientMessageType.MatchingCancelRequest)
            {
                throw new InvalidOperationException(
                    $"매칭 신청·취소 메시지가 아닙니다: {message.Type}");
            }
            switch (message.Type)
            {
                case ClientMessageType.MatchingRequest:
                    {
                        var response = new MatchingResponse
                        {
                            Result = MatchingResult.Accepted
                        };

                        var responseMessage =
                            ServerMessageFactory.CreateResponse(
                                ServerMessageType.MatchingResponse,
                                message.RequestId,
                                response);

                        await connection.SendAsync(
                            responseMessage,
                            cancellationToken);

                        NetworkView.Write(
                            NetworkMessageType.Message,
                           $"Client Matching Request: {connection.ConnectionInfo}", 
                           connection.ClientId);

                        break;
                    }

                case ClientMessageType.MatchingCancelRequest:
                    {
                        // 이후 취소 처리
                        break;
                    }
            }
        }
    }
}
