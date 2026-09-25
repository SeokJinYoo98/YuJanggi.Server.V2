using YuJanggi.Protocol.V2.Matching;
using YuJanggi.Protocol.V2.Messages;
using YuJanggi.Protocol.V2.Messages.MessageFactory;
using YuJanggi.Server.V2.ClientSession;
using YuJanggi.Core.Domain;
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

        public MatchingHandler(
            MatchMakingService matchMakingService)
        {
            _matchMakingService     = matchMakingService;
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
                ClientMessageType.FormationSubmit => HandleFormationSubmitAsync(session, message, cancellationToken),
                _ => throw new InvalidOperationException($"매칭·포진 메시지가 아닙니다: {message.Type}")
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
                try
                {
                    bool[] responsesSent = await Task.WhenAll(firstResponse!, secondResponse!)
                        .WaitAsync(cancellationToken);
                    if (responsesSent.All(sent => sent))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var confirmed = _matchMakingService.ConfirmMatch(matchPair);
                        if (confirmed is not null)
                            await SendMatchingFoundAsync(confirmed.MatchId, confirmed.Players, cancellationToken);
                    }
                }
                finally
                {
                    // 전송 대기 취소/실패 시 미확정 예약만 해제합니다. 확정 매치는 유지합니다.
                    _matchMakingService.RejectPendingPair(matchPair);
                }
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
                    _matchMakingService.RejectPendingMatch(session);
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

        private Task HandleFormationSubmitAsync(
            IClientSession session, ClientMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = message.GetPayload<FormationSubmit>();
            Formation? formation = payload.Formation switch
            {
                MatchingFormation.HEHE => Formation.HEHE,
                MatchingFormation.EHEH => Formation.EHEH,
                MatchingFormation.EHHE => Formation.EHHE,
                MatchingFormation.HEEH => Formation.HEEH,
                _ => null
            };
            // 필드 누락을 enum 기본값(HEHE) 제출로 처리하지 않습니다.
            if (!message.Payload!.Value.TryGetProperty(nameof(FormationSubmit.Formation), out _))
                formation = null;

            var submission = formation.HasValue
                ? _matchMakingService.SubmitFormation(session, formation.Value)
                : new FormationSubmission(FormationSubmitResult.InvalidFormation);

            // TODO:
            // 마지막 포진 접수로 룸을 생성해도 현재는 제출자에게 접수 응답만 보냅니다.
            // 상대는 룸 생성 완료를 별도 이벤트로 알 수 없으며 게임은 시작되지 않습니다.
            // 게임 준비/시작 프로토콜에서 양쪽 초기 설정과 준비 요청 경로를 연결해야 합니다.
            return SendResponseAsync(session, ServerMessageType.FormationSubmitResponse,
                message.RequestId!, new FormationSubmitResponse
                {
                    Result = submission.Result,
                    RoomCreated = submission.RoomCreated
                }, cancellationToken);
        }

        private static async Task SendMatchingFoundAsync(
            string matchId, MatchPair matchPair, CancellationToken cancellationToken)
        {
            // TODO:
            // 첫 번째 MatchingFound 전송 후 두 번째가 실패하면 한쪽만 매칭 확정을 알게 됩니다.
            // 현재 요청 처리 루프의 연결 종료 정리로 매치는 해제되지만 상대 화면은 남을 수 있습니다.
            // 매칭 실패/종료 이벤트 구현 시 상대 통지와 복구 정책을 연결해야 합니다.
            var firstDto = new MatchingFound
            {
                MatchId = matchId,
                MyTeam = ProtocolPlayerTeam.Cho,
                Opponent = CreateMatchingPlayer(matchPair.Second, ProtocolPlayerTeam.Han),
            };
            var firstMsg
                = ServerMessageFactory.CreateEvent(
                    ServerMessageType.MatchingFound,
                    firstDto);
            await matchPair.First.Connection.SendAsync(
                firstMsg,
                cancellationToken);

            var secondDto = new MatchingFound
            {
                MatchId = matchId,
                MyTeam = ProtocolPlayerTeam.Han,
                Opponent = CreateMatchingPlayer(matchPair.First, ProtocolPlayerTeam.Cho),
            };
            var secondMsg
                = ServerMessageFactory.CreateEvent(
                    ServerMessageType.MatchingFound,
                    secondDto);

            await matchPair.Second.Connection.SendAsync(
                secondMsg,
                cancellationToken);

            NetworkView.ShowMatchingFound(firstDto);
            NetworkView.ShowMatchingFound(secondDto);
        }

        private static MatchingPlayer CreateMatchingPlayer(IClientSession session, ProtocolPlayerTeam team)
        {
            return new MatchingPlayer
            {
                PlayerId = session.ClientId.ToString(),
                PlayerNickname = session.Nickname!,
                PlayerTeam = team
            };
        }
    }
}
