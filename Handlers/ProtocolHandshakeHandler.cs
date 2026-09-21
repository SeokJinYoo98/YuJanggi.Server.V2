using System.Threading;
using System.Threading.Tasks;

namespace YuJanggi.Server.V2.Handlers
{
    using Core;

    using Transport;

    using Protocol.V2.Messages;
    using Protocol.V2.Messages.MessageFactory;
    using Protocol.V2.Connection;

    /// <summary>
    /// 클라이언트의 핸드셰이크 요청을 처리합니다.
    /// </summary>
    internal sealed class ProtocolHandshakeHandler
    {
        public async Task HandleAsync(
            TcpClientConnection connection,
            ClientMessage message,
            CancellationToken cancellationToken)
        {
            if (message.Type != ClientMessageType.ProtocolHandshake)
            {
                throw new InvalidOperationException(
                    $"핸드셰이크 메시지가 아닙니다: {message.Type}");
            }

            if (string.IsNullOrWhiteSpace(message.RequestId))
            {
                throw new InvalidOperationException(
                    "핸드셰이크 요청에 RequestId가 없습니다.");
            }

            ProtocolHandshakeRequest request =
                message.GetPayload<ProtocolHandshakeRequest>();

            ProtocolHandshakeResult result =
                ValidateVersion(request);

            var response = new ProtocolHandshakeResponse
            {
                Result = result
            };

            ServerMessage responseMessage =
                ServerMessageFactory.CreateResponse(
                    ServerMessageType.ProtocolHandshake,
                    message.RequestId,
                    response);

            await connection.SendAsync(
                responseMessage,
                cancellationToken);
        }

        private static ProtocolHandshakeResult ValidateVersion(
            ProtocolHandshakeRequest request)
        {
            ProtocolHandshakeResult result =
                ProtocolHandshakeResult.Success;

            if (request.YuJanggiProtocolVersion !=
                Protocol.V2.ProtocolVersion.Current)
            {
                result |=
                    ProtocolHandshakeResult.ProtocolVersionMismatch;
            }

            if (request.YuJanggiCoreVersion != CoreVersion.Current)
            {
                result |=
                    ProtocolHandshakeResult.CoreVersionMismatch;
            }

            return result;
        }
    }
}
