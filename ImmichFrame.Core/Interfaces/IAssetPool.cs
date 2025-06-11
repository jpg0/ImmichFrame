using ImmichFrame.Core.Api;
using System.Threading.Tasks;

namespace ImmichFrame.Core.Interfaces
{
    public interface IAssetPool
    {
        string PoolName { get; }

        /// <summary>
        /// Gets the total number of assets that match this pool's criteria.
        /// This might involve an API call if the count is not yet cached.
        /// </summary>
        Task<int> GetAssetCountAsync();

        /// <summary>
        /// Retrieves the next available asset from the pool's internal queue.
        /// Will trigger queue refill mechanisms if the queue is low or empty.
        /// </summary>
        Task<AssetResponseDto?> GetNextAssetFromQueueAsync();

        /// <summary>
        /// Initiates an asynchronous background task to check queue levels and refill if necessary.
        /// This method should be non-blocking.
        /// </summary>
        void StartBackgroundRefillAsync();
    }
}
