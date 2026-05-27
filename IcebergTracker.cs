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
    private readonly ConcurrentDictionary<decimal, PriceLevelCycles> _priceCycles = new();

    private class PriceLevelCycles
    {
        public int Side;
        public int CycleCount;
        public decimal TotalFilled;
        public decimal LastDisplaySize;
        public DateTime LastSeen;
        public int BarIndex;
    }

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
                newIcebergConfirmed = HandleNew(mbo, currentBar);
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

    private bool HandleNew(MarketByOrder mbo, int currentBar)
    {
        int side = mbo.Side == MarketDataType.Bid ? 0 : 1;

        var snapshot = new OrderSnapshot
        {
            Price = mbo.Price,
            Side = side,
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

        // Price-level: check if a previous order at same price was recently deleted (Delete+New cycle)
        if (_priceCycles.TryGetValue(mbo.Price, out var cycle) && cycle.Side == side
            && (DateTime.Now - cycle.LastSeen).TotalSeconds < 5)
        {
            cycle.CycleCount++;
            cycle.LastDisplaySize = mbo.Volume;
            cycle.LastSeen = DateTime.Now;
            cycle.BarIndex = currentBar;
            return CheckPriceLevelPromotion(mbo.Price, cycle, mbo.ExchangeOrderId.ToString());
        }

        return false;
    }

    private bool CheckPriceLevelPromotion(decimal price, PriceLevelCycles cycle, string orderId)
    {
        if (cycle.CycleCount >= _minRefillCount && cycle.TotalFilled >= _minIcebergVolume)
        {
            bool existed = _confirmed.ContainsKey(price);
            _confirmed[price] = new IcebergEvent
            {
                OrderId = orderId,
                Price = price,
                Side = cycle.Side,
                TotalFilledVolume = cycle.TotalFilled,
                LastKnownDisplaySize = cycle.LastDisplaySize,
                RefillCount = cycle.CycleCount,
                FirstSeen = cycle.LastSeen,
                LastSeen = cycle.LastSeen,
                IsActive = true,
                BarIndex = cycle.BarIndex
            };
            RebuildSnapshot();
            return !existed;
        }
        return false;
    }

    private bool HandleChange(MarketByOrder mbo, int currentBar)
    {
        string orderId = mbo.ExchangeOrderId.ToString();

        if (!_activeOrders.TryGetValue(orderId, out var snapshot))
            return false;

        if (mbo.Volume > snapshot.CurrentVolume)
        {
            // Volume increased = hidden reserve refilled the displayed portion (iceberg replenishment)
            snapshot.RefillCount++;
            snapshot.LastKnownDisplaySize = mbo.Volume;
            snapshot.OriginalVolume = mbo.Volume;
        }
        else
        {
            // Volume decreased = normal fill execution
            decimal filled = snapshot.CurrentVolume - mbo.Volume;
            if (filled > 0)
                snapshot.TotalFilled += filled;
        }

        snapshot.CurrentVolume = mbo.Volume;
        snapshot.LastSeen = DateTime.Now;
        snapshot.BarIndex = currentBar;

        bool promoted = false;

        if (snapshot.RefillCount >= _minRefillCount && snapshot.TotalFilled >= _minIcebergVolume)
        {
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

        if (_activeOrders.TryRemove(orderId, out var snapshot))
        {
            // Record filled cycle at this price level for Delete+New detection
            decimal filled = snapshot.TotalFilled + snapshot.CurrentVolume; // include any remainder as filled
            if (filled > 0)
            {
                var cycle = _priceCycles.GetOrAdd(mbo.Price, _ => new PriceLevelCycles
                {
                    Side = snapshot.Side,
                    CycleCount = 0,
                    TotalFilled = 0,
                    LastDisplaySize = snapshot.OriginalVolume,
                    LastSeen = DateTime.Now,
                    BarIndex = snapshot.BarIndex
                });
                cycle.TotalFilled += filled;
                cycle.LastSeen = DateTime.Now;
            }
        }

        if (_confirmed.TryGetValue(mbo.Price, out var iceberg) && iceberg.OrderId == orderId)
        {
            iceberg.IsActive = false;
            RebuildSnapshot();
        }
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

        var staleCycles = _priceCycles
            .Where(kvp => (DateTime.Now - kvp.Value.LastSeen).TotalMinutes > expiryMinutes)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleCycles)
            _priceCycles.TryRemove(key, out _);
    }
}
