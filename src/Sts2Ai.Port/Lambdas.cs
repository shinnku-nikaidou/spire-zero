using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Sts2Ai.Port;

/// <summary>The compiler's names of the game's lambdas and local functions, read from the game's assembly.
///
/// A delegate of the running game names its method as reflection sees it (`&lt;GenerateMoveStateMachine&gt;b__12_0` on
/// the class, on `&lt;&gt;c` or on a `&lt;&gt;c__DisplayClass12_0`), and an export says which function a delegate is by
/// that name. The decompiled source has the lambda and not the name. The link is the member's IL: each place a
/// delegate is made is an `ldftn` of the compiled method, in the order of the code, so the k-th compiler-made target
/// of a member's `ldftn`s is the k-th lambda (or local function handed over) of its source. A member the compiler
/// turned into a state machine (async, an iterator) has its code, and so its `ldftn`s, in that machine's MoveNext.</summary>
public sealed class Lambdas
{
    /// <summary>One compiler-made method a delegate is made of. `Class` is its declaring type's CLR full name, `Holder`
    /// what that type is: "c" (the class of lambdas that capture nothing), "this" (the class itself: only `this` is
    /// captured, or nothing in a static one), "closure" (a display class: its fields are what was captured), "?" (a
    /// generic one, not named here). `Fields` are the display class's fields by name.</summary>
    public sealed record Made(string Class, string Method, bool IsStatic, int Params, string Holder, IReadOnlyList<string> Fields, int Row);

    private readonly PEReader pe;
    private readonly MetadataReader md;
    private readonly Dictionary<string, List<MethodDefinitionHandle>> byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<Made>> made = [];
    private static readonly Dictionary<ushort, OperandType> Operands = OperandTable();

