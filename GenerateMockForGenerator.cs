using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace MockGenereator
{
	[Generator]
	public class GenerateMockForGenerator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			context.RegisterPostInitializationOutput(static ctx =>
			{
				ctx.AddSource("GenerateMockForAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
						internal sealed class GenerateMockForAttribute : Attribute
						{
							public GenerateMockForAttribute(Type interfaceType) { InterfaceType = interfaceType; }
							public Type InterfaceType { get; }
						}
					}
					""");
			});

			var src = context.SyntaxProvider.ForAttributeWithMetadataName(
				"MockGenerator.GenerateMockForAttribute",
				static (node, _) => true,
				Transform).WithTrackingName("MockGenereator.GenerateMockForGenerator");

			context.RegisterSourceOutput(src, static (spc, source) =>
			{
				if (source == null) return;
				spc.AddSource(source.HintName, source.Code);
			});
		}

		internal sealed class Result : IEquatable<Result>
		{
			public string HintName { get; init; }
			public string Code { get; init; }

			public bool Equals(Result other) => other != null && HintName == other.HintName && Code == other.Code;
			public override bool Equals(object obj) => obj is Result s && Equals(s);
			public override int GetHashCode() => (HintName?.GetHashCode() ?? 0) ^ (Code?.GetHashCode() ?? 0);
		}

		static Result Transform(GeneratorAttributeSyntaxContext context, CancellationToken token)
		{
			var targetSymbol = (INamedTypeSymbol)context.TargetSymbol;
			var ns = targetSymbol.ContainingNamespace.IsGlobalNamespace ? null : targetSymbol.ContainingNamespace.ToString();
			var className = targetSymbol.Name;

			var interfaces = new List<INamedTypeSymbol>();
			foreach (var attr in context.Attributes)
			{
				if (attr.AttributeClass?.Name != "GenerateMockForAttribute") continue;
				if (attr.ConstructorArguments.Length == 0) continue;
				if (attr.ConstructorArguments[0].Value is INamedTypeSymbol it && it.TypeKind == TypeKind.Interface)
				{
					interfaces.Add(it);
				}
			}
			if (interfaces.Count == 0) return null;

			using var _ = StringBuilderHolder.Get(out var sb);

			var indent = ns != null ? "\t" : "";
			var memberIndent = indent + "\t";
			var classGenerics = targetSymbol.IsGenericType ? targetSymbol.TypeParameters.GenericsParams() : "";
			var classConstraints = targetSymbol.IsGenericType ? targetSymbol.TypeParameters.GenericsConstraints() : "";
			var baseList = string.Join(", ", interfaces.Select(x => x.QualifiedName()));

			if (ns != null)
			{
				sb.Append($$"""
					namespace {{ns}}
					{
						partial class {{className}}{{classGenerics}} : {{baseList}}{{classConstraints}}
						{
					""");
			}
			else
			{
				sb.Append($$"""
					partial class {{className}}{{classGenerics}} : {{baseList}}{{classConstraints}}
					{
					""");
			}

			var seenProperty = new HashSet<string>();
			var seenEvent = new HashSet<string>();
			var methods = new Utilities.MethodGroups();

			// Gather the listed interfaces plus all their (transitive) bases, deduped.
			var allIfaces = new List<INamedTypeSymbol>();
			var seenIface = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
			foreach (var iface in interfaces)
			{
				if (seenIface.Add(iface)) allIfaces.Add(iface);
				foreach (var baseIface in iface.AllInterfaces)
				{
					if (seenIface.Add(baseIface)) allIfaces.Add(baseIface);
				}
			}

			// Non-generic interfaces: flat public members (existing behavior). Generic interfaces: the
			// As{Iface}<...>() accessor scheme, so distinct closed type arguments never collide on name.
			var emittedSlots = new HashSet<string>();
			var genericDefs = new List<INamedTypeSymbol>();
			var genericClosed = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
			foreach (var iface in allIfaces)
			{
				if (iface.OriginalDefinition.TypeParameters.Length > 0)
				{
					var def = iface.OriginalDefinition;
					if (!genericClosed.TryGetValue(def, out var list))
					{
						list = new List<INamedTypeSymbol>();
						genericClosed[def] = list;
						genericDefs.Add(def);
					}
					if (!list.Any(x => SymbolEqualityComparer.Default.Equals(x, iface)))
					{
						list.Add(iface);
					}
				}
				else
				{
					EmitInterface(sb, memberIndent, iface, methods, seenProperty, seenEvent);
				}
			}

			methods.EmitAll(sb, memberIndent);

			foreach (var def in genericDefs)
			{
				EmitGenericAccessor(sb, memberIndent, def, genericClosed[def], emittedSlots);
			}

			if (ns != null)
			{
				sb.Append(@"
	}
}
");
			}
			else
			{
				sb.Append(@"
}
");
			}

			var hint = (ns == null ? "" : ns + ".") + className + ".GenerateMockFor.cs";
			return new Result { HintName = hint, Code = sb.ToString() };
		}

		static void EmitInterface(StringBuilder sb, string i, INamedTypeSymbol iface, Utilities.MethodGroups methods, HashSet<string> seenProperty, HashSet<string> seenEvent)
		{
			foreach (var member in iface.GetMembers())
			{
				switch (member)
				{
					case IPropertySymbol p:
						if (p.IsIndexer) break;
						if (!seenProperty.Add(p.Name)) break;
						{
							var rawType = p.Type.QualifiedName();
							var pascal = char.ToUpper(p.Name[0]) + p.Name.Substring(1);
							var camel = char.ToLower(p.Name[0]) + p.Name.Substring(1);
							var backing = "_" + camel + "Backing";
							var onSet = "On" + pascal + "Set";
							sb.Append($"\n{i}public System.Action<{rawType}> {onSet} {{ get; set; }}");
							sb.Append($"\n{i}private {rawType} {backing};");
							sb.Append($"\n{i}public {rawType} {p.Name} {{ get => {backing}; set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
						}
						break;
					case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
						methods.Add(m.Name, m);
						break;
					case IEventSymbol e:
						if (!seenEvent.Add(e.Name)) break;
						sb.EmitMockEventMember(i, e);
						break;
				}
			}
		}

		/// <summary>
		/// Emits the accessor scheme for one generic interface definition: a nested
		/// <c>{Iface}Accessor&lt;...&gt;</c> holding mock storage, an <c>As{Iface}&lt;...&gt;()</c> entry
		/// resolving the accessor for a closed type argument set via <c>typeof</c>, and one explicit interface
		/// implementation per implemented closed instantiation forwarding to that accessor. There is no public
		/// mirror (unlike GenerateMockView) because GenerateMockFor synthesizes the implementation itself, and
		/// explicit impls keep distinct closed instantiations from colliding. Accessor properties follow the
		/// GenerateMockFor convention (always <c>On{Name}Set</c> Action + get/set storage).
		/// </summary>
		static void EmitGenericAccessor(StringBuilder sb, string i, INamedTypeSymbol def, List<INamedTypeSymbol> closedList, HashSet<string> emittedSlots)
		{
			var inner = i + "\t";
			var accName = def.Name + "Accessor";
			var asName = "As" + def.Name;
			var tparams = def.TypeParameters.GenericsParams();
			var tconstraints = def.TypeParameters.GenericsConstraints();
			var mapField = "_" + char.ToLower(def.Name[0]) + def.Name.Substring(1) + "AccessorMap";

			// Accessor type + As<...>() entry: once per generic interface definition.
			if (emittedSlots.Add("ACC:" + def.QualifiedName()))
			{
				sb.Append($"\n{i}public sealed class {accName}{tparams}{tconstraints}");
				sb.Append($"\n{i}{{");
				var accMethods = new Utilities.MethodGroups();
				foreach (var member in def.GetMembers())
				{
					switch (member)
					{
						case IPropertySymbol p when !p.IsIndexer:
							{
								var rawType = p.Type.QualifiedName();
								var pascal = char.ToUpper(p.Name[0]) + p.Name.Substring(1);
								var camel = char.ToLower(p.Name[0]) + p.Name.Substring(1);
								var backing = "_" + camel + "Backing";
								var onSet = "On" + pascal + "Set";
								sb.Append($"\n{inner}public System.Action<{rawType}> {onSet} {{ get; set; }}");
								sb.Append($"\n{inner}private {rawType} {backing};");
								sb.Append($"\n{inner}public {rawType} {p.Name} {{ get => {backing}; set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
							}
							break;
						case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
							accMethods.Add(m.Name, m);
							break;
						case IEventSymbol e:
							sb.EmitMockEventMember(inner, e);
							break;
					}
				}
				accMethods.EmitAll(sb, inner);
				sb.Append($"\n{i}}}");

				sb.Append($"\n{i}private readonly System.Collections.Generic.Dictionary<System.Type, object> {mapField} = new System.Collections.Generic.Dictionary<System.Type, object>();");
				sb.Append($"\n{i}public {accName}{tparams} {asName}{tparams}(){tconstraints}");
				sb.Append($"\n{i}{{");
				sb.Append($"\n{inner}if (!{mapField}.TryGetValue(typeof({accName}{tparams}), out var __a))");
				sb.Append($"\n{inner}{{");
				sb.Append($"\n{inner}\t__a = new {accName}{tparams}();");
				sb.Append($"\n{inner}\t{mapField}[typeof({accName}{tparams})] = __a;");
				sb.Append($"\n{inner}}}");
				sb.Append($"\n{inner}return ({accName}{tparams})__a;");
				sb.Append($"\n{i}}}");
			}

			// Routing: explicit interface implementation per closed instantiation member.
			foreach (var closed in closedList)
			{
				var asCall = $"{asName}{closed.TypeArguments.GenericArgs()}()";
				var target = closed.QualifiedName();
				foreach (var member in closed.GetMembers())
				{
					switch (member)
					{
						case IPropertySymbol p when !p.IsIndexer:
							{
								if (!emittedSlots.Add("GPX:" + target + "." + p.Name)) break;
								var t = p.Type.QualifiedName();
								var hasGet = p.GetMethod != null;
								var hasSet = p.SetMethod != null;
								if (hasGet && hasSet)
									sb.Append($"\n{i}{t} {target}.{p.Name} {{ get => {asCall}.{p.Name}; set => {asCall}.{p.Name} = value; }}");
								else if (hasGet)
									sb.Append($"\n{i}{t} {target}.{p.Name} => {asCall}.{p.Name};");
								else
									sb.Append($"\n{i}{t} {target}.{p.Name} {{ set => {asCall}.{p.Name} = value; }}");
							}
							break;
						case IEventSymbol e:
							{
								if (!emittedSlots.Add("GEX:" + target + "." + e.Name)) break;
								var et = e.Type.QualifiedName();
								sb.Append($"\n{i}event {et} {target}.{e.Name} {{ add => {asCall}.{e.Name} += value; remove => {asCall}.{e.Name} -= value; }}");
							}
							break;
						case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
							{
								var g = m.TypeParameters.GenericsParams();
								var prmsNoDef = m.MethodParams();
								if (!emittedSlots.Add("GMX:" + target + "." + m.Name + g + prmsNoDef)) break;
								// Explicit interface implementations must NOT restate generic constraints (CS0460).
								var ret = m.ReturnTypeName();
								var args = m.MethodArgs();
								sb.Append($"\n{i}{ret} {target}.{m.Name}{g}{prmsNoDef} => {asCall}.{m.Name}{g}{args};");
							}
							break;
					}
				}
			}
		}
	}
}
