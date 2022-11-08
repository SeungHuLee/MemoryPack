﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MemoryPack.Generator;

partial class MemoryPackGenerator
{
    static void Generate(TypeDeclarationSyntax syntax, Compilation compilation, string? serializationInfoLogDirectoryPath, IGeneratorContext context)
    {
        var semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);

        var typeSymbol = semanticModel.GetDeclaredSymbol(syntax, context.CancellationToken);
        if (typeSymbol == null)
        {
            return;
        }

        // verify is partial
        if (!IsPartial(syntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MustBePartial, syntax.Identifier.GetLocation(), typeSymbol.Name));
            return;
        }

        // nested is not allowed
        if (IsNested(syntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NestedNotAllow, syntax.Identifier.GetLocation(), typeSymbol.Name));
            return;
        }

        var reference = new ReferenceSymbols(compilation);
        var typeMeta = new TypeMeta(typeSymbol, reference);

        if (typeMeta.GenerateType == GenerateType.NoGenerate)
        {
            return;
        }

        // ReportDiagnostic when validate failed.
        if (!typeMeta.Validate(syntax, context))
        {
            return;
        }

        var fullType = typeMeta.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "")
            .Replace("<", "_")
            .Replace(">", "_");

        var sb = new StringBuilder();

        sb.AppendLine(@"
// <auto-generated/>
#nullable enable
#pragma warning disable CS0108 // hides inherited member
#pragma warning disable CS0162 // Unreachable code
#pragma warning disable CS0164 // This label has not been referenced
#pragma warning disable CS0219 // Variable assigned but never used
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8601 // Possible null reference assignment
#pragma warning disable CS8604 // Possible null reference argument for parameter
#pragma warning disable CS8619
#pragma warning disable CS8620
#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method
#pragma warning disable CS8765 // Nullability of type of parameter
#pragma warning disable CS9074 // The 'scoped' modifier of parameter doesn't match overridden or implemented member
#pragma warning disable CA1050 // Declare types in namespaces.

using System;
using MemoryPack;
");

        var ns = typeMeta.Symbol.ContainingNamespace;
        if (!ns.IsGlobalNamespace)
        {
            if (context.IsCSharp10OrGreater())
            {
                sb.AppendLine($"namespace {ns};");
            }
            else
            {
                sb.AppendLine($"namespace {ns} {{");
            }
        }
        sb.AppendLine();

        // Write document comment as remarks
        if (typeMeta.GenerateType is GenerateType.Object or GenerateType.VersionTolerant)
        {
            BuildDebugInfo(sb, typeMeta, true);

            // also output to log
            if (serializationInfoLogDirectoryPath != null)
            {
                try
                {
                    if (!Directory.Exists(serializationInfoLogDirectoryPath))
                    {
                        Directory.CreateDirectory(serializationInfoLogDirectoryPath);
                    }
                    var logSw = new StringBuilder();
                    BuildDebugInfo(logSw, typeMeta, false);
                    var message = logSw.ToString();

                    File.WriteAllText(Path.Combine(serializationInfoLogDirectoryPath, $"{fullType}.txt"), message, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex.ToString());
                }
            }
        }

        // emit type info
        typeMeta.Emit(sb, context);

        if (!ns.IsGlobalNamespace && !context.IsCSharp10OrGreater())
        {
            sb.AppendLine($"}}");
        }

        var code = sb.ToString();

        context.AddSource($"{fullType}.MemoryPackFormatter.g.cs", code);
    }

    static bool IsPartial(TypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
    }

    static bool IsNested(TypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration.Parent is TypeDeclarationSyntax;
    }

    static void BuildDebugInfo(StringBuilder sb, TypeMeta type, bool xmlDocument)
    {
        string WithEscape(ISymbol symbol)
        {
            var str = symbol.FullyQualifiedToString().Replace("global::", "");
            if (xmlDocument)
            {
                return str.Replace("<", "&lt;").Replace(">", "&gt;");
            }
            else
            {
                return str;
            }
        }

        if (!xmlDocument)
        {
            if (type.IsUnmanagedType)
            {
                sb.Append("unmanaged ");
            }
            sb.AppendLine(WithEscape(type.Symbol));
            sb.AppendLine("---");
        }
        else
        {
            sb.AppendLine("/// <remarks>");
            sb.AppendLine("/// MemoryPack serialize members:<br/>");
            sb.AppendLine("/// <code>");
        }

        foreach (var item in type.Members)
        {
            if (xmlDocument)
            {
                sb.Append("/// <b>");
            }

            sb.Append(WithEscape(item.MemberType));
            if (xmlDocument)
            {
                sb.Append("</b>");
            }

            sb.Append(" ");
            sb.Append(item.Name);

            if (xmlDocument)
            {
                sb.AppendLine("<br/>");
            }
            else
            {
                sb.AppendLine();
            }
        }
        if (xmlDocument)
        {
            sb.AppendLine("/// </code>");
            sb.AppendLine("/// </remarks>");
        }
    }
}

