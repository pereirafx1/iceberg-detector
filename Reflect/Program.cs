using System.Reflection;
using System.Runtime.Loader;

var atasPath = @"C:\Program Files (x86)\ATAS Platform";

// Pre-load all DLLs so dependencies resolve; ignore individual failures
foreach (var dll in Directory.GetFiles(atasPath, "*.dll"))
{
    try { AssemblyLoadContext.Default.LoadFromAssemblyPath(dll); }
    catch { }
}

var dfc = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(atasPath, "ATAS.DataFeedsCore.dll"));
var ind = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(atasPath, "ATAS.Indicators.dll"));

// GetTypes() resilient to partial load failures
static Type[] SafeGetTypes(Assembly asm)
{
    try { return asm.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray()!; }
}

var dfcTypes = SafeGetTypes(dfc);
var indTypes = SafeGetTypes(ind);

// ── 1. SubscribeMarketByOrderData ─────────────────────────────────────────────
Console.WriteLine("=== SubscribeMarketByOrderData ===");
foreach (var t in indTypes)
{
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
    {
        if (m.Name.Contains("SubscribeMarketByOrder", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  [{t.Name}]  returns={m.ReturnType.FullName}  params=({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
    }
}

// ── 2. IMarketByOrdersManager — Changed event delegate ───────────────────────
Console.WriteLine("\n=== IMarketByOrdersManager ===");
var mgrType = dfcTypes.Concat(indTypes).FirstOrDefault(t => t.Name == "IMarketByOrdersManager");
if (mgrType != null)
{
    foreach (var m in mgrType.GetMembers())
        Console.WriteLine($"  {m.MemberType,-12} {m.Name}");

    var changed = mgrType.GetEvent("Changed");
    if (changed?.EventHandlerType != null)
    {
        Console.WriteLine($"\n  Changed delegate type : {changed.EventHandlerType.FullName}");
        var invoke = changed.EventHandlerType.GetMethod("Invoke");
        if (invoke != null)
            Console.WriteLine($"  Invoke signature      : ({string.Join(", ", invoke.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"))})");
    }
}
else Console.WriteLine("  NOT FOUND");

// ── 3. OnMarketByOrdersChanged virtual method ────────────────────────────────
Console.WriteLine("\n=== OnMarketByOrdersChanged ===");
foreach (var t in indTypes)
{
    var m = t.GetMethod("OnMarketByOrdersChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (m != null)
        Console.WriteLine($"  [{t.Name}] ({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"))})");
}

// ── 4. MarketByOrder properties ───────────────────────────────────────────────
Console.WriteLine("\n=== MarketByOrder properties ===");
var mboType = dfcTypes.FirstOrDefault(t => t.FullName == "ATAS.DataFeedsCore.MarketByOrder");
if (mboType != null)
    foreach (var p in mboType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        Console.WriteLine($"  {p.PropertyType.Name,-30} {p.Name}");
else Console.WriteLine("  NOT FOUND");

// ── 5. MarketByOrderUpdateTypes enum ─────────────────────────────────────────
Console.WriteLine("\n=== MarketByOrderUpdateTypes ===");
var updType = dfcTypes.FirstOrDefault(t => t.Name == "MarketByOrderUpdateTypes");
if (updType?.IsEnum == true)
    foreach (var v in Enum.GetNames(updType)) Console.WriteLine($"  {v}");
else Console.WriteLine("  NOT FOUND");

// ── 6. MarketDataType enum ────────────────────────────────────────────────────
Console.WriteLine("\n=== MarketDataType ===");
var mdtType = dfcTypes.Concat(indTypes).FirstOrDefault(t => t.Name == "MarketDataType");
if (mdtType?.IsEnum == true)
    foreach (var v in Enum.GetNames(mdtType)) Console.WriteLine($"  {v}");
else Console.WriteLine("  NOT FOUND");
