using System;
using System.Linq;
using System.Reflection;
using System.Text;

public static class DumpApi
{
    static readonly string[] Want = {
        "cAlgo.API.Robot", "cAlgo.API.Symbol", "cAlgo.API.Bars", "cAlgo.API.Bar",
        "cAlgo.API.Position", "cAlgo.API.PendingOrder", "cAlgo.API.TradeResult",
        "cAlgo.API.TradeType", "cAlgo.API.Account", "cAlgo.API.MarketData",
        "cAlgo.API.TimeFrame", "cAlgo.API.Timer", "cAlgo.API.Server",
        "cAlgo.API.ErrorCode", "cAlgo.API.RobotAttribute", "cAlgo.API.SymbolInfo",
        "cAlgo.API.PositionOpenedEventArgs", "cAlgo.API.PositionClosedEventArgs",
        "cAlgo.API.TradeOperation", "cAlgo.API.AccessRights", "cAlgo.API.TimeZones",
        "cAlgo.API.History", "cAlgo.API.PositionState", "cAlgo.API.PendingOrderState",
        "cAlgo.API.SymbolCommissionType", "cAlgo.API.SymbolCommissionChargeType",
        "cAlgo.API.VolumeInUnits", "cAlgo.API.IAccount", "cAlgo.API.IBars"
    };

    public static int Main(string[] args)
    {
        var asm = Assembly.LoadFrom(args[0]);
        var sb = new StringBuilder();
        var types = asm.GetExportedTypes();
        sb.AppendLine("### ALLTYPE_NAMES_MATCHING_KEYWORDS");
        foreach (var t in types.Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.Name, "Timer|History|Symbol|Trade|Bar|Account|Position|Quote|Tick|Error")).OrderBy(t => t.FullName))
            sb.AppendLine("NAME " + t.FullName + (t.IsEnum ? " [enum]" : ""));
        foreach (var name in Want)
        {
            var t = types.FirstOrDefault(x => x.FullName == name);
            if (t == null) { sb.AppendLine("### MISSING " + name); continue; }
            sb.AppendLine("### TYPE " + t.FullName + " : " + (t.BaseType?.Name ?? "-"));
            if (t.IsEnum)
            {
                foreach (var v in Enum.GetNames(t)) sb.AppendLine("  ENUM " + v + "=" + Convert.ToInt64(Enum.Parse(t, v)));
                continue;
            }
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMembers(F))
            {
                if (m is MethodInfo mi)
                {
                    if (mi.IsSpecialName) continue;
                    if (mi.IsPrivate || mi.IsAssembly) continue;
                    if (mi.Name.StartsWith("<")) continue;
                    var ps = string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name + (p.HasDefaultValue ? "=" + (p.DefaultValue ?? "null") : "")));
                    sb.AppendLine("  M " + (mi.IsStatic ? "static " : "") + (mi.IsPublic ? "public " : "protected ") + mi.ReturnType.Name + " " + mi.Name + "(" + ps + ")");
                }
                else if (m is PropertyInfo pi)
                {
                    var acc = (pi.GetGetMethod(true) != null ? "get;" : "") + (pi.GetSetMethod(true) != null ? "set;" : "");
                    sb.AppendLine("  P " + pi.PropertyType.Name + " " + pi.Name + " {" + acc + "}");
                }
                else if (m is FieldInfo fi && (fi.IsPublic || fi.IsFamily))
                    sb.AppendLine("  F " + fi.FieldType.Name + " " + fi.Name);
                else if (m is EventInfo ei)
                    sb.AppendLine("  E " + ei.EventHandlerType?.Name + " " + ei.Name);
            }
        }
        Console.WriteLine("<<<API_DUMP_START>>>");
        Console.WriteLine(sb.ToString());
        Console.WriteLine("<<<API_DUMP_END>>>");
        return 0;
    }
}
