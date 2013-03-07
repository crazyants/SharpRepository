using System;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Reflection;
using Omu.ValueInjecter;
using SharpRepository.Repository;
using SharpRepository.Repository.Caching;
using SharpRepository.Repository.FetchStrategies;
using SharpRepository.Repository.Helpers;

namespace SharpRepository.Ef5Repository
{
    public class DynamicProxyCloneInjection : LoopValueInjection
    {
        protected override void Inject(object source, object target)
        {
            base.Inject(source, target);
        }

        protected override object SetValue(object v)
        {
            if (v == null)
                return null;

            var type = v.GetType();
            var typeName = type.FullName;

            if (typeName.StartsWith("System.Data.Entity.DynamicProxies"))
            {
                var baseType = type.BaseType;
                if (baseType != null)
                {
                    // what happens if we make this NULL, will it get it back later after being pulled out of cache?
                    //return null;

                    // circular reference: need to be able to detect that in the custom CloneIInjection class
                    return Activator.CreateInstance(baseType).InjectFrom(v);
                }
            }

            return base.SetValue(v);
        }
    }

    public class Ef5RepositoryBase<T, TKey> : LinqRepositoryBase<T, TKey> where T : class, new()
    {
        protected IDbSet<T> DbSet { get; private set; }
        protected DbContext Context { get; private set; }

        internal Ef5RepositoryBase(DbContext dbContext, ICachingStrategy<T, TKey> cachingStrategy = null) : base(cachingStrategy)
        {
            Initialize(dbContext, cachingStrategy != null);
        }

        private void Initialize(DbContext dbContext, bool hasCaching)
        {
            Context = dbContext;
            DbSet = Context.Set<T>();
        }

        protected override void AddItem(T entity)
        {
            if (typeof(TKey) == typeof(Guid) || typeof(TKey) == typeof(string))
            {
                TKey id;
                if (GetPrimaryKey(entity, out id) && Equals(id, default(TKey)))
                {
                    id = GeneratePrimaryKey();
                    SetPrimaryKey(entity, id);
                }
            }
            DbSet.Add(entity);
        }

        protected override void DeleteItem(T entity)
        {
            DbSet.Remove(entity);
        }

        protected override void UpdateItem(T entity)
        {
            // mark this entity as modified, in case it is not currently attached to this context
            try
            {
                Context.Entry(entity).State = EntityState.Modified;
            }
            catch (Exception)
            {
                // don't let this throw everything off
            }
        }

        protected override void SaveChanges()
        {
            Context.SaveChanges();
        }

        protected override IQueryable<T> BaseQuery(IFetchStrategy<T> fetchStrategy = null)
        {
            var query = DbSet.AsQueryable();
            return fetchStrategy == null ? query : fetchStrategy.IncludePaths.Aggregate(query, (current, path) => current.Include(path));
        }

        public override TCacheItem ConvertItem<TCacheItem>(TCacheItem item)
        {
            return item;

            if (item.GetType() == typeof(TCacheItem))
            {
                return item;
            }

            // this is a dynamic proxy so let's get rid of the 

            // using Activator.CreateInstance instead of new TCacheItem() so that I don't need the to mark TCacheItem as new() 
            //  when marked as new() it won't allow me to use anonymous types in the selector param of FindAll or other methods
            return (TCacheItem)Activator.CreateInstance<TCacheItem>().InjectFrom < DynamicProxyCloneInjection>(item); // can't use as because otherwise we would need a "where TCacheItem : class" which would mean we couldn't do a selector that returns an int or a string
        }

        // we override the implementation fro LinqBaseRepository becausee this is built in and doesn't need to find the key column and do dynamic expressions, etc.
        protected override T GetQuery(TKey key)
        {
            return DbSet.Find(key);
        }

        protected override PropertyInfo GetPrimaryKeyPropertyInfo()
        {
            // checks for the Code First KeyAttribute and if not there no the normal checks
            var type = typeof(T);
            var keyType = typeof(TKey);

            return type.GetProperties().FirstOrDefault(x => x.HasAttribute<KeyAttribute>() && x.PropertyType == keyType)
                ?? base.GetPrimaryKeyPropertyInfo();
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            if (Context == null) return;

            Context.Dispose();
            Context = null;
        }

        private TKey GeneratePrimaryKey()
        {
            if (typeof(TKey) == typeof(Guid))
            {
                return (TKey)Convert.ChangeType(Guid.NewGuid(), typeof(TKey));
            }

            if (typeof(TKey) == typeof(string))
            {
                return (TKey)Convert.ChangeType(Guid.NewGuid().ToString(), typeof(TKey));
            }
            
            throw new InvalidOperationException("Primary key could not be generated. This only works for GUID, Int32 and String.");
        }
    }
}