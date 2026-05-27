using System.Collections.Concurrent;
using ATAS.DataFeedsCore;
using MarketDataType = ATAS.DataFeedsCore.MarketDataType;

namespace IcebergDetector;

public class IcebergTracker
{
    private readonly int _minRefillCount;
    private readonly decimal _minIcebergVolume;

    private readonly ConcurrentDictionary<string, OrderSnapshot> _activeOrders = new();
    private readonly ConcurrentDictionary<string, (OrderSnapshot snap, DateTime at)> _recentlyDeleted = new();
    private readonly ConcurrentDictionary<decimal, IcebergEvent> _confirmed = new();

    private List<IcebergEvent> _renderSnapshot = new();
    private readonly object _snapshotLock = new();

    // Diagnostics
    public int DiagVolumeIncreasesSeen;
    public int DiagSameIdDeleteNewSeen;
    public int DiagMaxRefillCount;
    public decimal DiagMaxTotalFilled;

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

        string orderId = mbo.ExchangeOrderId.ToString();
        int side = mbo.Side == MarketDataType.Bid ? 0 : 1;

        // Pattern 2: Delete+New with same order ID (Rithmic iceberg slice continuation)
        if (_recentlyDeleted.TryRemove(orderId, out var prev)
            && (DateTime.Now - prev.at).TotalSeconds < 3
            && prev.snap.Price == mbo.Price
            && prev.snap.Side == side)
        {
            var snap = prev.snap;
            snap.RefillCount++;
            snap.DisplaySize = mbo.Volume;
            snap.CurrentVolume = mbo.Volume;
            snap.LastSeen = DateTime.Now;
            snap.BarIndex = currentBar;
            _activeOrders[orderId] = snap;

            DiagSameIdDeleteNewSeen++;
            if (snap.RefillCount > DiagMaxRefillCount) DiagMaxRefillCount = snap.RefillCount;
            if (snap.TotalFilled > DiagMaxTotalFilled) DiagMaxTotalFilled = snap.TotalFilled;

            return TryPromote(mbo.Price, orderId, snap);
        }

        _activeOrders[orderId] = new OrderSnapshot
        {
            Price = mbo.Price,
            Side = side,
            DisplaySize = mbo.Volume,
            CurrentVolume = mbo.Volume,
            RefillCount = 0,
            TotalFilled = 0m,
            FirstSeen = DateTime.Now,
            LastSeen = DateTime.Now,
            BarIndex = currentBar
        };
        return false;
    }

    private bool HandleChange(MarketByOrder mbo, int currentBar)
    {
        string orderId = mbo.ExchangeOrderId.ToString();

        if (!_activeOrders.TryGetValue(orderId, out var snapshot))
            return false;

        if (mbo.Volume > snapshot.CurrentVolume)
        {
            // Pattern 1: volume increased = hidden reserve refilled displayed portion
            snapshot.RefillCount++;
            snapshot.DisplaySize = mbo.Volume;
            DiagVolumeIncreasesSeen++;
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

        if (snapshot.RefillCount > DiagMaxRefillCount) DiagMaxRefillCount = snapshot.RefillCount;
        if (snapshot.TotalFilled > DiagMaxTotalFilled) DiagMaxTotalFilled = snapshot.TotalFilled;

        return TryPromote(mbo.Price, orderId, snapshot);
    }

    private bool HandleDelete(MarketByOrder mbo)
    {
        string orderId = mbo.ExchangeOrderId.ToString();

        if (_activeOrders.TryRemove(orderId, out var snapshot))
        {
            // Save mostly-consumed orders for potential same-ID Delete+New continuation
            bool mostlyConsumed = snapshot.CurrentVolume <= snapshot.DisplaySize * 0.30m;
            if (mostlyConsumed && snapshot.TotalFilled > 0)
                _recentlyDeleted[orderId] = (snapshot, DateTime.Now);
        }

        if (_confirmed.TryGetValue(mbo.Price, out var iceberg) && iceberg.OrderId == orderId)
        {
            iceberg.IsActive = false;
            RebuildSnapshot();
        }

        return false;
    }

    private bool TryPromote(decimal price, string orderId, OrderSnapshot snap)
    {
        if (snap.RefillCount < _minRefillCount || snap.TotalFilled < _minIcebergVolume)
            return false;

        bool existed = _confirmed.ContainsKey(price);
        _confirmed[price] = new IcebergEvent
        {
            OrderId = orderId,
            Price = snap.Price,
            Side = snap.Side,
            TotalFilledVolume = snap.TotalFilled,
            LastKnownDisplaySize = snap.DisplaySize,
            RefillCount = snap.RefillCount,
            FirstSeen = snap.FirstSeen,
            LastSeen = snap.LastSeen,
            IsActive = true,
            BarIndex = snap.BarIndex
        };
        RebuildSnapshot();
        return !existed;
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

        var staleDeleted = _recentlyDeleted
            .Where(kvp => (DateTime.Now - kvp.Value.at).TotalSeconds > 10)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleDeleted)
            _recentlyDeleted.TryRemove(key, out _);
    }
}
