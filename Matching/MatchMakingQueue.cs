using System.Collections.Generic;
using YuJanggi.Server.V2.ClientSession;

namespace YuJanggi.Server.V2.Matching
{
    internal class MatchMakingQueue
    {
        #region Fields

        private readonly LinkedList<IClientSession> _sessions = new();

        #endregion

        #region Properties

        public int Count =>
            _sessions.Count;

        #endregion

        #region Public Methods

        // 매칭 대기 등록
        public void Enqueue(IClientSession session)
        {
            _sessions.AddLast(session);
        }

        // 매칭 취소
        public bool Remove(IClientSession session)
        {
            var node = FindSession(session);
            if (node is null)
                return false;

            _sessions.Remove(node);
            return true;
        }

        // 이미 대기 중인지 확인
        public bool Contains(IClientSession session)
        {
            return FindSession(session) is not null;
        }

        // 가장 먼저 들어온 클라이언트 꺼내기
        public bool TryDequeue(
            out IClientSession? session)
        {
            if (_sessions.First is null)
            {
                session = null;
                return false;
            }

            session = _sessions.First.Value;
            _sessions.RemoveFirst();

            return true;
        }

        public void Clear()
        {
            _sessions.Clear();
        }

        // 두 FIFO 항목을 함께 인출합니다. 실패 시 큐를 그대로 유지합니다.
        public bool TryDequeue(out IClientSession? first, out IClientSession? second)
        {
            var firstNode = _sessions.First;
            var secondNode = firstNode?.Next;
            if (firstNode is null || secondNode is null)
            {
                first = null;
                second = null;
                return false;
            }

            first = firstNode.Value;
            second = secondNode.Value;
            _sessions.Remove(firstNode);
            _sessions.Remove(secondNode);
            return true;
        }

        private LinkedListNode<IClientSession>? FindSession(IClientSession session)
        {
            for (var node = _sessions.First; node is not null; node = node.Next)
            {
                if (node.Value.ClientId == session.ClientId)
                    return node;
            }
            return null;
        }

        #endregion
    }
}
