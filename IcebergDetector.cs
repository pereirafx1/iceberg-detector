using ATAS.Indicators;
using ATAS.DataFeedsCore;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Utils.Common.Logging;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using Color = System.Drawing.Color;

namespace IcebergDetector;

[DisplayName("Iceberg Detector")]
[Category("Order Flow")]
[Description("Detects hidden iceberg orders in real-time using MBO data feed (requires Rithmic)")]
public class IcebergDetector : Indicator
{
    private IcebergTracker _tracker = null!;
    private bool _mboAvailable = false;

    public IcebergDetector()
    {
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    // --- Detection ---

    private int _minRefillCount = 3;

    [Display(Name = "Min Refill Count", GroupName = "Detection", Order = 1)]
    [Range(2, 20)]
    public int MinRefillCount
    {
        get => _minRefillCount;
        set
        {
            _minRefillCount = value;
            RecalculateValues();
        }
    }

    private decimal _minIcebergVolume = 10m;

    [Display(Name = "Min Total Volume", GroupName = "Detection", Order = 2)]
    public decimal MinIcebergVolume
    {
        get => _minIcebergVolume;
        set
        {
            _minIcebergVolume = value;
            RecalculateValues();
        }
    }

    [Display(Name = "Event Expiry (minutes)", GroupName = "Detection", Order = 3)]
    [Range(1, 120)]
    public int ExpiryMinutes { get; set; } = 10;

    // --- Display ---

    [Display(Name = "Show Buy Icebergs", GroupName = "Display", Order = 10)]
    public bool ShowBuyIcebergs { get; set; } = true;

    [Display(Name = "Show Sell Icebergs", GroupName = "Display", Order = 11)]
    public bool ShowSellIcebergs { get; set; } = true;

    [Display(Name = "Buy Color", GroupName = "Display", Order = 12)]
    public Color BuyColor { get; set; } = Color.FromArgb(200, 0, 200, 80);

    [Display(Name = "Sell Color", GroupName = "Display", Order = 13)]
    public Color SellColor { get; set; } = Color.FromArgb(200, 220, 50, 50);

    [Display(Name = "Line Height (px)", GroupName = "Display", Order = 14)]
    [Range(2, 20)]
    public int LineHeight { get; set; } = 6;

    [Display(Name = "Show Volume Label", GroupName = "Display", Order = 15)]
    public bool ShowVolumeLabel { get; set; } = true;

    [Display(Name = "Show Refill Count", GroupName = "Display", Order = 16)]
    public bool ShowRefillCount { get; set; } = true;

    [Display(Name = "Font Size", GroupName = "Display", Order = 17)]
    [Range(6, 14)]
    public int FontSize { get; set; } = 9;

    // --- Alerts ---

    [Display(Name = "Alert on New Iceberg", GroupName = "Alerts", Order = 20)]
    public bool AlertOnDetection { get; set; } = false;

    protected override void OnInitialize()
    {
        _tracker = new IcebergTracker(MinRefillCount, MinIcebergVolume);
        _ = SubscribeMarketByOrderData();
    }

    protected override void OnMarketByOrdersChanged(IEnumerable<MarketByOrder> orders)
    {
        _mboAvailable = true;
        bool newIcebergFound = false;

        foreach (var mbo in orders)
        {
            bool isNew = _tracker.ProcessMboUpdate(mbo, CurrentBar);
            if (isNew) newIcebergFound = true;
        }

        if (newIcebergFound && AlertOnDetection)
            this.LogInfo("New iceberg order detected!");

        RedrawChart();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == CurrentBar - 1)
            _tracker.Cleanup(ExpiryMinutes);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (layout != DrawingLayouts.Final) return;
        if (ChartInfo is null) return;
        if (!_mboAvailable) return;

        var snapshot = _tracker.GetSnapshot();
        if (snapshot.Count == 0) return;

        var font = new RenderFont("Arial", FontSize);
        int barWidth = ChartInfo.GetXByBar(1) - ChartInfo.GetXByBar(0);

        foreach (var iceberg in snapshot)
        {
            if (iceberg.Side == 0 && !ShowBuyIcebergs) continue;
            if (iceberg.Side == 1 && !ShowSellIcebergs) continue;

            var color = iceberg.Side == 0 ? BuyColor : SellColor;
            var borderColor = Color.FromArgb(255, color.R, color.G, color.B);

            int x = ChartInfo.GetXByBar(iceberg.BarIndex);
            int y = ChartInfo.GetYByPrice(iceberg.Price);
            int halfH = LineHeight / 2;

            // Horizontal bar at the iceberg price level (3-bar width)
            var lineRect = new Rectangle(x - barWidth, y - halfH, barWidth * 3, LineHeight);
            context.FillRectangle(color, lineRect);
            context.DrawRectangle(new RenderPen(borderColor, 1), lineRect);

            if (iceberg.Side == 0) // Buy — triangle below pointing up
            {
                int ty = y + halfH + 4;
                var pts = new[]
                {
                    new Point(x,     ty - 8),
                    new Point(x - 5, ty),
                    new Point(x + 5, ty)
                };
                context.FillPolygon(color, pts);
            }
            else // Sell — triangle above pointing down
            {
                int ty = y - halfH - 4;
                var pts = new[]
                {
                    new Point(x,     ty + 8),
                    new Point(x - 5, ty),
                    new Point(x + 5, ty)
                };
                context.FillPolygon(color, pts);
            }

            if (ShowVolumeLabel || ShowRefillCount)
            {
                var labelParts = new List<string>();
                if (ShowVolumeLabel)
                    labelParts.Add($"V:{iceberg.TotalFilledVolume:F0}");
                if (ShowRefillCount)
                    labelParts.Add($"R:{iceberg.RefillCount}");

                string label = string.Join(" ", labelParts);
                var labelRect = new Rectangle(x + 8, y - FontSize / 2, 200, FontSize + 4);
                context.DrawString(label, font, borderColor, labelRect);
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
