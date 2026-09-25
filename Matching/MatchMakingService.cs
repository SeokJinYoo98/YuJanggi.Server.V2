using YuJanggi.Core.Domain;
using YuJanggi.Protocol.V2.Matching;
using YuJanggi.Server.V2.ClientSession;
using YuJanggi.Server.V2.GameRoom;

namespace YuJanggi.Server.V2.Matching
{
    internal sealed record MatchPair(IClientSession First, IClientSession Second);
    internal sealed record ConfirmedMatch(string MatchId, MatchPair Players);
    internal readonly record struct FormationSubmission(FormationSubmitResult Result, bool RoomCreated = false);

    /// <summary>매칭 예약·확정 및 포진 접수를 관리합니다. 메시지 생성이나 네트워크 전송은 하지 않습니다.</summary>
    internal sealed class MatchMakingService
    {
        private sealed class MatchState
        {
            public MatchState(MatchPair players) => Players = players;
            public MatchPair Players { get; }
            public string? MatchId { get; set; }
            public Formation? ChoFormation { get; set; }
            public Formation? HanFormation { get; set; }
            public bool RoomCreated { get; set; }
        }

        private readonly Lock _sync = new();
        private MatchMakingQueue _queue = new();
        private readonly Dictionary<Guid, MatchState> _playerMatches = new();
        private readonly GameRoomManager _gameRoomManager;

        public MatchMakingService(GameRoomManager gameRoomManager)
        {
            _gameRoomManager = gameRoomManager;
        }

        public MatchingResult RequestMatch(IClientSession session, out MatchPair? matchPair)
        {
            matchPair = null;
            lock (_sync)
            {
                if (!session.IsHandshakeCompleted)
                    return MatchingResult.HandshakeRequired;
                if (FindMatch(session.ClientId) is { } match)
                    return match.MatchId is null ? MatchingResult.AlreadyMatching : MatchingResult.AlreadyMatched;
                if (_queue.Contains(session))
                    return MatchingResult.AlreadyMatching;

                _queue.Enqueue(session);
                matchPair = TryCreateMatchPair();
                return MatchingResult.Accepted;
            }
        }

        public MatchingCancelResult CancelMatch(IClientSession session)
        {
            lock (_sync)
            {
                // 쌍을 확보한 이후에는 큐 취소로 상대 예약을 무효화하지 않습니다.
                if (FindMatch(session.ClientId) is not null)
                    return MatchingCancelResult.AlreadyMatched;
                _queue.Remove(session);
                return MatchingCancelResult.Cancelled;
            }
        }

        /// <summary>핸들러가 양쪽 Accepted 전송 성공을 확인한 뒤 호출합니다. 룸은 생성하지 않습니다.</summary>
        public ConfirmedMatch? ConfirmMatch(MatchPair pair)
        {
            lock (_sync)
            {
                if (!_playerMatches.TryGetValue(pair.First.ClientId, out var state) ||
                    !ReferenceEquals(state.Players, pair) || state.MatchId is not null ||
                    !_playerMatches.TryGetValue(pair.Second.ClientId, out var other) ||
                    !ReferenceEquals(state, other))
                    return null;

                state.MatchId = Guid.NewGuid().ToString();
                return new ConfirmedMatch(state.MatchId, pair);
            }
        }

        /// <summary>응답 전송 실패 시 큐 또는 아직 확정되지 않은 예약을 해제합니다.</summary>
        public void RejectPendingMatch(IClientSession session)
        {
            lock (_sync)
            {
                _queue.Remove(session);
                if (_playerMatches.TryGetValue(session.ClientId, out var state) && state.MatchId is null)
                    RemoveMatch(state);
            }
        }

        public void RejectPendingPair(MatchPair pair)
        {
            lock (_sync)
            {
                if (_playerMatches.TryGetValue(pair.First.ClientId, out var state) &&
                    ReferenceEquals(state.Players, pair) && state.MatchId is null)
                    RemoveMatch(state);
            }
        }

