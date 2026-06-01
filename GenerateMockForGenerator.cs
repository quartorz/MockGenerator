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

			var seenMethodFunc = new HashSet<string>();
			var seenProperty = new HashSet<string>();
			var seenEvent = new HashSet<string>();

			foreach (var iface in interfaces)
			{
				EmitInterface(sb, memberIndent, iface, seenMethodFunc, seenProperty, seenEvent);
				foreach (var baseIface in iface.AllInterfaces)
				{
					EmitInterface(sb, memberIndent, baseIface, seenMethodFunc, seenProperty, seenEvent);
				}
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

		static void EmitInterface(StringBuilder sb, string i, INamedTypeSymbol iface, HashSet<string> seenMethodFunc, HashSet<string> seenProperty, HashSet<string> seenEvent)
		{
			foreach (var member in iface.GetMembers())
			{
				switch (member)
				{
					case IPropertySymbol p:
						if (p.IsIndexer) break;
						if (!seenProperty.Add(p.Name)) break;
						sb.Append($"\n{i}public {p.Type.QualifiedName()} {p.Name} {{ get; set; }}");
						break;
					case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
						if (!seenMethodFunc.Add(m.Name + "Func"))
						{
							sb.Append($"\n{i}#warning GenerateMockFor: overload for '{m.Name}' was skipped (Func name '{m.Name}Func' already used)");
							break;
						}
						sb.EmitMockMethodMember(i, m);
						break;
					case IEventSymbol e:
						if (!seenEvent.Add(e.Name)) break;
						sb.EmitMockEventMember(i, e);
						break;
				}
			}
		}
	}
}
