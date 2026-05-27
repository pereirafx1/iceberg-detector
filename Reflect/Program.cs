using System.Reflection;
using System.Runtime.Loader;

var atasPath = @"C:\Program Files (x86)\ATAS Platform";

// Load all DLLs from the ATAS folder so dependencies resolve
foreach (var dll in Directory.GetFiles(atasPath, "*.dll"))
{
    try { AssemblyLoadContext.Default.LoadFromAssemblyPath(dll); }
    catch { }
}

var dfc  = Assembly.LoadFrom(Path.Combine(atasPath, "ATAS.DataFeedsCore.dll"));
var ind  = Assembly.LoadFrom(Path.Combine(atasPath, "ATAS.Indicators.dll"));
var rend = Assembly.LoadFrom(Path.Combine(atasPath, "OFT.Rendering.dll"));

// ── 1. MarketByOrder and related types ───────────────────────────────────────
Console.WriteLine("=== ATAS.DataFeedsCore — types matching *MarketByOrder* *OrderSide* *UpdateType* *Changed* ===");
foreach (var t in dfc.GetTypes()
    .Where(t => t.Name.Contains("MarketByOrder", StringComparison.OrdinalIgnoreCase)
             || t.Name.Contains("OrderSide",     StringComparison.OrdinalIgnoreCase)
             || t.Name.Contains("UpdateType",    StringComparison.OrdinalIgnoreCase)
             || t.Name.Contains("Changed",       StringComparison.OrdinalIgnoreCase))
    .OrderBy(t => t.FullName))
    Console.WriteLine($"  {t.FullName}  [{(t.IsEnum ? "enum" : t.IsInterface ? "interface" : "class")}]");

// ── 2. MarketByOrder properties ───────────────────────────────────────────────
var mboType = dfc.GetType("ATAS.DataFeedsCore.MarketByOrder");
if (mboType != null)
{
    Console.WriteLine("\n=== MarketByOrder properties ===");
    foreach (var p in mboType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"  {p.PropertyType.Name,-30} {p.Name}");
}

// ── 3. IMarketByOrdersManager — event delegate signature ─────────────────────
var mgrType = dfc.GetTypes().FirstOrDefault(t => t.Name == "IMarketByOrdersManager");
if (mgrType != null)
{
    Console.WriteLine("\n=== IMarketByOrdersManager members ===");
    foreach (var m in mgrType.GetMembers())
        Console.WriteLine($"  {m.MemberType,-12} {m.Name}");
    var changed = mgrType.GetEvent("Changed");
    if (changed != null)
        Console.WriteLine($"\n  Changed event handler type: {changed.EventHandlerType?.FullName}");
}

// ── 4. ExtendedIndicator public methods ───────────────────────────────────────
var extType = ind.GetTypes().FirstOrDefault(t => t.FullName == "ATAS.Indicators.ExtendedIndicator");
if (extType != null)
{
    Console.WriteLine("\n=== ExtendedIndicator declared methods ===");
    foreach (var m in extType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                              .OrderBy(m => m.Name))
        Console.WriteLine($"  {m.ReturnType.Name,-20} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
}
else
{
    Console.WriteLine("\n  [ExtendedIndicator not found — listing all ATAS.Indicators types]");
    foreach (var t in ind.GetTypes().Where(t => t.Namespace == "ATAS.Indicators").OrderBy(t => t.Name))
        Console.WriteLine($"  {t.FullName}");
}

// ── 5. OFT.Rendering public types ─────────────────────────────────────────────
Console.WriteLine("\n=== OFT.Rendering public types ===");
foreach (var t in rend.GetTypes().Where(t => t.IsPublic).OrderBy(t => t.FullName))
    Console.WriteLine($"  {t.FullName}");
