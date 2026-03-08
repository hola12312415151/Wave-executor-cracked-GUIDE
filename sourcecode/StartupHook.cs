using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;

public class StartupHook
{
    private static int _dumped;
    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    static StartupHook()
    {
        foreach (var fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (fi.FieldType != typeof(OpCode))
                continue;
            var op = (OpCode)fi.GetValue(null);
            var value = unchecked((ushort)op.Value);
            if (value < 0x100)
                OneByte[value] = op;
            else if ((value & 0xFF00) == 0xFE00)
                TwoByte[value & 0xFF] = op;
        }
    }

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(@"C:\Users\alvaro\Desktop\holacomotas\hook_out");
            File.AppendAllText(LogPath(), "[init] startup hook loaded" + Environment.NewLine);
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                TryDumpAssembly(asm, "initial");
            }
            var t = new Thread(ScanLoop);
            t.IsBackground = true;
            t.Start();
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath(), "[init-error] " + ex + Environment.NewLine);
        }
    }

    private static void ScanLoop()
    {
        for (int i = 0; i < 60 && _dumped == 0; i++)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    TryDumpAssembly(asm, "scan");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(LogPath(), "[scan-error] " + ex + Environment.NewLine);
            }
            Thread.Sleep(500);
        }
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
    {
        TryDumpAssembly(e.LoadedAssembly, "load");
    }

    private static void TryDumpAssembly(Assembly asm, string source)
    {
        try
        {
        var name = asm.GetName().Name ?? "";
        if (Interlocked.CompareExchange(ref _dumped, 0, 0) != 0 && !name.Equals("Wave", StringComparison.OrdinalIgnoreCase))
            return;

        var path = "";
        try { path = asm.Location ?? ""; } catch { }
        if (!name.Equals("Wave", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("\\Wave.dll", StringComparison.OrdinalIgnoreCase))
            return;

        if (Interlocked.Exchange(ref _dumped, 1) != 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("[assembly] " + source);
        sb.AppendLine("FullName=" + asm.FullName);
        sb.AppendLine("Location=" + path);
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
            sb.AppendLine("TYPELOAD " + ex);
        }
        foreach (var type in types.OrderBy(t => t.FullName))
        {
            sb.AppendLine("TYPE " + type.FullName);
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch (Exception ex)
            {
                sb.AppendLine("METHODERR " + ex.GetType().FullName + ": " + ex.Message);
                continue;
            }
            foreach (var m in methods)
            {
                string methodName;
                try { methodName = m.Name; } catch { methodName = "<noname>"; }
                string returnType;
                try { returnType = m.ReturnType.FullName ?? m.ReturnType.Name; } catch { returnType = "<ret-err>"; }
                sb.AppendLine("METHOD " + m.MetadataToken.ToString("X8") + " " + methodName + " RET=" + returnType);
                try
                {
                    var body = m.GetMethodBody();
                    if (body != null)
                    {
                        var dis = Disassemble(m, body.GetILAsByteArray());
                        if (dis.Length != 0)
                            sb.Append(dis);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("ILERR " + ex.GetType().FullName + ": " + ex.Message);
                }
            }
        }
        File.WriteAllText(@"C:\Users\alvaro\Desktop\holacomotas\hook_out\wave_il_dump.txt", sb.ToString());
        File.AppendAllText(LogPath(), "[dumped] " + asm.FullName + Environment.NewLine);
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath(), "[dump-error] " + ex + Environment.NewLine);
        }
    }

    private static string LogPath()
    {
        return @"C:\Users\alvaro\Desktop\holacomotas\hook_out\startup_hook.log";
    }

    private static string Disassemble(MethodInfo method, byte[] il)
    {
        if (il == null || il.Length == 0)
            return "";

        var sb = new StringBuilder();
        int i = 0;
        while (i < il.Length)
        {
            int offset = i;
            OpCode op;
            byte b = il[i++];
            if (b == 0xFE)
                op = TwoByte[il[i++]];
            else
                op = OneByte[b];

            sb.Append("IL_");
            sb.Append(offset.ToString("X4"));
            sb.Append(": ");
            sb.Append(op.Name);

            object operand = null;
            try
            {
                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineI:
                        operand = (sbyte)il[i++];
                        break;
                    case OperandType.InlineI:
                        operand = BitConverter.ToInt32(il, i);
                        i += 4;
                        break;
                    case OperandType.InlineI8:
                        operand = BitConverter.ToInt64(il, i);
                        i += 8;
                        break;
                    case OperandType.ShortInlineR:
                        operand = BitConverter.ToSingle(il, i);
                        i += 4;
                        break;
                    case OperandType.InlineR:
                        operand = BitConverter.ToDouble(il, i);
                        i += 8;
                        break;
                    case OperandType.ShortInlineVar:
                        operand = il[i++];
                        break;
                    case OperandType.InlineVar:
                        operand = BitConverter.ToUInt16(il, i);
                        i += 2;
                        break;
                    case OperandType.ShortInlineBrTarget:
                        operand = i + (sbyte)il[i];
                        i += 1;
                        break;
                    case OperandType.InlineBrTarget:
                        operand = i + 4 + BitConverter.ToInt32(il, i);
                        i += 4;
                        break;
                    case OperandType.InlineSwitch:
                        int count = BitConverter.ToInt32(il, i);
                        i += 4;
                        var targets = new string[count];
                        for (int j = 0; j < count; j++)
                        {
                            targets[j] = "IL_" + (i + 4 * count + BitConverter.ToInt32(il, i + 4 * j)).ToString("X4");
                        }
                        i += 4 * count;
                        operand = string.Join(", ", targets);
                        break;
                    case OperandType.InlineString:
                        {
                            int token = BitConverter.ToInt32(il, i);
                            i += 4;
                            operand = "\"" + SafeResolveString(method.Module, token) + "\"";
                            break;
                        }
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        {
                            int token = BitConverter.ToInt32(il, i);
                            i += 4;
                            operand = SafeResolveMember(method.Module, token);
                            break;
                        }
                    case OperandType.InlineSig:
                        operand = "sig 0x" + BitConverter.ToInt32(il, i).ToString("X8");
                        i += 4;
                        break;
                }
            }
            catch (Exception ex)
            {
                operand = "<err " + ex.GetType().Name + ">";
            }

            if (operand != null)
            {
                sb.Append(" ");
                sb.Append(operand);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string SafeResolveString(Module module, int token)
    {
        try { return module.ResolveString(token); }
        catch { return "str(0x" + token.ToString("X8") + ")"; }
    }

    private static string SafeResolveMember(Module module, int token)
    {
        try
        {
            var member = module.ResolveMember(token);
            if (member == null)
                return "tok 0x" + token.ToString("X8");
            return member.DeclaringType + "::" + member.Name;
        }
        catch
        {
            return "tok 0x" + token.ToString("X8");
        }
    }
}