public partial class TypeMeta
{
    public void Emit(StringBuilder writer, IGeneratorContext context)
    {
        if (IsUnion)
        {
            writer.AppendLine(EmitUnionTemplate(context));
            return;
        }

        if (GenerateType == GenerateType.Collection)
        {
            writer.AppendLine(EmitGenericCollectionTemplate(context));
            return;
        }

        var serializeBody = "";
        var deserializeBody = "";
        if (IsUnmanagedType)
        {
            serializeBody = $$"""
        writer.WriteUnmanaged(value);
""";
            deserializeBody = $$"""
        reader.ReadUnmanaged(out value);
""";
        }
        else
        {
            var originalMembers = Members;
            if (GenerateType == GenerateType.VersionTolerant)
            {
                // for emit time, replace padded empty
                if (Members.Length != 0)
                {
                    var maxOrder = Members.Max(x => x.Order);
                    var tempMembers = new MemberMeta[maxOrder + 1];
                    for (int i = 0; i <= maxOrder; i++)
                    {
                        tempMembers[i] = Members.FirstOrDefault(x => x.Order == i) ?? MemberMeta.CreateEmpty();
                    }
                    Members = tempMembers;
                }
            }

            serializeBody = EmitSerializeBody(context.IsForUnity);
            deserializeBody = EmitDeserializeBody();

            Members = originalMembers;
        }

        var classOrStructOrRecord = (IsRecord, IsValueType) switch
        {
            (true, true) => "record struct",
            (true, false) => "record",
            (false, true) => "struct",
            (false, false) => "class",
        };

        var nullable = IsValueType ? "" : "?";

        string staticRegisterFormatterMethod, staticMemoryPackableMethod, scopedRef, constraint, registerBody, registerT;
        if (!context.IsNet7OrGreater)
        {
            staticRegisterFormatterMethod = "public static void ";
            staticMemoryPackableMethod = "public static void ";
            scopedRef = "ref";
            constraint = context.IsForUnity ? "" : "where TBufferWriter : class, System.Buffers.IBufferWriter<byte>";
            registerBody = $"MemoryPackFormatterProvider.Register(new {Symbol.Name}Formatter());";
            registerT = "RegisterFormatter();";
        }
        else
        {
            staticRegisterFormatterMethod = $"static void IMemoryPackFormatterRegister.";
            staticMemoryPackableMethod = $"static void IMemoryPackable<{TypeName}>.";
            scopedRef = "scoped ref";
            constraint = "";
            registerBody = $"MemoryPackFormatterProvider.Register(new MemoryPack.Formatters.MemoryPackableFormatter<{TypeName}>());";
            registerT = $"MemoryPackFormatterProvider.Register<{TypeName}>();";
        }
        string serializeMethodSignarture = context.IsForUnity
            ? "Serialize(ref MemoryPackWriter"
            : "Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter>";

        writer.AppendLine($$"""
partial {{classOrStructOrRecord}} {{TypeName}} : IMemoryPackable<{{TypeName}}>
{
    static {{Symbol.Name}}()
    {
        {{registerT}}
    }

    [global::MemoryPack.Internal.Preserve]
    {{staticRegisterFormatterMethod}}RegisterFormatter()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<{{TypeName}}>())
        {
            {{registerBody}}
        }
        if (!MemoryPackFormatterProvider.IsRegistered<{{TypeName}}[]>())
        {
            MemoryPackFormatterProvider.Register(new global::MemoryPack.Formatters.ArrayFormatter<{{TypeName}}>());
        }
{{EmitAdditionalRegisterFormatter("        ", context)}}
    }

    [global::MemoryPack.Internal.Preserve]
    {{staticMemoryPackableMethod}}{{serializeMethodSignarture}} writer, {{scopedRef}} {{TypeName}}{{nullable}} value) {{constraint}}
    {
{{OnSerializing.Select(x => "        " + x.Emit()).NewLine()}}
{{serializeBody}}
    END:
{{OnSerialized.Select(x => "        " + x.Emit()).NewLine()}}
        return;
    }

    [global::MemoryPack.Internal.Preserve]
    {{staticMemoryPackableMethod}}Deserialize(ref MemoryPackReader reader, {{scopedRef}} {{TypeName}}{{nullable}} value)
    {
{{OnDeserializing.Select(x => "        " + x.Emit()).NewLine()}}
{{deserializeBody}}
    END:
{{OnDeserialized.Select(x => "        " + x.Emit()).NewLine()}}
        return;
    }
}
""");

        if (!context.IsNet7OrGreater)
        {
            // add formatter(can not use MemoryPackableFormatter)

            var code = $$"""
partial {{classOrStructOrRecord}} {{TypeName}}
{
    [global::MemoryPack.Internal.Preserve]
    sealed class {{Symbol.Name}}Formatter : MemoryPackFormatter<{{TypeName}}>
    {
        [global::MemoryPack.Internal.Preserve]
        public override void {{serializeMethodSignarture}} writer,  {{scopedRef}} {{TypeName}} value)
        {
            {{TypeName}}.Serialize(ref writer, ref value);
        }

        [global::MemoryPack.Internal.Preserve]
        public override void Deserialize(ref MemoryPackReader reader, ref {{TypeName}} value)
        {
            {{TypeName}}.Deserialize(ref reader, ref value);
        }
    }
}
""";
            writer.AppendLine(code);
        }

    }

