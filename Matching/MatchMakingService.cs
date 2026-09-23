using YuJanggi.Protocol.V2.Matching;
using YuJanggi.Server.V2.ClientSession;

namespace YuJanggi.Server.V2.Matching
{
    internal sealed record MatchPair(IClientSession First, IClientSession Second);

    internal class MatchMakingService
    {
        private readonly Lock _sync = new();
        // 외부에서 동기화 없이 접근하지 못하도록 서비스가 큐를 소유합니다.
        private readonly MatchMakingQueue _queue = new();

        public MatchingResult RequestMatch(IClientSession session, out MatchPair? matchPair)
        {
            matchPair = null;
            lock (_sync)
            {
                if (!session.IsHandshakeCompleted)
                    return MatchingResult.HandshakeRequired;
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
                // 기존 멱등 취소 동작 유지: 대기 중이 아니어도 Cancelled입니다.
                _queue.Remove(session);
                return MatchingCancelResult.Cancelled;
            }
        }

        // 반드시 _sync 안에서 호출합니다. 네트워크 처리는 호출자가 수행합니다.
        private MatchPair? TryCreateMatchPair()
        {
            if (_queue.Count < 2)
                return null;

            if (!_queue.TryDequeue(out var first, out var second))
                throw new InvalidOperationException("매칭 대기열의 개수와 인출 결과가 일치하지 않습니다.");

            // 큐를 서비스만 소유하고 ClientId로 중복 검사하므로 동일 세션끼리 매칭되지 않습니다.
            // TODO:
            // 쌍을 만든 뒤에는 두 세션이 큐에서 빠지므로 재신청이 새 대기로 접수될 수 있습니다.
            // 현재는 매칭 확정 상태가 없어 중복 대국 및 확정 후 취소를 구분하지 못합니다.
            // GameRoom / MatchSession에서 세션별 생명주기와 AlreadyMatched 판정을 추가해야 합니다.
            return new MatchPair(first!, second!);
        }
    }
}
