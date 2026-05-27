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
        public decimal DisplaySize;
        public decimal CurrentVolume;
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

    public bool ProcessMboUpdate(MarketByOrder mbo, int currentBar)
    {
        // Ignore trade-side events and zero/negative prices
        if (mbo.Side == MarketDataType.Trade || mbo.Price <= 0)
            return false;

        return mbo.Type switch
        {
            MarketByOrderUpdateTypes.New or MarketByOrderUpdateTypes.Snapshot => HandleNew(mbo, currentBar),
            MarketByOrderUpdateTypes.Change => HandleChange(mbo, currentBar),
            MarketByOrderUpdateTypes.Delete => HandleDelete(mbo),
            _ => false
        };
    }

    private bool HandleNew(MarketByOrder mbo, int currentBar)
    {
        if (mbo.Volume <= 0) return false;

        var snapshot = new OrderSnapshot
        {
            Price = mbo.Price,
            Side = mbo.Side == MarketDataType.Bid ? 0 : 1,
            DisplaySize = mbo.Volume,
            CurrentVolume = mbo.Volume,
            RefillCount = 0,
            TotalFilled = 0m,
            FirstSeen = DateTime.Now,
            LastSeen = DateTime.Now,
            BarIndex = currentBar
        };

        _activeOrders[mbo.ExchangeOrderId.ToString()] = snapshot;
        return false;
    }

    private bool HandleChange(MarketByOrder mbo, int currentBar)
    {
        string orderId = mbo.ExchangeOrderId.ToString();

        if (!_activeOrders.TryGetValue(orderId, out var snapshot))
            return false;

        if (mbo.Volume > snapshot.CurrentVolume)
        {
            // Volume increased = hidden reserve refilled the displayed portion
            snapshot.RefillCount++;
            snapshot.DisplaySize = mbo.Volume;
        }
        else
        {
            decimal filled = snapshot.CurrentVolume - mbo.Volume;
            if (filled > 0)
                snapshot.TotalFilled += filled;
        }

        snapshot.CurrentVolume = mbo.Volume;
        snapshot.LastSeen = DateTime.Now;
        snapshot.BarIndex = currentBar;

        if (snapshot.RefillCount >= _minRefillCount && snapshot.TotalFilled >= _minIcebergVolume)
        {
            bool existed = _confirmed.ContainsKey(mbo.Price);

            _confirmed[mbo.Price] = new IcebergEvent
            {
                OrderId = orderId,
                Price = snapshot.Price,
                Side = snapshot.Side,
                TotalFilledVolume = snapshot.TotalFilled,
                LastKnownDisplaySize = snapshot.DisplaySize,
                RefillCount = snapshot.RefillCount,
                FirstSeen = snapshot.FirstSeen,
                LastSeen = snapshot.LastSeen,
                IsActive = true,
                BarIndex = snapshot.BarIndex
            };

            RebuildSnapshot();
            return !existed;
        }

        return false;
    }

    private bool HandleDelete(MarketByOrder mbo)
    {
        string orderId = mbo.ExchangeOrderId.ToString();
        _activeOrders.TryRemove(orderId, out _);

        if (_confirmed.TryGetValue(mbo.Price, out var iceberg) && iceberg.OrderId == orderId)
        {
            iceberg.IsActive = false;
            RebuildSnapshot();
        }

        return false;
    }

    private void RebuildSnapshot()
    {
        var newSnapshot = new List<IcebergEvent>(_confirmed.Values);
        lock (_snapshotLock)
            _renderSnapshot = newSnapshot;
    }

    public List<IcebergEvent> GetSnapshot()
    {
        lock (_snapshotLock)
            return _renderSnapshot;
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