    private string EmitDeserializeBody()
    {
        var count = Members.Length;

        var isVersionTolerant = this.GenerateType == GenerateType.VersionTolerant;
        var readBeginBody = "";
        var readEndBody = "";
        var commentOutInvalidBody = "";

        if (isVersionTolerant)
        {
            readBeginBody = """
        Span<int> deltas = stackalloc int[count];
        var delta = 0;
        for (int i = 0; i < count; i++)
        {
            deltas[i] = reader.ReadVarIntInt32();
        }
""";

            readEndBody = """
        if (count == readCount) goto END;

        for (int i = readCount; i < count; i++)
        {
            reader.Advance(deltas[i]);
        }
""";

            commentOutInvalidBody = "// ";
        }

        return $$"""
        if (!reader.TryReadObjectHeader(out var count))
        {
            value = default!;
            goto END;
        }

{{readBeginBody}}
        
{{Members.Select(x => $"        {x.MemberType.FullyQualifiedToString()} __{x.Name};").NewLine()}}

        {{(!isVersionTolerant ? "" : "var readCount = " + count + ";")}}
        if (count == {{count}})
        {
            {{(IsValueType ? "" : "if (value == null)")}}
            {
{{EmitDeserializeMembers(Members, "                ")}}

                goto NEW;
            }
{{(IsValueType ? "#if false" : "            else")}}
            {
{{Members.Select(x => $"                __{x.Name} = value.{x.Name};").NewLine()}}

{{Members.Select(x => "                " + x.EmitReadRefDeserialize(x.Order)).NewLine()}}

                goto SET;
            }
{{(IsValueType ? "#endif" : "")}}
        }
        {{commentOutInvalidBody}}else if (count > {{count}})
        {{commentOutInvalidBody}}{
            {{commentOutInvalidBody}}MemoryPackSerializationException.ThrowInvalidPropertyCount({{count}}, count);
            {{commentOutInvalidBody}}goto READ_END;
        {{commentOutInvalidBody}}}
        else
        {
            {{(IsValueType ? "" : "if (value == null)")}}
            {
{{Members.Select(x => $"               __{x.Name} = default!;").NewLine()}}
            }
{{(IsValueType ? "#if false" : "            else")}}
            {
{{Members.Select(x => $"               __{x.Name} = value.{x.Name};").NewLine()}}
            }
{{(IsValueType ? "#endif" : "")}}

            if (count == 0) goto SKIP_READ;
{{Members.Select((x, i) => "            " + x.EmitReadRefDeserialize(x.Order) + $" if (count == {i + 1}) goto SKIP_READ;").NewLine()}}

    SKIP_READ:
            {{(IsValueType ? "" : "if (value == null)")}}
            {
                goto NEW;
            }
{{(IsValueType ? "#if false" : "            else")}}            
            {
                goto SET;
            }
{{(IsValueType ? "#endif" : "")}}
        }

    SET:
        {{(!IsUseEmptyConstructor ? "goto NEW;" : "")}}
{{Members.Where(x => x.IsAssignable).Select(x => $"        {(IsUseEmptyConstructor ? "" : "// ")}value.{x.Name} = __{x.Name};").NewLine()}}
        goto READ_END;

    NEW:
        value = {{EmitConstructor()}}
        {
{{EmitDeserializeConstruction("            ")}}
        };
    READ_END:
{{readEndBody}}
""";
    }

