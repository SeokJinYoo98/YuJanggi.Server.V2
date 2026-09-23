using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace YuJanggi.Server.V2.ClientSession
{
    internal sealed class ClientSessionManager
    {
        #region Fields

        private readonly ConcurrentDictionary<Guid, IClientSession> _sessions;

        #endregion

        #region Properties

        public int Count =>
            _sessions.Count;

        #endregion

        #region Constructors

        public ClientSessionManager()
        {
            _sessions =
                new ConcurrentDictionary<Guid, IClientSession>();
        }

        #endregion

        #region Public Methods

        public bool Add(IClientSession session)
        {
            return _sessions.TryAdd(
                session.ClientId,
                session);
        }

        public bool TryGet(
            Guid clientId,
            out IClientSession? session)
        {
            return _sessions.TryGetValue(
                clientId,
                out session);
        }

        public bool Remove(
            Guid clientId,
            out IClientSession? session)
        {
            return _sessions.TryRemove(
                clientId,
                out session);
        }

        public bool Contains(Guid clientId)
        {
            return _sessions.ContainsKey(clientId);
        }

        public void Clear()
        {
            foreach (IClientSession session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
        }
        public Task[] GetProcessingTasks()
        {
            return _sessions.Values
                .Where(session => session.ProcessingTask is not null)
                .Select(session => session.ProcessingTask!)
                .ToArray();
        }
        #endregion
    }
}