        /// <summary>현재 확정 매치의 참가자 포진을 한 번 접수하며, 양쪽 접수 시 한 번만 룸을 생성합니다.</summary>
        public FormationSubmission SubmitFormation(IClientSession session, Formation formation)
        {
            // TODO:
            // 현재 FormationSubmit에는 MatchId가 없어 세션의 현재 매치에 제출을 연결합니다.
            // 같은 연결에서 재매칭 후 이전 제출이 늦게 도착하면 새 매치의 포진으로 접수될 수 있습니다.
            // 재매칭/재전송 프로토콜 확장 시 MatchId를 포함해 이전 매치의 제출을 거절해야 합니다.
            lock (_sync)
            {
                if (!session.IsHandshakeCompleted)
                    return new(FormationSubmitResult.HandshakeRequired);
                if (!Enum.IsDefined(formation))
                    return new(FormationSubmitResult.InvalidFormation);

                var state = FindMatch(session.ClientId);
                if (state?.MatchId is null)
                    return new(FormationSubmitResult.NotMatched);

                bool isCho = state.Players.First.ClientId == session.ClientId;
                Formation? submitted = isCho ? state.ChoFormation : state.HanFormation;
                if (submitted.HasValue)
                    return new(FormationSubmitResult.AlreadySubmitted, state.RoomCreated);

                if (isCho)
                    state.ChoFormation = formation;
                else
                    state.HanFormation = formation;

                if (state.ChoFormation.HasValue && state.HanFormation.HasValue)
                {
                    try
                    {
                        _gameRoomManager.CreateGameRoom(state.MatchId,
                            state.Players.First, state.ChoFormation.Value,
                            state.Players.Second, state.HanFormation.Value);
                        state.RoomCreated = true;
                    }
                    catch
                    {
                        // 두 번째 제출은 확정하지 않아 룸 생성 실패 후 다시 시도할 수 있습니다.
                        if (isCho) state.ChoFormation = null;
                        else state.HanFormation = null;
                        throw;
                    }
                }
                return new(FormationSubmitResult.Accepted, state.RoomCreated);
            }
        }

        /// <summary>연결 종료 시 큐와 상대를 포함한 매칭 상태를 정리하고 룸 종료 작업을 반환합니다.</summary>
        public Task DisconnectPlayerAsync(IClientSession session)
        {
            // TODO:
            // 포진 접수 중 연결이 끊기면 양쪽 매칭 상태는 해제되지만 상대에게 종료 메시지는 없습니다.
            // 상대는 다음 제출 시 NotMatched를 받거나 매칭 화면에서 계속 기다릴 수 있습니다.
            // 매칭 실패 이벤트 프로토콜에서 상대 알림과 재매칭 정책을 연결해야 합니다.
            lock (_sync)
            {
                _queue.Remove(session);
                if (_playerMatches.TryGetValue(session.ClientId, out var state))
                    RemoveMatch(state);
                return _gameRoomManager.RemoveRoomsForPlayerAsync(session.ClientId);
            }
        }

        public Task ClearAsync()
        {
            lock (_sync)
            {
                _playerMatches.Clear();
                _queue = new MatchMakingQueue();
                return _gameRoomManager.ClearAsync();
            }
        }

        private MatchState? FindMatch(Guid clientId)
        {
            if (!_playerMatches.TryGetValue(clientId, out var state))
                return null;
            if (state.RoomCreated && !_gameRoomManager.TryGetRoom(state.MatchId!, out _))
            {
                RemoveMatch(state);
                return null;
            }
            return state;
        }

        private void RemoveMatch(MatchState state)
        {
            _playerMatches.Remove(state.Players.First.ClientId);
            _playerMatches.Remove(state.Players.Second.ClientId);
        }

        private MatchPair? TryCreateMatchPair()
        {
            if (_queue.Count < 2)
                return null;
            if (!_queue.TryDequeue(out var first, out var second))
                throw new InvalidOperationException("매칭 대기열의 개수와 인출 결과가 일치하지 않습니다.");

            var pair = new MatchPair(first!, second!);
            var state = new MatchState(pair);
            _playerMatches.Add(pair.First.ClientId, state);
            _playerMatches.Add(pair.Second.ClientId, state);
            return pair;
        }
    }
}