    string EmitAdditionalRegisterFormatter(string indent, IGeneratorContext context)
    {
        var collector = new TypeCollector();
        collector.Visit(this, false);

        var types = collector.GetTypes()
            .Select(x => (x, reference.KnownTypes.GetNonDefaultFormatterName(x)))
            .Where(x => x.Item2 != null)
            .Where(x =>
            {
                if (!context.IsNet7OrGreater)
                {
                    if (x.Item2!.StartsWith("global::MemoryPack.Formatters.InterfaceReadOnlySetFormatter"))
                    {
                        return false;
                    }
                    if (x.Item2!.StartsWith("global::MemoryPack.Formatters.PriorityQueueFormatter"))
                    {
                        return false;
                    }
                }
                return true;
            })
            .ToArray();

        if (types.Length == 0) return "";

        var sb = new StringBuilder();
        foreach (var (symbol, formatter) in types)
        {
            sb.AppendLine($"{indent}if (!MemoryPackFormatterProvider.IsRegistered<{symbol.FullyQualifiedToString()}>())");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    MemoryPackFormatterProvider.Register(new {formatter}());");
            sb.AppendLine($"{indent}}}");

            // try check IsDictionary
            foreach (var item in symbol.AllInterfaces)
            {
                if (item.EqualsUnconstructedGenericType(reference.KnownTypes.System_Collections_Generic_IDictionary_T))
                {
                    var kv = string.Join(", ", item.TypeArguments.Select(x => x.FullyQualifiedToString()));
                    sb.AppendLine($"{indent}if (!MemoryPackFormatterProvider.IsRegistered<System.Collections.Generic.KeyValuePair<{kv}>>())");
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    MemoryPackFormatterProvider.Register(new MemoryPack.Formatters.KeyValuePairFormatter<{kv}>());");
                    sb.AppendLine($"{indent}}}");
                    break;
                }
            }
        }

        return sb.ToString();
    }