    public Lambdas(string assemblyPath)
    {
        pe = new PEReader(File.OpenRead(assemblyPath));
        md = pe.GetMetadataReader();
        foreach (var th in md.TypeDefinitions)
        {
            string type = FullName(th);
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                string key = type + "::" + md.GetString(md.GetMethodDefinition(mh).Name);
                if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
                list.Add(mh);
            }
        }
    }

    private static Dictionary<ushort, OperandType> OperandTable()
    {
        var table = new Dictionary<ushort, OperandType>();
        foreach (var f in typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (f.GetValue(null) is OpCode op) table[(ushort)op.Value] = op.OperandType;
        return table;
    }

    private string FullName(TypeDefinitionHandle th)
    {
        var t = md.GetTypeDefinition(th);
        string name = md.GetString(t.Name);
        var outer = t.GetDeclaringType();
        if (!outer.IsNil) return FullName(outer) + "+" + name;
        string ns = md.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>The compiler-made methods the member's code makes delegates of, in the order of the code; null when
    /// the member is not found in the assembly as the source has it (another overload count, another signature).</summary>
    public List<Made>? Of(IMethodSymbol member)
    {
        if (!byName.TryGetValue($"{World.FullName(member.ContainingType)}::{member.MetadataName}", out var candidates)) return null;
        var fits = candidates.Where(h => Fits(h, member)).ToList();
        return fits.Count == 1 ? Of(fits[0]) : null;
    }

    /// <summary>The same for a compiler-made method itself: the lambdas inside a lambda.</summary>
    public List<Made>? Inside(Made lambda) => lambda.Row == 0 ? null : Of(MetadataTokens.MethodDefinitionHandle(lambda.Row));

    private bool Fits(MethodDefinitionHandle h, IMethodSymbol member)
    {
        var sig = md.GetMethodDefinition(h).DecodeSignature(new Names(md), null);
        if (sig.ParameterTypes.Length != member.Parameters.Length) return false;
        for (int i = 0; i < sig.ParameterTypes.Length; i++)
        {
            string ours = Simple(member.Parameters[i].Type), theirs = sig.ParameterTypes[i].TrimEnd('&');
            if (!ours.Contains('*') && !theirs.Contains('*') && theirs != ours) return false;
        }
        return true;
    }

    /// <summary>A type's name as the signature reader below writes it; "*" where it would depend on a type parameter.</summary>
    private static string Simple(ITypeSymbol t) => t switch
    {
        IArrayTypeSymbol a => Simple(a.ElementType) is var e && e == "*" ? "*" : e + "[]",
        ITypeParameterSymbol or IPointerTypeSymbol or IFunctionPointerTypeSymbol or IDynamicTypeSymbol => "*",
        INamedTypeSymbol n => n.MetadataName,
        _ => "*",
    };

    private List<Made> Of(MethodDefinitionHandle h)
    {
        int row = MetadataTokens.GetRowNumber(h);
        lock (made)
        {
            if (made.TryGetValue(row, out var known)) return known;
            var list = new List<Made>();
            Scan(h, list);
            // A member the compiler made a state machine of: its code is the machine's MoveNext.
            if (MachineOf(h) is { } machine)
                foreach (var mh in md.GetTypeDefinition(machine).GetMethods())
                    if (md.GetString(md.GetMethodDefinition(mh).Name) == "MoveNext") Scan(mh, list);
            made[row] = list;
            return list;
        }
    }

    /// <summary>The state machine class of an async method or an iterator, named by the attribute the compiler puts on it.</summary>
    private TypeDefinitionHandle? MachineOf(MethodDefinitionHandle h)
    {
        var method = md.GetMethodDefinition(h);
        foreach (var ah in method.GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(ah);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var ctor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
            string name = md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name);
            if (name is not ("AsyncStateMachineAttribute" or "IteratorStateMachineAttribute")) continue;
            // The value: a prolog, then the type's name as a string.
            var blob = md.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) continue;
            string? typeName = blob.ReadSerializedString();
            if (typeName == null) continue;
            int comma = typeName.IndexOf(',');
            if (comma >= 0) typeName = typeName[..comma];
            foreach (var nested in md.GetTypeDefinition(method.GetDeclaringType()).GetNestedTypes())
                if (FullName(nested) == typeName) return nested;
        }
        return null;
    }

    private void Scan(MethodDefinitionHandle h, List<Made> into)
    {
        var method = md.GetMethodDefinition(h);
        if (method.RelativeVirtualAddress == 0) return;
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
        while (il.RemainingBytes > 0)
        {
            ushort code = il.ReadByte();
            if (code == 0xFE) code = (ushort)(0xFE00 | il.ReadByte());
            if (!Operands.TryGetValue(code, out var operand)) throw new InvalidDataException($"IL opcode {code:x} in {FullName(method.GetDeclaringType())}::{md.GetString(method.Name)}");
            // ldftn, ldvirtftn
            if (code is 0xFE06 or 0xFE07)
            {
                if (Compiled(MetadataTokens.EntityHandle(il.ReadInt32())) is { } target) into.Add(target);
                continue;
            }
            switch (operand)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar: il.Offset += 1; break;
                case OperandType.InlineVar: il.Offset += 2; break;
                case OperandType.InlineI8 or OperandType.InlineR: il.Offset += 8; break;
                case OperandType.InlineSwitch:
                {
                    int targets = (int)il.ReadUInt32();
                    il.Offset += 4 * targets;
                    break;
                }
                default: il.Offset += 4; break;
            }
        }
    }

    /// <summary>What an `ldftn` names, when the compiler made it: a lambda or a local function.</summary>
    private Made? Compiled(EntityHandle token)
    {
        switch (token.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var h = (MethodDefinitionHandle)token;
                var method = md.GetMethodDefinition(h);
                string name = md.GetString(method.Name);
                if (!name.StartsWith('<')) return null;
                var type = md.GetTypeDefinition(method.GetDeclaringType());
                string typeName = md.GetString(type.Name);
                bool isStatic = (method.Attributes & System.Reflection.MethodAttributes.Static) != 0;
                string holder = type.GetGenericParameters().Count > 0 || method.GetGenericParameters().Count > 0 ? "?"
                    : typeName == "<>c" ? "c" : typeName.StartsWith("<>c__DisplayClass") ? "closure" : typeName.StartsWith('<') ? "?" : "this";
                var fields = holder == "closure" ? type.GetFields().Select(f => md.GetString(md.GetFieldDefinition(f).Name)).ToList() : [];
                int count = method.DecodeSignature(new Names(md), null).ParameterTypes.Length;
                return new Made(FullName(method.GetDeclaringType()), name, isStatic, count, holder, fields, MetadataTokens.GetRowNumber(h));
            }
            // A method of a generic class or a generic method, named through its instantiation: counted, not named.
            case HandleKind.MemberReference:
            {
                var reference = md.GetMemberReference((MemberReferenceHandle)token);
                string name = md.GetString(reference.Name);
                return name.StartsWith('<') && reference.Parent.Kind == HandleKind.TypeSpecification ? new Made("?", name, false, -1, "?", [], 0) : null;
            }
            case HandleKind.MethodSpecification:
            {
                var inner = Compiled(md.GetMethodSpecification((MethodSpecificationHandle)token).Method);
                return inner == null ? null : inner with { Holder = "?", Row = 0 };
            }
            default:
                return null;
        }
    }

    /// <summary>Types of a signature as their bare names: enough to tell the overloads of a method apart.</summary>
    private sealed class Names(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => md.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => md.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? genericContext, int index) => "*";
        public string GetGenericTypeParameter(object? genericContext, int index) => "*";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "*";
    }
}
