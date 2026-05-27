using System.Collections.Concurrent;
using ATAS.DataFeedsCore;
using MarketDataType = ATAS.DataFeedsCore.MarketDataType;

namespace IcebergDetector;

public class IcebergTracker
{
    private readonly int _minRefillCount;
    private readonly decimal _minIcebergVolume;

    private readonly ConcurrentDictionary<string, OrderSnapshot> _activeOrders = new();
    private readonly ConcurrentDictionary<decimal, IcebergEvent> _confirmed = new();

    private List<IcebergEvent> _renderSnapshot = new();
    private readonly object _snapshotLock = new();

    private class OrderSnapshot
    {
        public decimal Price;
        public int Side;
        public decimal OriginalVolume;
        public decimal CurrentVolume;
        public decimal LastKnownDisplaySize;
        public int RefillCount;
        public decimal TotalFilled;
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public int BarIndex;
    }

    public IcebergTracker(int minRefillCount, decimal minIcebergVolume)
    {
        _minRefillCount = minRefillCount;
        _minIcebergVolume = minIcebergVolume;
    }

    // Returns true if a new iceberg price level was confirmed for the first time.
    public bool ProcessMboUpdate(MarketByOrder mbo, int currentBar)
    {
        bool newIcebergConfirmed = false;

        switch (mbo.Type)
        {
            case MarketByOrderUpdateTypes.New:
            case MarketByOrderUpdateTypes.Snapshot:
                HandleNew(mbo, currentBar);
                break;

            case MarketByOrderUpdateTypes.Change:
                newIcebergConfirmed = HandleChange(mbo, currentBar);
                break;

            case MarketByOrderUpdateTypes.Delete:
                HandleDelete(mbo);
                break;
        }

        return newIcebergConfirmed;
    }

    private void HandleNew(MarketByOrder mbo, int currentBar)
    {
        var snapshot = new OrderSnapshot
        {
            Price = mbo.Price,
            Side = mbo.Side == MarketDataType.Bid ? 0 : 1,
            OriginalVolume = mbo.Volume,
            CurrentVolume = mbo.Volume,
            LastKnownDisplaySize = mbo.Volume,
            RefillCount = 0,
            TotalFilled = 0m,
            FirstSeen = DateTime.Now,
            LastSeen = DateTime.Now,
            BarIndex = currentBar
        };

        _activeOrders[mbo.ExchangeOrderId.ToString()] = snapshot;
    }

    private bool HandleChange(MarketByOrder mbo, int currentBar)
    {
        string orderId = mbo.ExchangeOrderId.ToString();

        if (!_activeOrders.TryGetValue(orderId, out var snapshot))
            return false;

        decimal filled = snapshot.CurrentVolume - mbo.Volume;

        if (filled > 0)
            snapshot.TotalFilled += filled;

        // Replenishment: new volume is within 10% of the original display size and there was execution
        bool isReplenishment = mbo.Volume >= snapshot.OriginalVolume * 0.90m && filled > 0;

        if (isReplenishment)
        {
            snapshot.RefillCount++;
            snapshot.LastKnownDisplaySize = mbo.Volume;
        }

        snapshot.CurrentVolume = mbo.Volume;
        snapshot.LastSeen = DateTime.Now;
        snapshot.BarIndex = currentBar;

        bool promoted = false;

        if (snapshot.RefillCount >= _minRefillCount && snapshot.TotalFilled >= _minIcebergVolume)
        {
            Console.WriteLine($"Iceberg confirmed at price {mbo.Price}, refills {snapshot.RefillCount}");
            bool existed = _confirmed.ContainsKey(mbo.Price);

            _confirmed[mbo.Price] = new IcebergEvent
            {
                OrderId = orderId,
                Price = snapshot.Price,
                Side = snapshot.Side,
                TotalFilledVolume = snapshot.TotalFilled,
                LastKnownDisplaySize = snapshot.LastKnownDisplaySize,
                RefillCount = snapshot.RefillCount,
                FirstSeen = snapshot.FirstSeen,
                LastSeen = snapshot.LastSeen,
                IsActive = true,
                BarIndex = snapshot.BarIndex
            };

            RebuildSnapshot();
            promoted = !existed;
        }

        return promoted;
    }

    private void HandleDelete(MarketByOrder mbo)
    {
        string orderId = mbo.ExchangeOrderId.ToString();

        if (_confirmed.TryGetValue(mbo.Price, out var iceberg) && iceberg.OrderId == orderId)
        {
            iceberg.IsActive = false;
            RebuildSnapshot();
        }

        _activeOrders.TryRemove(orderId, out _);
    }

    private void RebuildSnapshot()
    {
        var newSnapshot = new List<IcebergEvent>(_confirmed.Values);

        lock (_snapshotLock)
        {
            _renderSnapshot = newSnapshot;
        }
    }

    public List<IcebergEvent> GetSnapshot()
    {
        lock (_snapshotLock)
        {
            return _renderSnapshot;
        }
    }

    public void Cleanup(int expiryMinutes)
    {
        var cutoff = DateTime.Now.AddMinutes(-expiryMinutes);
        var expired = _confirmed
            .Where(kvp => !kvp.Value.IsActive && kvp.Value.LastSeen < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
            _confirmed.TryRemove(key, out _);

        if (expired.Count > 0)
            RebuildSnapshot();
    }
}