    string EmitSerializeBody(bool isForUnity)
    {
        if (this.GenerateType == GenerateType.VersionTolerant)
        {
            return EmitVersionTorelantSerializeBody(isForUnity);
        }

        return $$"""
{{(!IsValueType ? $$"""
        if (value == null)
        {
            writer.WriteNullObjectHeader();
            goto END;
        }
""" : "")}}

{{EmitSerializeMembers(Members, "        ", toTempWriter: false)}}
""";
    }

    string EmitVersionTorelantSerializeBody(bool isForUnity)
    {
        var newTempWriter = isForUnity
            ? "new MemoryPackWriter(ref tempBuffer, writer.Options)"
            : "new MemoryPackWriter<MemoryPack.Internal.ReusableLinkedArrayBufferWriter>(ref tempBuffer, writer.Options)";

        return $$"""
{{(!IsValueType ? $$"""
        if (value == null)
        {
            writer.WriteNullObjectHeader();
            goto END;
        }
""" : "")}}

        var tempBuffer = MemoryPack.Internal.ReusableLinkedArrayBufferWriterPool.Rent();
        try
        {
            Span<int> offsets = stackalloc int[{{Members.Length}}];
            var tempWriter = {{newTempWriter}};

{{EmitSerializeMembers(Members, "            ", toTempWriter: true)}}

            tempWriter.Flush();
            
            writer.WriteObjectHeader({{Members.Length}});
            for (int i = 0; i < {{Members.Length}}; i++)
            {
                int delta;
                if (i == 0)
                {
                    delta = offsets[i];
                }
                else
                {
                    delta = offsets[i] - offsets[i - 1];
                }
                writer.WriteVarInt(delta);
            }
            
            tempBuffer.WriteToAndReset(ref writer);
        }
        finally
        {
            ReusableLinkedArrayBufferWriterPool.Return(tempBuffer);
        }
""";
    }

