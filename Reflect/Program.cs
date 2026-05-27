using System.Reflection;
using System.Runtime.Loader;

var atasPath = @"C:\Program Files (x86)\ATAS Platform";

// Pre-load all DLLs so dependencies resolve
foreach (var dll in Directory.GetFiles(atasPath, "*.dll"))
{
    try { AssemblyLoadContext.Default.LoadFromAssemblyPath(dll); }
    catch { }
}

var dfc = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(atasPath, "ATAS.DataFeedsCore.dll"));
var ind = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(atasPath, "ATAS.Indicators.dll"));

// ── 1. SubscribeMarketByOrderData — return type ───────────────────────────────
Console.WriteLine("=== SubscribeMarketByOrderData ===");
foreach (var t in ind.GetTypes())
{
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
    {
        if (m.Name.Contains("SubscribeMarketByOrder", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  [{t.FullName}] {m.ReturnType.FullName} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"))})");
    }
}

// ── 2. IMarketByOrdersManager — all members ───────────────────────────────────
Console.WriteLine("\n=== IMarketByOrdersManager members ===");
var mgrType = dfc.GetTypes().FirstOrDefault(t => t.Name == "IMarketByOrdersManager")
           ?? ind.GetTypes().FirstOrDefault(t => t.Name == "IMarketByOrdersManager");
if (mgrType != null)
{
    foreach (var m in mgrType.GetMembers())
        Console.WriteLine($"  {m.MemberType,-12} {m.Name}");

    var changed = mgrType.GetEvent("Changed");
    if (changed?.EventHandlerType != null)
    {
        Console.WriteLine($"\n  Changed event handler type : {changed.EventHandlerType.FullName}");
        var invoke = changed.EventHandlerType.GetMethod("Invoke");
        if (invoke != null)
            Console.WriteLine($"  Invoke signature           : void Invoke({string.Join(", ", invoke.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"))})");
    }
}
else
{
    Console.WriteLine("  IMarketByOrdersManager NOT FOUND in either DLL");
}

// ── 3. OnMarketByOrdersChanged — virtual method signature ─────────────────────
Console.WriteLine("\n=== OnMarketByOrdersChanged ===");
foreach (var t in ind.GetTypes())
{
    var m = t.GetMethod("OnMarketByOrdersChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (m != null)
        Console.WriteLine($"  [{t.FullName}] {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"))})");
}

// ── 4. MarketByOrder — all public properties ──────────────────────────────────
Console.WriteLine("\n=== MarketByOrder properties ===");
var mboType = dfc.GetTypes().FirstOrDefault(t => t.FullName == "ATAS.DataFeedsCore.MarketByOrder");
if (mboType != null)
{
    foreach (var p in mboType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        Console.WriteLine($"  {p.PropertyType.Name,-30} {p.Name}");
}

// ── 5. MarketByOrderUpdateTypes enum values ───────────────────────────────────
Console.WriteLine("\n=== MarketByOrderUpdateTypes values ===");
var updType = dfc.GetTypes().FirstOrDefault(t => t.Name == "MarketByOrderUpdateTypes");
if (updType?.IsEnum == true)
    foreach (var v in Enum.GetNames(updType))
        Console.WriteLine($"  {v}");

// ── 6. MarketDataType enum values ─────────────────────────────────────────────
Console.WriteLine("\n=== MarketDataType values ===");
var mdtType = dfc.GetTypes().FirstOrDefault(t => t.Name == "MarketDataType")
           ?? ind.GetTypes().FirstOrDefault(t => t.Name == "MarketDataType");
if (mdtType?.IsEnum == true)
    foreach (var v in Enum.GetNames(mdtType))
        Console.WriteLine($"  {v}");
