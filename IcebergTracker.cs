using System.Collections.Concurrent;
using ATAS.DataFeedsCore;
using MarketDataType = ATAS.DataFeedsCore.MarketDataType;

namespace IcebergDetector;

public class IcebergTracker
{
    private readonly int _minRefillCount;
    private readonly decimal _minIcebergVolume;

    private readonly ConcurrentDictionary<string, OrderSnapshot> _activeOrders = new();
    private readonly ConcurrentDictionary<decimal, DeleteRecord> _recentDeletes = new();
    private readonly ConcurrentDictionary<decimal, PriceLevelState> _priceLevels = new();
    private readonly ConcurrentDictionary<decimal, IcebergEvent> _confirmed = new();

    private List<IcebergEvent> _renderSnapshot = new();
    private readonly object _snapshotLock = new();

    // Diagnostics
    public int DiagVolumeIncreasesSeen;
    public int DiagPriceLevelRefillsSeen;
    public int DiagMaxRefillCount;
    public decimal DiagMaxTotalFilled;
    public int DiagTotalNew;
    public int DiagTotalChange;
    public int DiagTotalDelete;
    public int DiagDeletesWithFills;
    public int DiagDeletesNoFills;
    public int DiagDeletesOrphan;
    public int DiagSizeRejected;
    public int DiagActiveOrders => _activeOrders.Count;
    public int DiagRecentDeletes => _recentDeletes.Count;
    public string DiagLastNewId = "";
    public string DiagLastDeleteId = "";

    private class OrderSnapshot
    {
        public decimal Price;
        public int Side;
        public decimal DisplaySize;
        public decimal CurrentVolume;
        public decimal TotalFilled;
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public int BarIndex;
    }

    private class DeleteRecord
    {
        public int Side;
        public decimal DisplaySize;
        public decimal FilledAmount;
        public DateTime DeleteTime;
        public int BarIndex;
    }

    private class PriceLevelState
    {
        public int Side;
        public decimal DisplaySize;
        public int CycleCount;
        public decimal TotalFilled;
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public int BarIndex;
        public string LastOrderId = "";
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

        switch (mbo.Type)
        {
            case MarketByOrderUpdateTypes.New:
            case MarketByOrderUpdateTypes.Snapshot:
                DiagTotalNew++;
                return HandleNew(mbo, currentBar);
            case MarketByOrderUpdateTypes.Change:
                DiagTotalChange++;
                return HandleChange(mbo, currentBar);
            case MarketByOrderUpdateTypes.Delete:
                DiagTotalDelete++;
                return HandleDelete(mbo);
        }
        return false;
    }

