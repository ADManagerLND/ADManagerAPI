using System.Collections.Concurrent;

namespace ADManagerAPI.Utils
{
    /// <summary>
    /// Classe utilitaire pour un HashSet thread-safe optimisé pour les opérations concurrentes
    /// </summary>
    public class ConcurrentHashSet<T> : IDisposable
    {
        private readonly HashSet<T> _hashSet;
        private readonly ReaderWriterLockSlim _lock;

        public ConcurrentHashSet(IEqualityComparer<T>? comparer = null)
        {
            _hashSet = new HashSet<T>(comparer ?? EqualityComparer<T>.Default);
            _lock = new ReaderWriterLockSlim();
        }

        public ConcurrentHashSet(IEnumerable<T> collection, IEqualityComparer<T>? comparer = null)
        {
            _hashSet = new HashSet<T>(collection, comparer ?? EqualityComparer<T>.Default);
            _lock = new ReaderWriterLockSlim();
        }

        /// <summary>
        /// Ajoute un élément au HashSet de manière thread-safe
        /// </summary>
        public bool Add(T item)
        {
            _lock.EnterWriteLock();
            try
            {
                return _hashSet.Add(item);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Vérifie si l'élément existe de manière thread-safe
        /// </summary>
        public bool Contains(T item)
        {
            _lock.EnterReadLock();
            try
            {
                return _hashSet.Contains(item);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Retire un élément du HashSet de manière thread-safe
        /// </summary>
        public bool Remove(T item)
        {
            _lock.EnterWriteLock();
            try
            {
                return _hashSet.Remove(item);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Vide le HashSet de manière thread-safe
        /// </summary>
        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _hashSet.Clear();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Obtient le nombre d'éléments de manière thread-safe
        /// </summary>
        public int Count
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    return _hashSet.Count;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Copie les éléments vers un tableau
        /// </summary>
        public T[] ToArray()
        {
            _lock.EnterReadLock();
            try
            {
                return _hashSet.ToArray();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Obtient un énumérateur thread-safe (snapshot)
        /// </summary>
        public IEnumerator<T> GetEnumerator()
        {
            _lock.EnterReadLock();
            try
            {
                // Créer une copie pour éviter les modifications concurrentes
                return _hashSet.ToList().GetEnumerator();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Libère les ressources
        /// </summary>
        public void Dispose()
        {
            _lock?.Dispose();
        }
    }
}
