using YuJanggi.Protocol.V2.Matching;
using YuJanggi.Protocol.V2.Messages;
using YuJanggi.Protocol.V2.Messages.MessageFactory;
using YuJanggi.Server.V2.ClientSession;
using YuJanggi.Server.V2.Matching;
using YuJanggi.Server.V2.View;

namespace YuJanggi.Server.V2.Handlers
{
    /// <summary>매칭 요청을 해석하고 응답 및 매칭 이벤트를 전송합니다.</summary>
    internal sealed class MatchingHandler : IMessageHandler
    {
        private readonly MatchMakingService _matchMakingService;
        private readonly Lock _responseSync = new();
        private readonly Dictionary<Guid, TaskCompletionSource<bool>> _pendingResponses = new();

        public MatchingHandler(MatchMakingService matchMakingService)
        {
            _matchMakingService = matchMakingService;
        }

        public Task HandleAsync(
            IClientSession session, ClientMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMessage(message);
            NetworkView.Write(
                NetworkMessageType.Debug,
                $"Receive: {message.Type} / RequestId: {message.RequestId}",
                session.Nickname);

            return message.Type switch
            {
                ClientMessageType.MatchingRequest => HandleRequestAsync(session, message.RequestId!, cancellationToken),
                ClientMessageType.MatchingCancelRequest => HandleCancelAsync(session, message.RequestId!, cancellationToken),
                _ => throw new InvalidOperationException($"매칭 신청·취소 메시지가 아닙니다: {message.Type}")
            };
        }

        private static void ValidateMessage(ClientMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.RequestId))
                throw new InvalidOperationException("매칭 요청에 RequestId가 없습니다.");
        }

        private async Task HandleRequestAsync(
            IClientSession session, string requestId, CancellationToken cancellationToken)
        {
            MatchingResult result;
            MatchPair? matchPair;
            TaskCompletionSource<bool>? responseCompletion = null;
            Task<bool>? firstResponse = null;
            Task<bool>? secondResponse = null;

            // 큐 등록과 응답 완료 신호 등록 사이에 다른 요청이 쌍을 가져가지 못하게 합니다.
            // 이 lock과 서비스 lock 안에서는 네트워크 호출이나 await를 하지 않습니다.
            lock (_responseSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                matchPair = null;
                result = _pendingResponses.ContainsKey(session.ClientId)
                    ? MatchingResult.AlreadyMatching
                    : _matchMakingService.RequestMatch(session, out matchPair);
                if (result == MatchingResult.Accepted)
                {
                    responseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingResponses.Add(session.ClientId, responseCompletion);
                }
                if (matchPair is not null)
                {
                    firstResponse = GetResponseCompletion(matchPair.First);
                    secondResponse = GetResponseCompletion(matchPair.Second);
                }
            }

            bool responseSent = false;
            try
            {
                await SendResponseAsync(session, ServerMessageType.MatchingResponse, requestId,
                    new MatchingResponse { Result = result }, cancellationToken);
                responseSent = true;
            }
            finally
            {
                if (responseCompletion is not null)
                    CompleteResponse(session, responseCompletion, responseSent);
            }

            if (matchPair is not null)
            {
                // 다른 클라이언트의 Accepted 응답까지 성공한 뒤 이벤트를 전송합니다.
                bool[] responsesSent = await Task.WhenAll(firstResponse!, secondResponse!)
                    .WaitAsync(cancellationToken);
                if (responsesSent.All(sent => sent))
                    await SendMatchingFoundAsync(matchPair, cancellationToken);
            }
        }

        // _responseSync 안에서만 읽습니다. 항목이 없으면 응답 전송이 이미 완료된 것입니다.
        private Task<bool> GetResponseCompletion(IClientSession session)
        {
            return _pendingResponses.TryGetValue(session.ClientId, out var completion)
                ? completion.Task
                : Task.FromResult(true);
        }

        private void CompleteResponse(
            IClientSession session, TaskCompletionSource<bool> completion, bool responseSent)
        {
            lock (_responseSync)
            {
                if (!responseSent)
                    _matchMakingService.CancelMatch(session);
                completion.SetResult(responseSent);
                _pendingResponses.Remove(session.ClientId);
            }
            // TODO:
            // 응답 실패 또는 토큰 취소 전에 쌍이 생성되었다면 상대도 이미 큐에서 빠진 상태입니다.
            // 현재는 MatchingFound를 보내지 않지만 상대는 Accepted 이후 계속 기다릴 수 있습니다.
            // GameRoom / MatchSession에서 매칭 확정과 실패 통지 또는 재대기 정책을 구현해야 합니다.
        }

        private Task HandleCancelAsync(
            IClientSession session, string requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _matchMakingService.CancelMatch(session);
            return SendResponseAsync(session, ServerMessageType.MatchingCancelResponse, requestId,
                new MatchingCancelResponse { Result = result }, cancellationToken);
        }

        private static Task SendResponseAsync<TPayload>(
            IClientSession session, ServerMessageType type, string requestId,
            TPayload payload, CancellationToken cancellationToken)
        {
            var response = ServerMessageFactory.CreateResponse(type, requestId, payload);
            return session.Connection.SendAsync(response, cancellationToken);
        }

        private static async Task SendMatchingFoundAsync(
            MatchPair matchPair, CancellationToken cancellationToken)
        {
            var matchingFound = new MatchingFound
            {
                MatchId = Guid.NewGuid().ToString(),
                ChoPlayer = CreateMatchingPlayer(matchPair.First),
                HanPlayer = CreateMatchingPlayer(matchPair.Second)
            };
            var foundMessage = ServerMessageFactory.CreateEvent(ServerMessageType.MatchingFound, matchingFound);

            // TODO:
            // 쌍 생성 직후 상대가 연결을 종료하거나 전송 중 토큰이 취소되면 전송이 실패할 수 있습니다.
            // 현재는 두 클라이언트에게 MatchingFound를 순차 전송하므로 첫 전송 성공 후 두 번째가
            // 실패하면 양쪽 상태가 불일치하며, 예외는 요청 처리 루프로 전달되어 요청자 연결도 종료됩니다.
            // GameRoom / MatchSession에서 매칭 확정 상태, 실패 대상 구분 및 복구 정책을 추가해야 합니다.
            await matchPair.First.Connection.SendAsync(foundMessage, cancellationToken);
            await matchPair.Second.Connection.SendAsync(foundMessage, cancellationToken);
        }

        private static MatchingPlayer CreateMatchingPlayer(IClientSession session)
        {
            return new MatchingPlayer
            {
                PlayerId = session.ClientId.ToString(),
                PlayerName = session.Nickname!
            };
        }
    }
}
