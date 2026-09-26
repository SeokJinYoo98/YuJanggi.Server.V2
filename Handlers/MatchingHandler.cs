using YuJanggi.Protocol.V2.Matching;
using YuJanggi.Protocol.V2.Messages;
using YuJanggi.Protocol.V2.Messages.MessageFactory;
using YuJanggi.Server.V2.ClientSession;
using YuJanggi.Core.V2.Domain;
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
            // 매칭 실패 통지는 Handler에서, 재대기 정책은 MatchMakingService에서 처리해야 합니다.
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
            return session.SendAsync(response, cancellationToken);
        }

        private async Task HandleFormationSubmitAsync(
            IClientSession session, ClientMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = message.GetPayload<FormationSubmitRequest>();
            Formation? formation = payload.Formation switch
            {
                ProtocolFormation.HEHE => Formation.HEHE,
                ProtocolFormation.EHEH => Formation.EHEH,
                ProtocolFormation.EHHE => Formation.EHHE,
                ProtocolFormation.HEEH => Formation.HEEH,
                _ => null
            };
            // 필드 누락을 enum 기본값(HEHE) 제출로 처리하지 않습니다.
            if (!message.Payload!.Value.TryGetProperty(nameof(FormationSubmitRequest.Formation), out _))
                formation = null;

            var submission = formation.HasValue
                ? _matchMakingService.SubmitFormation(session, formation.Value)
                : new FormationSubmission(FormationSubmissionStatus.InvalidFormation);

            // TODO:
            // 룸 생성 후 접수 응답 전송이 실패하거나 취소되면 GameReady는 전송하지 않습니다.
            // 현재 예외는 요청 처리 루프로 전달되며 상대는 준비 완료를 기다릴 수 있습니다.
            // 준비 실패 통지와 재접속 시 상태 복원 정책은 Protocol 확장 시 연결해야 합니다.
            await SendResponseAsync(session, ServerMessageType.FormationSubmitResponse,
                message.RequestId!, new FormationSubmitResponse
                {
                    MatchId = submission.MatchId ?? string.Empty,
                    Result = ToProtocolResult(submission.Result),
                    RoomCreated = submission.RoomCreated
                }, cancellationToken);

            if (submission.IsReady)
                await SendGameReadyAsync(submission, cancellationToken);
        }

        private static FormationSubmitResult ToProtocolResult(FormationSubmissionStatus result) => result switch
        {
            FormationSubmissionStatus.Accepted => FormationSubmitResult.Accepted,
            FormationSubmissionStatus.AlreadySubmitted => FormationSubmitResult.AlreadySubmitted,
            FormationSubmissionStatus.NotMatched => FormationSubmitResult.NotMatched,
            FormationSubmissionStatus.InvalidFormation => FormationSubmitResult.InvalidFormation,
            FormationSubmissionStatus.HandshakeRequired => FormationSubmitResult.HandshakeRequired,
            FormationSubmissionStatus.ServerError => FormationSubmitResult.ServerError,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };

        private static ProtocolFormation ToProtocolFormation(Formation formation) => formation switch
        {
            Formation.HEHE => ProtocolFormation.HEHE,
            Formation.EHEH => ProtocolFormation.EHEH,
            Formation.EHHE => ProtocolFormation.EHHE,
            Formation.HEEH => ProtocolFormation.HEEH,
            _ => throw new ArgumentOutOfRangeException(nameof(formation))
        };

        private static async Task SendGameReadyAsync(
            FormationSubmission submission, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = new GameReadyEvent
            {
                MatchId = submission.MatchId!,
                ChoFormation = ToProtocolFormation(submission.ChoFormation!.Value),
                HanFormation = ToProtocolFormation(submission.HanFormation!.Value)
            };
            var message = ServerMessageFactory.CreateEvent(ServerMessageType.GameReady, ready);
            var players = submission.Players!;

            // TODO:
            // 룸 생성 직후 연결이 끊기거나 첫 전송 성공 후 두 번째 전송이 실패할 수 있습니다.
            // 현재 순차 전송이므로 한쪽만 GameReady를 받고, 연결 종료 정리로 룸이 제거될 수 있습니다.
            // 준비 수신 확인, 실패 이벤트와 재전송·복구 정책은 Protocol 확장 시 결정해야 합니다.
            await players.First.SendAsync(message, cancellationToken);
            await players.Second.SendAsync(message, cancellationToken);
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
            await matchPair.First.SendAsync(
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

            await matchPair.Second.SendAsync(
                secondMsg,
                cancellationToken);
        }

        private static MatchingPlayerEvent CreateMatchingPlayer(IClientSession session, ProtocolPlayerTeam team)
        {
            return new MatchingPlayerEvent
            {
                PlayerId = session.ClientId.ToString(),
                PlayerNickname = session.Nickname!,
                PlayerTeam = team
            };
        }
    }
}
