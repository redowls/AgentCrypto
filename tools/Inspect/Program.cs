using System.Reflection;
using Binance.Net.Interfaces.Clients.UsdFuturesApi;
using Binance.Net.Interfaces.Clients.SpotApi;

void DumpReturn(string name, MethodInfo? m)
{
    if (m is null) { Console.WriteLine($"{name} NOT FOUND"); return; }
    var ret = m.ReturnType;
    Console.WriteLine($"{name} -> {ret.FullName}");
    if (ret.IsGenericType)
        foreach (var ga in ret.GetGenericArguments())
            Console.WriteLine($"   T = {ga.FullName}");
}

var futAcct = typeof(IBinanceRestClientUsdFuturesApiAccount);
DumpReturn("UsdFut.Account.GetAccountInfoV2Async",
    futAcct.GetMethod("GetAccountInfoV2Async"));
DumpReturn("UsdFut.Account.GetAccountInfoV3Async",
    futAcct.GetMethod("GetAccountInfoV3Async"));
DumpReturn("UsdFut.Account.GetBalancesAsync",
    futAcct.GetMethod("GetBalancesAsync"));

Console.WriteLine();

var spotTrading = typeof(Binance.Net.Interfaces.Clients.SpotApi.IBinanceRestClientSpotApiTrading);
foreach (var m in spotTrading.GetMethods().Where(x => x.Name == "PlaceOrderAsync"))
{
    Console.WriteLine("Spot PlaceOrderAsync params:");
    foreach (var p in m.GetParameters())
        Console.WriteLine($"   {p.ParameterType.Name} {p.Name}{(p.IsOptional ? " (optional)" : "")}");
}

Console.WriteLine();
var futTrading = typeof(Binance.Net.Interfaces.Clients.UsdFuturesApi.IBinanceRestClientUsdFuturesApiTrading);
foreach (var m in futTrading.GetMethods().Where(x => x.Name == "PlaceOrderAsync"))
{
    Console.WriteLine("Fut PlaceOrderAsync params:");
    foreach (var p in m.GetParameters())
        Console.WriteLine($"   {p.ParameterType.Name} {p.Name}{(p.IsOptional ? " (optional)" : "")}");
}

Console.WriteLine();
var spotAcct = typeof(IBinanceRestClientSpotApiAccount);
foreach (var m in spotAcct.GetMethods().Where(x => x.Name == "GetAccountInfoAsync"))
{
    Console.WriteLine("Spot GetAccountInfoAsync:");
    Console.WriteLine($"  ret: {m.ReturnType}");
    foreach (var p in m.GetParameters())
        Console.WriteLine($"   {p.ParameterType.Name} {p.Name}{(p.IsOptional ? " (optional)" : "")}");
}

Console.WriteLine();
// Check spot account info model
var spotAcctInfo = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => a.GetExportedTypes())
    .FirstOrDefault(t => t.FullName == "Binance.Net.Objects.Models.Spot.BinanceAccountInfo");
if (spotAcctInfo is not null)
{
    Console.WriteLine($"BinanceAccountInfo props:");
    foreach (var p in spotAcctInfo.GetProperties())
        Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");
}

Console.WriteLine();
// Check spot order model
var spotOrder = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
    .FirstOrDefault(t => t.FullName == "Binance.Net.Objects.Models.Spot.BinanceOrder");
if (spotOrder is not null)
{
    Console.WriteLine($"BinanceOrder props:");
    foreach (var p in spotOrder.GetProperties())
        Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");
}

Console.WriteLine();
// Check placed order
var placedOrder = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
    .FirstOrDefault(t => t.FullName == "Binance.Net.Objects.Models.Spot.BinancePlacedOrder");
if (placedOrder is not null)
{
    Console.WriteLine($"BinancePlacedOrder props:");
    foreach (var p in placedOrder.GetProperties())
        Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");
}

Console.WriteLine();
// Futures order model
var futOrder = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
    .FirstOrDefault(t => t.FullName == "Binance.Net.Objects.Models.Futures.BinanceUsdFuturesOrder");
if (futOrder is not null)
{
    Console.WriteLine($"BinanceUsdFuturesOrder props:");
    foreach (var p in futOrder.GetProperties())
        Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");
}

Console.WriteLine();
// Spot trade model
var spotTrade = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
    .FirstOrDefault(t => t.FullName == "Binance.Net.Objects.Models.Spot.BinanceTrade");
if (spotTrade is not null)
{
    Console.WriteLine($"Spot BinanceTrade props:");
    foreach (var p in spotTrade.GetProperties())
        Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");
}

Console.WriteLine();
var spotKline = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
    .Where(t => t.Name.Contains("BinanceSpotKline") || t.Name == "BinanceKline" || t.Name == "IBinanceKline").ToList();
foreach (var t in spotKline)
{
    Console.WriteLine($"{t.FullName} props:");
    foreach (var p in t.GetProperties())
        Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");
    Console.WriteLine();
}

Console.WriteLine();
// Stream order update model
var streamOrderUpdate = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
    .FirstOrDefault(t => t.FullName == "Binance.Net.Objects.Models.Spot.Socket.BinanceStreamOrderUpdate");
if (streamOrderUpdate is not null)
{
    Console.WriteLine($"BinanceStreamOrderUpdate props:");
    foreach (var p in streamOrderUpdate.GetProperties())
        Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");
}

Console.WriteLine();
// SocketOptions
var socketOpts = AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
    .FirstOrDefault(t => t.FullName == "Binance.Net.Objects.Options.BinanceSocketOptions");
if (socketOpts is not null)
{
    Console.WriteLine($"BinanceSocketOptions props/fields:");
    foreach (var p in socketOpts.GetProperties())
        Console.WriteLine($"   prop {p.PropertyType.Name} {p.Name}");
    foreach (var p in socketOpts.GetFields())
        Console.WriteLine($"   field {p.FieldType.Name} {p.Name}");

    var bt = socketOpts.BaseType;
    while (bt is not null && bt != typeof(object))
    {
        Console.WriteLine($"  base = {bt.FullName}");
        foreach (var p in bt.GetProperties(BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance))
            Console.WriteLine($"     base prop {p.PropertyType.Name} {p.Name}");
        bt = bt.BaseType;
    }
}
