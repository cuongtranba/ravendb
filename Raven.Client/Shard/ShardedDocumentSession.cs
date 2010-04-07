using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Raven.Database.Data;
using System;
using Raven.Client.Shard;

namespace Raven.Client.Shard
{
	public class ShardedDocumentSession : IDocumentSession
	{
        public event Action<object> Stored;

        public ShardedDocumentSession(IShardStrategy shardStrategy, params IDocumentSession[] shardSessions)
		{
            this.shardStrategy = shardStrategy;
            this.shardSessions = shardSessions;

            foreach (var shardSession in shardSessions)
            {
                shardSession.Stored += this.Stored;
            }
		}

        IShardStrategy shardStrategy = null;
        IDocumentSession[] shardSessions = null;

		public T Load<T>(string id)
		{
            var shardIds = shardStrategy.ShardResolutionStrategy.SelectShardIdsFromData(ShardResolutionStrategyData.BuildFrom(typeof(T)));

            if (shardIds == null || shardIds.Count == 0) throw new ApplicationException("Unable to resolve shard from type " + typeof(T).Name);

            if (shardIds.Count > 1) throw new ApplicationException("Can't resolve type " + typeof(T).Name + " for single entity load, resolved multiple shards");

            var shardSession = GetSingleShardSession(shardIds[0]);

            return shardSession.Load<T>(id);
		}

        public void StoreAll<T>(IEnumerable<T> entities)
        {
            foreach (var entity in entities)
            {
                Store(entity);
            }
        }

        private void SingleShardAction<T>(T entity, Action<IDocumentSession> action)
        {
            string shardId = shardStrategy.ShardSelectionStrategy.SelectShardIdForNewObject(entity);
            if (String.IsNullOrEmpty(shardId)) throw new ApplicationException("Can't find single shard to use for entity: " + entity.ToString());

            var shardSession = GetSingleShardSession(shardId);

            action(shardSession);
        }

        private IDocumentSession GetSingleShardSession(string shardId)
        {
            var shardSession = shardSessions.Where(x => x.StoreIdentifier == shardId).FirstOrDefault();
            if (shardSession == null) throw new ApplicationException("Can't find single shard with identifier: " + shardId);
            return shardSession;
        }

		public void Store<T>(T entity)
		{
            SingleShardAction(entity, shard => shard.Store(entity));
		}

		public void SaveChanges()
		{
            throw new NotImplementedException();
        }

		public IQueryable<T> Query<T>()
		{
            throw new NotImplementedException();
        }

		public IList<T> GetAll<T>() // NOTE: We probably need to ask the user if they can accept stale results
		{
            throw new NotImplementedException();
        }

        public string StoreIdentifier { get { return "ShardedSession"; } }

        #region IDisposable Members

        public void Dispose()
        {
            foreach (var shardSession in shardSessions)
                shardSession.Dispose();

            //dereference all event listeners
            Stored = null;
        }

        #endregion
 
    }
}