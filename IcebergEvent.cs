namespace IcebergDetector;

public class IcebergEvent
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Side { get; set; }              // 0 = Buy, 1 = Sell
    public decimal TotalFilledVolume { get; set; }
    public decimal LastKnownDisplaySize { get; set; }
    public int RefillCount { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsActive { get; set; }
    public int BarIndex { get; set; }
}
