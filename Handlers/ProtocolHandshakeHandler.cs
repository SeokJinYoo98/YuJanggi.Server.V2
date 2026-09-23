using System.Threading;
using System.Threading.Tasks;

namespace YuJanggi.Server.V2.Handlers
{
    using Core;

    using Protocol.V2.Connection;
    using Protocol.V2.Messages;
    using Protocol.V2.Messages.MessageFactory;

    using Transport;
    using View;
    using YuJanggi.Server.V2.ClientSession;

    /// <summary>
    /// 클라이언트의 핸드셰이크 요청을 처리합니다.
    /// </summary>
    internal sealed class ProtocolHandshakeHandler : IMessageHandler
    {
        public async Task HandleAsync(
            IClientSession session,
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

            NetworkView.ShowHandShakeResult(
                session.Nickname,
                request,
                result);

            var response = new ProtocolHandshakeResponse
            {
                Result = result
            };

            ServerMessage responseMessage =
                ServerMessageFactory.CreateResponse(
                    ServerMessageType.ProtocolHandshake,
                    message.RequestId,
                    response);

            await session.Connection.SendAsync(
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