    public string EmitSerializeMembers(MemberMeta[] members, string indent, bool toTempWriter)
    {
        // members is guranteed writable.
        if (members.Length == 0 && !toTempWriter)
        {
            return $"{indent}writer.WriteObjectHeader(0);";
        }

        var writer = toTempWriter ? "tempWriter" : "writer";

        var sb = new StringBuilder();
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i].Kind != MemberKind.Unmanaged)
            {
                sb.Append(indent);
                if (i == 0 && !toTempWriter)
                {
                    sb.AppendLine($"{writer}.WriteObjectHeader({Members.Length});");
                    sb.Append(indent);
                }

                sb.Append(members[i].EmitSerialize(writer));
                if (toTempWriter)
                {
                    sb.AppendLine($" offsets[{i}] = tempWriter.WrittenCount;");
                }
                else
                {
                    sb.AppendLine();
                }
                continue;
            }

            // search optimization
            var optimizeFrom = i;
            var optimizeTo = i;
            var limit = Math.Min(members.Length, i + 15);
            for (int j = i; j < limit; j++)
            {
                if (members[j].Kind == MemberKind.Unmanaged)
                {
                    optimizeTo = j;
                    continue;
                }
                else
                {
                    break;
                }
            }

            // write method
            sb.Append(indent);
            if (optimizeFrom == 0 && !toTempWriter)
            {
                sb.Append($"{writer}.WriteUnmanagedWithObjectHeader(");
                sb.Append(members.Length);
                sb.Append(", ");
            }
            else
            {
                sb.Append($"{writer}.WriteUnmanaged(");
            }

            for (int index = optimizeFrom; index <= optimizeTo; index++)
            {
                if (index != i)
                {
                    sb.Append(", ");
                }
                sb.Append("value.");
                sb.Append(members[index].Name);
            }
            sb.Append(");");

            if (toTempWriter)
            {
                sb.AppendLine($" offsets[{i}] = tempWriter.WrittenCount;");
            }
            else
            {
                sb.AppendLine();
            }

            i = optimizeTo;
        }

        return sb.ToString();
    }

    // for optimize, can use same count, value == null.
    public string EmitDeserializeMembers(MemberMeta[] members, string indent)
    {
        // {{Members.Select(x => "                " + x.EmitReadToDeserialize()).NewLine()}}
        var sb = new StringBuilder();
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i].Kind != MemberKind.Unmanaged)
            {
                sb.Append(indent);
                sb.AppendLine(members[i].EmitReadToDeserialize(i));
                continue;
            }

            // search optimization
            var optimizeFrom = i;
            var optimizeTo = i;
            var limit = Math.Min(members.Length, i + 15);
            for (int j = i; j < limit; j++)
            {
                if (members[j].Kind == MemberKind.Unmanaged)
                {
                    optimizeTo = j;
                    continue;
                }
                else
                {
                    break;
                }
            }

            // write read method
            sb.Append(indent);
            sb.Append("reader.ReadUnmanaged(");

            for (int index = optimizeFrom; index <= optimizeTo; index++)
            {
                if (index != i)
                {
                    sb.Append(", ");
                }
                sb.Append("out __");
                sb.Append(members[index].Name);
            }
            sb.AppendLine(");");

            i = optimizeTo;
        }

        return sb.ToString();
    }

    string EmitConstructor()
    {
        // noee need `;` because after using object initializer
        if (this.Constructor == null || this.Constructor.Parameters.Length == 0)
        {
            return $"new {TypeName}()";
        }
        else
        {
            var nameDict = Members.ToDictionary(x => x.Name, x => x.Name, StringComparer.OrdinalIgnoreCase);
            var parameters = this.Constructor.Parameters
                .Select(x =>
                {
                    if (nameDict.TryGetValue(x.Name, out var name))
                    {
                        return $"__{name}";
                    }
                    return null; // invalid, validated.
                })
                .Where(x => x != null);

            return $"new {TypeName}({string.Join(", ", parameters)})";
        }
    }

    string EmitDeserializeConstruction(string indent)
    {
        // all value is deserialized, __Name is exsits.
        return string.Join("," + Environment.NewLine, Members
            .Where(x => x.IsSettable && !x.IsConstructorParameter)
            .Select(x => $"{indent}{x.Name} = __{x.Name}"));
    }

    string EmitUnionTemplate(IGeneratorContext context)
    {
        var classOrInterface = Symbol.TypeKind == TypeKind.Interface ? "interface" : "class";

        var staticRegisterFormatterMethod = (context.IsNet7OrGreater)
            ? $"static void IMemoryPackFormatterRegister."
            : "public static void ";
        var register = (context.IsNet7OrGreater)
            ? $"MemoryPackFormatterProvider.Register<{TypeName}>();"
            : "RegisterFormatter();";
        var scopedRef = context.IsCSharp11OrGreater()
            ? "scoped ref"
            : "ref";
        string serializeMethodSignarture = context.IsForUnity
            ? "Serialize(ref MemoryPackWriter"
            : "Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter>";

        var code = $$"""

partial {{classOrInterface}} {{TypeName}} : IMemoryPackFormatterRegister
{
    static {{Symbol.Name}}()
    {
        {{register}}
    }

    [global::MemoryPack.Internal.Preserve]
    {{staticRegisterFormatterMethod}}RegisterFormatter()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<{{TypeName}}>())
        {
            MemoryPackFormatterProvider.Register(new {{Symbol.Name}}Formatter());
        }
        if (!MemoryPackFormatterProvider.IsRegistered<{{TypeName}}[]>())
        {
            MemoryPackFormatterProvider.Register(new global::MemoryPack.Formatters.ArrayFormatter<{{TypeName}}>());
        }
    }

    [global::MemoryPack.Internal.Preserve]
    sealed class {{Symbol.Name}}Formatter : MemoryPackFormatter<{{TypeName}}>
    {
{{EmitUnionTypeToTagField()}}

        [global::MemoryPack.Internal.Preserve]
        public override void {{serializeMethodSignarture}} writer, {{scopedRef}} {{TypeName}}? value)
        {
{{OnSerializing.Select(x => "            " + x.Emit()).NewLine()}}
{{EmitUnionSerializeBody()}}
{{OnSerialized.Select(x => "            " + x.Emit()).NewLine()}}
        }

        [global::MemoryPack.Internal.Preserve]
        public override void Deserialize(ref MemoryPackReader reader, {{scopedRef}} {{TypeName}}? value)
        {
{{OnDeserializing.Select(x => "            " + x.Emit()).NewLine()}}
{{EmitUnionDeserializeBody()}}
{{OnDeserialized.Select(x => "            " + x.Emit()).NewLine()}}            
        }
    }
}
""";

        return code;
    }

    string ToUnionTagTypeFullyQualifiedToString(INamedTypeSymbol type)
    {
        if (type.IsGenericType && this.Symbol.IsGenericType)
        {
            // when generic type, it is unconstructed.( typeof(T<>) ) so construct symbol's T
            var typeName = string.Join(", ", this.Symbol.TypeArguments.Select(x => x.FullyQualifiedToString()));
            return type.FullyQualifiedToString().Replace("<>", "<" + typeName + ">");
        }
        else
        {
            return type.FullyQualifiedToString();
        }
    }

    string EmitUnionTypeToTagField()
    {
        var elements = UnionTags.Select(x => $"            {{ typeof({ToUnionTagTypeFullyQualifiedToString(x.Type)}), {x.Tag} }},").NewLine();

        return $$"""
        static readonly System.Collections.Generic.Dictionary<Type, byte> __typeToTag = new({{UnionTags.Length}})
        {
{{elements}}
        };
""";
    }

    string EmitUnionSerializeBody()
    {
        var writeBody = UnionTags
            .Select(x =>
            {
                var method = x.Type.IsWillImplementIMemoryPackable(reference)
                    ? "WritePackable"
                    : "WriteValue";
                return $"                    case {x.Tag}: writer.{method}(System.Runtime.CompilerServices.Unsafe.As<{TypeName}?, {ToUnionTagTypeFullyQualifiedToString(x.Type)}>(ref value)); break;";
            })
            .NewLine();

        return $$"""
            if (value == null)
            {
                writer.WriteNullUnionHeader();
{{OnSerialized.Select(x => "            " + x.Emit()).NewLine()}}
                return;
            }

            if (__typeToTag.TryGetValue(value.GetType(), out var tag))
            {
                writer.WriteUnionHeader(tag);

                switch (tag)
                {
{{writeBody}}                
                    default:
                        break;
                }
            }
            else
            {
                MemoryPackSerializationException.ThrowNotFoundInUnionType(value.GetType(), typeof({{TypeName}}));
            }
""";
    }

    string EmitUnionDeserializeBody()
    {
        var readBody = UnionTags.Select(x =>
        {
            var method = x.Type.IsWillImplementIMemoryPackable(reference)
                ? "ReadPackable"
                : "ReadValue";
            return $$"""
                case {{x.Tag}}:
                    if (value is {{ToUnionTagTypeFullyQualifiedToString(x.Type)}})
                    {
                        reader.{{method}}(ref System.Runtime.CompilerServices.Unsafe.As<{{TypeName}}?, {{ToUnionTagTypeFullyQualifiedToString(x.Type)}}>(ref value));
                    }
                    else
                    {
                        value = reader.{{method}}<{{ToUnionTagTypeFullyQualifiedToString(x.Type)}}>();
                    }
                    break;
""";
        }).NewLine();


        return $$"""
            if (!reader.TryReadUnionHeader(out var tag))
            {
                value = default;
{{OnDeserialized.Select(x => "                " + x.Emit()).NewLine()}}
                return;
            }
        
            switch (tag)
            {
{{readBody}}
                default:
                    MemoryPackSerializationException.ThrowInvalidTag(tag, typeof({{TypeName}}));
                    break;
            }
""";
    }

    string EmitGenericCollectionTemplate(IGeneratorContext context)
    {
        var (collectionKind, collectionSymbol) = ParseCollectionKind(Symbol, reference);
        var methodName = collectionKind switch
        {
            CollectionKind.Collection => "Collection",
            CollectionKind.Set => "Set",
            CollectionKind.Dictionary => "Dictionary",
            _ => "",
        };

        var typeArgs = string.Join(", ", collectionSymbol!.TypeArguments.Select(x => x.FullyQualifiedToString()));

        var staticRegisterFormatterMethod = (context.IsNet7OrGreater)
            ? $"static void IMemoryPackFormatterRegister."
            : "public static void ";
        var register = (context.IsNet7OrGreater)
            ? $"MemoryPackFormatterProvider.Register<{TypeName}>();"
            : "RegisterFormatter();";

        var code = $$"""
partial class {{TypeName}} : IMemoryPackFormatterRegister
{
    static {{Symbol.Name}}()
    {
        {{register}}
    }

    {{staticRegisterFormatterMethod}}RegisterFormatter()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<{{TypeName}}>())
        {
            MemoryPackFormatterProvider.Register{{methodName}}<{{TypeName}}, {{typeArgs}}>();
        }
    }
}
""";

        return code;
    }
}

