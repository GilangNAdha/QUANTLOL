using System;
using System.Linq;
using System.Reflection;
using System.Text;

public static class DumpAll
{
    public static int Main(string[] args)
    {
        var path = args[0];
        var outFile = args.Length > 1 ? args[1] : "api.txt";
        Assembly asm;
        try { asm = Assembly.LoadFrom(path); }
        catch (Exception e) { Console.WriteLine("LOAD_FAIL " + e); return 2; }

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
            Console.WriteLine("PARTIAL_LOAD " + ex.LoaderExceptions.Length + " loader exceptions");
        }

        var sb = new StringBuilder();
        sb.AppendLine("# cAlgo.API dump: " + types.Length + " types");
        foreach (var t in types.Where(t => t.IsPublic || t.IsNestedPublic).OrderBy(t => t.FullName))
        {
            sb.AppendLine($"TYPE {t.FullName} :: base={t.BaseType?.Name} kind={(t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class")} {string.Join(" ", t.GetInterfaces().Select(i => i.Name).Take(6).Select(n => "iface:" + n))}");
            if (t.IsEnum)
            {
                foreach (var n in Enum.GetNames(t)) sb.AppendLine($"  E {n}={Convert.ToInt64(Enum.Parse(t, n))}");
                continue;
            }
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMembers(F))
            {
                if (m is MethodInfo mi)
                {
                    if (mi.IsPrivate || mi.IsAssembly || mi.Name.StartsWith("<")) continue;
                    var ps = string.Join(", ", mi.GetParameters().Select(p =>
                        p.ParameterType.Name + " " + p.Name + (p.HasDefaultValue ? $"={Val(p.DefaultValue)}" : "")));
                    sb.AppendLine($"  M {(mi.IsStatic ? "static " : "")}{(mi.IsPublic ? "pub " : "prot ")}{mi.ReturnType.Name} {mi.Name}({ps})");
                }
                else if (m is ConstructorInfo ci && (ci.IsPublic || ci.IsFamily))
                    sb.AppendLine($"  C .ctor({string.Join(", ", ci.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                else if (m is PropertyInfo pi)
                    sb.AppendLine($"  P {pi.PropertyType.Name} {pi.Name} {{{(pi.GetGetMethod(true) != null ? " get;" : "")}{(pi.GetSetMethod(true) != null ? " set;" : "")}}}");
                else if (m is FieldInfo fi && (fi.IsPublic || fi.IsFamily))
                    sb.AppendLine($"  F {fi.FieldType.Name} {fi.Name}");
                else if (m is EventInfo ei)
                    sb.AppendLine($"  EV {ei.EventHandlerType?.Name} {ei.Name}");
            }
        }
        System.IO.File.WriteAllText(outFile, sb.ToString());
        Console.WriteLine("WROTE " + outFile + " bytes=" + sb.Length);
        return 0;
    }
    static string Val(object? v) => v == null ? "null" : v is string s ? "\"" + s + "\"" : v.ToString()!;
}
