namespace YuJanggi.Server.V2.GameRoom
{
    using ClientSession;

    /// <summary>한 대국의 두 참가자와 네트워크 룸의 준비·시작·종료 상태를 소유합니다.</summary>
    internal sealed class GameRoom
    {
        private readonly Lock _sync = new();
        private bool _choReady;
        private bool _hanReady;
        private bool _started;
        private bool _closed;

        public string MatchId { get; private set; } = string.Empty;
        public IClientSession? ChoPlayer { get; private set; }
        public IClientSession? HanPlayer { get; private set; }
        public bool ChoReady { get { lock (_sync) return _choReady; } }
        public bool HanReady { get { lock (_sync) return _hanReady; } }
        public bool Started { get { lock (_sync) return _started; } }
        public bool Closed { get { lock (_sync) return _closed; } }

        public void Initialize(string matchId, IClientSession choPlayer, IClientSession hanPlayer)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
            ArgumentNullException.ThrowIfNull(choPlayer);
            ArgumentNullException.ThrowIfNull(hanPlayer);
            if (choPlayer.ClientId == hanPlayer.ClientId)
                throw new ArgumentException("서로 다른 두 세션이 필요합니다.");

            lock (_sync)
            {
                if (_closed || MatchId.Length != 0)
                    throw new InvalidOperationException("초기화할 수 없는 게임룸입니다.");
                MatchId = matchId;
                ChoPlayer = choPlayer;
                HanPlayer = hanPlayer;
            }
        }

        /// <summary>양측 준비를 처음 충족한 요청만 시작 전환에 성공합니다.</summary>
        public bool MarkPlayerReady(IClientSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            lock (_sync)
            {
                if (!Contains(session))
                    throw new InvalidOperationException("룸 참가자가 아닙니다.");
                if (_closed || _started)
                    return false;

                if (ChoPlayer!.ClientId == session.ClientId)
                    _choReady = true;
                else
                    _hanReady = true;

                if (!_choReady || !_hanReady)
                    return false;
                _started = true;
                return true;
            }
        }

        // 참가자와 MatchId는 초기화 후 변경하지 않습니다.
        public bool Contains(IClientSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            return ChoPlayer?.ClientId == session.ClientId || HanPlayer?.ClientId == session.ClientId;
        }

        public IClientSession? GetOpponent(IClientSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (ChoPlayer?.ClientId == session.ClientId)
                return HanPlayer;
            if (HanPlayer?.ClientId == session.ClientId)
                return ChoPlayer;
            return null;
        }

        /// <summary>이후 준비·시작을 차단합니다. 연결의 해제는 서버가 담당합니다.</summary>
        public void Close()
        {
            lock (_sync)
                _closed = true;
        }
    }
}