public partial class MethodMeta
{
    public string Emit()
    {
        if (IsStatic)
        {
            return $"{Name}();";
        }
        else
        {
            if (IsValueType)
            {
                return $"value.{Name}();";
            }
            else
            {
                return $"value?.{Name}();";
            }
        }
    }
}

public partial class MemberMeta
{
    public string EmitSerialize(string writer)
    {
        switch (Kind)
        {
            case MemberKind.MemoryPackable:
                return $"{writer}.WritePackable(value.{Name});";
            case MemberKind.Unmanaged:
                return $"{writer}.WriteUnmanaged(value.{Name});";
            case MemberKind.String:
                return $"{writer}.WriteString(value.{Name});";
            case MemberKind.UnmanagedArray:
                return $"{writer}.WriteUnmanagedArray(value.{Name});";
            case MemberKind.Array:
                return $"{writer}.WriteArray(value.{Name});";
            case MemberKind.Blank:
                return "";
            default:
                return $"{writer}.WriteValue(value.{Name});";
        }
    }

    public string EmitReadToDeserialize(int i)
    {
        switch (Kind)
        {
            case MemberKind.MemoryPackable:
                return $"__{Name} = reader.ReadPackable<{MemberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>();";
            case MemberKind.Unmanaged:
                return $"reader.ReadUnmanaged(out __{Name});";
            case MemberKind.String:
                return $"__{Name} = reader.ReadString();";
            case MemberKind.UnmanagedArray:
                return $"__{Name} = reader.ReadUnmanagedArray<{(MemberType as IArrayTypeSymbol)!.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>();";
            case MemberKind.Array:
                return $"__{Name} = reader.ReadArray<{(MemberType as IArrayTypeSymbol)!.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>();";
            case MemberKind.Blank:
                return $"reader.Advance(deltas[{i}]);";
            default:
                return $"__{Name} = reader.ReadValue<{MemberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>();";
        }
    }

    public string EmitReadRefDeserialize(int i)
    {
        switch (Kind)
        {
            case MemberKind.MemoryPackable:
                return $"reader.ReadPackable(ref __{Name});";
            case MemberKind.Unmanaged:
                return $"reader.ReadUnmanaged(out __{Name});";
            case MemberKind.String:
                return $"__{Name} = reader.ReadString();";
            case MemberKind.UnmanagedArray:
                return $"reader.ReadUnmanagedArray(ref __{Name});";
            case MemberKind.Array:
                return $"reader.ReadArray(ref __{Name});";
            case MemberKind.Blank:
                return $"reader.Advance(deltas[{i}]);";
            default:
                return $"reader.ReadValue(ref __{Name});";
        }
    }
}

