using ImmichFrame.Core.Api;

namespace ImmichFrame.Core.Logic.Pool;

public class MultiAssetPool(IEnumerable<IAssetPool> delegates) : AggregatingAssetPool
{
    private readonly Random _random = new();

    public override async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        var counts = delegates.Select(pool => pool.GetAssetCount(ct));
        return (await Task.WhenAll(counts)).Sum();
    }

    protected override async Task<AssetResponseDto?> GetNextAsset(CancellationToken ct)
    {
        var poolsAndCounts = await Task.WhenAll(
            delegates.Select(async pool => (Pool: pool, Count: await pool.GetAssetCount(ct)))
                .ToList());

        var totalAssets = poolsAndCounts.Sum(pool => pool.Count);

        var randomAssetIndex = (long)(_random.NextDouble() * totalAssets);

        foreach (var poolAndCount in poolsAndCounts)
        {
            if (randomAssetIndex < poolAndCount.Count)
            {
                return (await poolAndCount.Pool.GetAssets(1, ct)).FirstOrDefault();
            }

            randomAssetIndex -= poolAndCount.Count;
        }
        
        return null;
    }
}