    private bool HandleNew(MarketByOrder mbo, int currentBar)
    {
        if (mbo.Volume <= 0) return false;

        string orderId = mbo.ExchangeOrderId.ToString();
        int side = mbo.Side == MarketDataType.Bid ? 0 : 1;

        DiagLastNewId = orderId;
        _activeOrders[orderId] = new OrderSnapshot
        {
            Price = mbo.Price,
            Side = side,
            DisplaySize = mbo.Volume,
            CurrentVolume = mbo.Volume,
            TotalFilled = 0m,
            FirstSeen = DateTime.Now,
            LastSeen = DateTime.Now,
            BarIndex = currentBar
        };

        // Price-level: check if a mostly-consumed order was recently deleted at this price
        if (_recentDeletes.TryGetValue(mbo.Price, out var del)
            && del.Side == side
            && (DateTime.Now - del.DeleteTime).TotalSeconds < 10)
        {
            bool sizeOk = mbo.Volume >= del.DisplaySize * 0.70m && mbo.Volume <= del.DisplaySize * 1.30m;
            if (!sizeOk) DiagSizeRejected++;
            _recentDeletes.TryRemove(mbo.Price, out _);

            var lvl = _priceLevels.GetOrAdd(mbo.Price, _ => new PriceLevelState
            {
                Side = side,
                DisplaySize = del.DisplaySize,
                CycleCount = 0,
                TotalFilled = 0m,
                FirstSeen = del.DeleteTime,
                LastSeen = DateTime.Now,
                BarIndex = del.BarIndex
            });

            lvl.CycleCount++;
            lvl.TotalFilled += del.FilledAmount;
            lvl.LastSeen = DateTime.Now;
            lvl.BarIndex = currentBar;
            lvl.DisplaySize = mbo.Volume;
            lvl.LastOrderId = orderId;

            DiagPriceLevelRefillsSeen++;
            if (lvl.CycleCount > DiagMaxRefillCount) DiagMaxRefillCount = lvl.CycleCount;
            if (lvl.TotalFilled > DiagMaxTotalFilled) DiagMaxTotalFilled = lvl.TotalFilled;

            return TryPromotePriceLevel(mbo.Price, lvl);
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
            // Volume increase = hidden reserve refilled (rare on Rithmic but handle it)
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

        if (snapshot.TotalFilled > DiagMaxTotalFilled) DiagMaxTotalFilled = snapshot.TotalFilled;

        return false;
    }

    private bool HandleDelete(MarketByOrder mbo)
    {
        string orderId = mbo.ExchangeOrderId.ToString();

        DiagLastDeleteId = orderId;
        if (_activeOrders.TryRemove(orderId, out var snapshot))
        {
            bool significantlyFilled = snapshot.TotalFilled >= snapshot.DisplaySize * 0.40m;
            decimal effectiveFill = snapshot.TotalFilled > 0 ? snapshot.TotalFilled : snapshot.DisplaySize;
            bool recordable = significantlyFilled || snapshot.TotalFilled == 0;

            if (significantlyFilled) DiagDeletesWithFills++;
            else if (snapshot.TotalFilled == 0) DiagDeletesNoFills++;

            if (recordable && effectiveFill > 0)
            {
                _recentDeletes[mbo.Price] = new DeleteRecord
                {
                    Side = snapshot.Side,
                    DisplaySize = snapshot.DisplaySize,
                    FilledAmount = effectiveFill,
                    DeleteTime = DateTime.Now,
                    BarIndex = snapshot.BarIndex
                };
            }
        }
        else
        {
            DiagDeletesOrphan++;
        }

        if (_confirmed.TryGetValue(mbo.Price, out var iceberg) && iceberg.OrderId == orderId)
        {
            iceberg.IsActive = false;
            _priceLevels.TryRemove(mbo.Price, out _);
            RebuildSnapshot();
        }

        return false;
    }

    private bool TryPromotePriceLevel(decimal price, PriceLevelState lvl)
    {
        if (lvl.CycleCount < _minRefillCount || lvl.TotalFilled < _minIcebergVolume)
            return false;

        bool existed = _confirmed.ContainsKey(price);
        _confirmed[price] = new IcebergEvent
        {
            OrderId = lvl.LastOrderId,
            Price = price,
            Side = lvl.Side,
            TotalFilledVolume = lvl.TotalFilled,
            LastKnownDisplaySize = lvl.DisplaySize,
            RefillCount = lvl.CycleCount,
            FirstSeen = lvl.FirstSeen,
            LastSeen = lvl.LastSeen,
            IsActive = true,
            BarIndex = lvl.BarIndex
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

        var staleDeletes = _recentDeletes
            .Where(kvp => (DateTime.Now - kvp.Value.DeleteTime).TotalSeconds > 30)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in staleDeletes)
            _recentDeletes.TryRemove(key, out _);

        // Purge snapshot orders that were never deleted (sitting idle > 10 min)
        var staleOrders = _activeOrders
            .Where(kvp => (DateTime.Now - kvp.Value.LastSeen).TotalMinutes > 10)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in staleOrders)
            _activeOrders.TryRemove(key, out _);

        var staleLevels = _priceLevels
            .Where(kvp => (DateTime.Now - kvp.Value.LastSeen).TotalMinutes > expiryMinutes)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in staleLevels)
            _priceLevels.TryRemove(key, out _);
    }
}
