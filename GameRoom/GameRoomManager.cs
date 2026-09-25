namespace YuJanggi.Server.V2.GameRoom
{
    using ClientSession;
    using Matching;

    /// <summary>매칭된 게임룸의 생성, 등록 및 보관 목록 정리를 담당합니다.</summary>
    internal sealed class GameRoomManager
    {
        private readonly ClientSessionManager _sessionManager;
        private readonly Lock _roomSync;
        private readonly Dictionary<string, GameRoom> _gameRooms = new();

        /// <summary>참가자 연결 종료와 룸 등록을 동기화하는 서버의 잠금을 공유합니다.</summary>
        public GameRoomManager(ClientSessionManager sessionManager, Lock roomSync)
        {
            _sessionManager = sessionManager;
            _roomSync = roomSync;
        }

        /// <summary>
        /// 매칭된 초·한 참가자로 룸을 초기화하고 매칭 ID별로 보관합니다.
        /// 양쪽 Accepted 응답 전송 후, MatchingFound 이벤트 전송 전에 호출합니다.
        /// 준비 완료 처리와 실제 게임 시작은 수행하지 않습니다.
        /// </summary>
        public GameRoom CreateGameRoom(string matchId, MatchPair matchPair)
        {
            var room = new GameRoom();
            room.Initialize(matchId, matchPair);

            lock (_roomSync)
            {
                if (!_sessionManager.Contains(matchPair.First.ClientId) ||
                    !_sessionManager.Contains(matchPair.Second.ClientId))
                    throw new InvalidOperationException("연결이 종료된 참가자의 룸을 생성할 수 없습니다.");

                _gameRooms.Add(room.MatchId, room);
            }
            return room;
        }

        /// <summary>연결이 종료된 참가자가 속한 룸을 보관 목록에서 제거합니다.</summary>
        public void RemoveRoomsForPlayer(Guid clientId)
        {
            lock (_roomSync)
            {
                foreach (var entry in _gameRooms.ToArray())
                {
                    if (entry.Value.ChoPlayer?.ClientId == clientId ||
                        entry.Value.HanPlayer?.ClientId == clientId)
                        _gameRooms.Remove(entry.Key);
                }
            }

            // TODO:
            // 참가자가 연결을 종료하면 현재는 룸 참조만 제거하며 상대에게 종료 알림을 보내지 않습니다.
            // 상대가 MatchingFound를 받았다면 Matched 상태로 남을 수 있습니다.
            // GameRoom 생명주기 구현 시 상대 알림, 재접속 여부 및 Close의 자원 정리 정책을 연결해야 합니다.
        }

        /// <summary>서버 종료 시 클라이언트 처리 작업이 끝난 뒤 보관 중인 룸 참조를 모두 제거합니다.</summary>
        public void Clear()
        {
            lock (_roomSync)
                _gameRooms.Clear();
        }
    }
}
